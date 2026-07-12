<#
.SYNOPSIS
  Public-repo scrub scan for the DevTools provenance (W4) area: fails if any forbidden internal token
  appears in the source, harness, docs, or reference corpus.

.DESCRIPTION
  This repo is public-bound. The following must never appear in W4 files:
    MCP, SM- (internal probe label), clean-room, nikolame, win-devex, FrameworkUdk, InitializeXaml
  Deliberately NOT forbidden (legitimate/public, and required by the domain): census, xamlOM.h,
  DisableXbfLineInfo, and the branch name 'winui-devex' (which does not contain 'win-devex').

.PARAMETER Root
  Directory to scan. Defaults to the winapp-devtools area (the parent of this script's folder).

.EXAMPLE
  pwsh src/winapp-devtools/scripts/scrub-scan.ps1
#>
[CmdletBinding()]
param(
    [string]$Root,
    [string[]]$ExtraPaths
)

$ErrorActionPreference = 'Stop'
$Root = if ($Root) { $Root } else { Resolve-Path (Join-Path $PSScriptRoot '..') }

# W4-owned files that live outside the winapp-devtools area (repo root is three levels up from here).
if (-not $ExtraPaths) {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
    $ExtraPaths = @(
        (Join-Path $repoRoot '.github\workflows\devtools-provenance.yml'),
        (Join-Path $repoRoot 'specs\winapp-devtools-provenance.md')
    ) | Where-Object { Test-Path $_ }
}

# Case-sensitive patterns (acronyms / labels that must match exactly to avoid false positives).
$caseSensitive = @('MCP', 'SM-', 'FrameworkUdk', 'InitializeXaml')
# Case-insensitive patterns (names / phrases).
$caseInsensitive = @('clean-room', 'nikolame', 'win-devex')

# Skip build output and this script itself (it necessarily names the tokens it forbids).
$skip = '\\(bin|obj)\\|scrub-scan\.ps1$'

$files = Get-ChildItem -Path $Root -Recurse -File |
    Where-Object { $_.FullName -notmatch $skip }

# Append the W4-owned files that live outside $Root (deduped by full path).
$files = @($files)
foreach ($p in $ExtraPaths) {
    $item = Get-Item -LiteralPath $p -ErrorAction SilentlyContinue
    if ($item -and ($files.FullName -notcontains $item.FullName)) { $files += $item }
}

$hits = [System.Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }

    foreach ($pat in $caseSensitive) {
        if ($text -cmatch [regex]::Escape($pat)) {
            $hits.Add("$($file.FullName): '$pat' (case-sensitive)")
        }
    }
    foreach ($pat in $caseInsensitive) {
        if ($text -imatch [regex]::Escape($pat)) {
            $hits.Add("$($file.FullName): '$pat' (case-insensitive)")
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Host "SCRUB SCAN FAILED - $($hits.Count) forbidden token hit(s):" -ForegroundColor Red
    $hits | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "SCRUB SCAN CLEAN - 0 forbidden tokens across $($files.Count) files under $Root" -ForegroundColor Green
exit 0
