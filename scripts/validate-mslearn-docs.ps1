#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate winapp CLI source docs against MS Learn publishing rules.
.DESCRIPTION
    Enforces the docs-repo conventions that the MicrosoftDocs/windows-dev-docs-pr
    review (including the Copilot reviewer) checks, so that ported docs are clean
    BEFORE a PR is opened. Runs against the hand-authored source under docs/ — the
    same files port-mslearn-docs.ps1 consumes — so failures are fixed at the source
    of truth rather than in the generated output.

    HARD-FAIL checks (exit code 1):
      * Every doc opted into MS Learn (<!-- mslearn: true -->) must resolve a
        front-matter description that is:
          - present via <!-- description: ... --> (not defaulted to the title)
          - not equal to the page title
          - 115–145 characters
          - free of YAML-special characters that would force the value to be quoted
      * No banned marketing words anywhere in the doc (title, prose, or sample text).

    WARN-only checks (reported, non-fatal):
      * Blockquotes used as callouts (a "> **Bold lead-in**") that don't use MS Learn
        alert syntax (> [!NOTE] / [!IMPORTANT] / [!TIP] / [!WARNING] / [!CAUTION]).
        Converting these is a judgement call, so they are surfaced but never fail CI.

    Keep this in sync with Get-FrontMatter in port-mslearn-docs.ps1 (description
    resolution + quoting regex).
.PARAMETER DocsRoot
    Root of the docs tree to validate (default: <repo>/docs).
.EXAMPLE
    ./scripts/validate-mslearn-docs.ps1
#>

param(
    [string]$DocsRoot = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
if (-not $DocsRoot) { $DocsRoot = Join-Path $ProjectRoot "docs" }

# ─── Rules (keep in sync with port-mslearn-docs.ps1) ────────────────────────────

$DescriptionMin = 115
$DescriptionMax = 145

# Characters that Get-FrontMatter would quote — a resolved description containing
# any of these renders as a quoted YAML scalar, which the docs reviewer flags.
$YamlSpecialPattern = '[:\[\]{}#&*!|>''"%@`]'

# Marketing / promotional words disallowed by the docs style guidance. Scanned
# across the whole file (including sample strings, which is where they hide).
$BannedWords = @(
    'powerful', 'seamless', 'seamlessly', 'cutting-edge', 'world-class',
    'revolutionary', 'effortless', 'effortlessly', 'blazing', 'game-changing',
    'best-in-class', 'state-of-the-art', 'unleash', 'supercharge', 'robust'
)

$AlertPattern = '^\s*>\s*\[!(NOTE|IMPORTANT|TIP|WARNING|CAUTION)\]'
$CalloutPattern = '^\s*>\s*\*\*'   # blockquote whose first line starts with bold text

# ─── Helpers ────────────────────────────────────────────────────────────────────

$errors = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Add-Error { param([string]$File, [string]$Msg) $errors.Add("$File`: $Msg") }
function Add-Warning { param([string]$File, [string]$Msg) $warnings.Add("$File`: $Msg") }

# Blank out fenced code blocks (preserving line count so reported line numbers
# stay aligned with the source) so structural checks ignore code. Marketing
# scanning intentionally uses the raw content instead.
function Remove-CodeFences {
    param([string]$Content)
    return [regex]::Replace($Content, '(?ms)```.*?```', {
        param($m)
        # Keep one blank line per source line the block spanned.
        "`n" * ([regex]::Matches($m.Value, "`n").Count)
    })
}

# ─── Validate ───────────────────────────────────────────────────────────────────

$docFiles = Get-ChildItem $DocsRoot -Recurse -File -Filter *.md
$checked = 0

foreach ($file in $docFiles) {
    $relPath = $file.FullName.Substring($ProjectRoot.Length + 1) -replace '\\', '/'
    $raw = Get-Content $file.FullName -Raw

    # Opt-in marker (match the first-10-lines window port-mslearn-docs.ps1 uses)
    $head = ($raw -split "`n" | Select-Object -First 10) -join "`n"
    if ($head -notmatch '<!--\s*mslearn:\s*true\s*-->') { continue }
    $checked++

    # Title = first H1
    $title = $null
    if ($raw -match '(?m)^#\s+(.+?)\s*$') { $title = $Matches[1].Trim() }
    if (-not $title) {
        Add-Error $relPath "no H1 title found (required to derive front matter)."
        continue
    }

    # Resolve description exactly like Get-FrontMatter: marker override, else title.
    $hasMarker = $raw -match '<!--\s*description:\s*(.+?)\s*-->'
    $description = if ($hasMarker) { $Matches[1].Trim() } else { $title }

    if (-not $hasMarker -or $description -eq $title) {
        Add-Error $relPath "description is missing or duplicates the title; add a <!-- description: ... --> marker distinct from the H1."
    }
    else {
        $len = $description.Length
        if ($len -lt $DescriptionMin -or $len -gt $DescriptionMax) {
            Add-Error $relPath "description length $len is outside $DescriptionMin-$DescriptionMax characters."
        }
        if ($description -match $YamlSpecialPattern) {
            Add-Error $relPath "description contains a YAML-special character (would be emitted as a quoted value); reword to avoid $YamlSpecialPattern."
        }
    }

    # Banned marketing words (whole-file, case-insensitive, word-boundary)
    foreach ($word in $BannedWords) {
        if ($raw -match "(?i)\b$([regex]::Escape($word))\b") {
            Add-Error $relPath "contains banned marketing word '$word'; use neutral, instructional language."
        }
    }

    # Blockquote-as-callout (warn only)
    $body = Remove-CodeFences $raw
    $lines = $body -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $CalloutPattern) {
            $prev = if ($i -gt 0) { $lines[$i - 1] } else { '' }
            if ($prev -notmatch $AlertPattern) {
                Add-Warning $relPath "blockquote callout on line $($i + 1) does not use MS Learn alert syntax ([!NOTE]/[!IMPORTANT]/[!TIP]/[!WARNING]/[!CAUTION])."
            }
        }
    }
}

# ─── Report ─────────────────────────────────────────────────────────────────────

Write-Host "`nValidated $checked MS Learn doc(s) under $DocsRoot" -ForegroundColor Cyan

if ($warnings.Count -gt 0) {
    Write-Host "`nWarnings ($($warnings.Count)):" -ForegroundColor Yellow
    foreach ($w in $warnings) { Write-Host "  [warn] $w" -ForegroundColor Yellow }
}

if ($errors.Count -gt 0) {
    Write-Host "`nErrors ($($errors.Count)):" -ForegroundColor Red
    foreach ($e in $errors) { Write-Host "  [error] $e" -ForegroundColor Red }
    Write-Host "`nMS Learn doc validation FAILED." -ForegroundColor Red
    exit 1
}

Write-Host "MS Learn doc validation passed." -ForegroundColor Green
exit 0
