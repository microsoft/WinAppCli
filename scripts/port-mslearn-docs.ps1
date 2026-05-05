#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Port winapp CLI docs to MS Learn format
.DESCRIPTION
    Transforms documentation from this repo into MS Learn-ready format:
    1. Copies mapped doc files to an output directory mirroring the docs repo structure
    2. Adds YAML front matter (title, description, ms.date, ms.topic)
    3. Rewrites links using a canonical source→destination map
    4. Flattens electron subfolder guides
    5. Copies referenced images
    6. Generates guides/index.md

    The output directory can be directly committed to MicrosoftDocs/windows-dev-docs-pr
    under hub/apps/dev-tools/winapp-cli/.
.PARAMETER OutputPath
    Output directory for ported docs (default: artifacts/mslearn-docs)
.PARAMETER Version
    Version label for metadata (default: from version.json)
.EXAMPLE
    .\scripts\port-mslearn-docs.ps1
.EXAMPLE
    .\scripts\port-mslearn-docs.ps1 -OutputPath "./my-output"
#>

param(
    [string]$OutputPath = "",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
$DocsRoot = Join-Path $ProjectRoot "docs"

# ─── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step  { param([string]$msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Info  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Gray }
function Write-Ok    { param([string]$msg) Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Yellow }

# ─── Configuration ──────────────────────────────────────────────────────────────

$GitHubRepoBase = "https://github.com/microsoft/WinAppCli"

# Paths under docs/ to exclude from porting (relative to docs/, forward slash)
# These are always excluded regardless of mslearn marker (non-doc files, generated content)
$AlwaysExcludePaths = @(
    "cli-schema.json"
    "fragments/"
)

# Front matter overrides: output path → { description, topic }
# Falls back to auto-detection if not specified.
# Only needed for pages where the first paragraph isn't a good description.
$FrontMatterOverrides = @{
    "index.md" = @{
        description = "The Windows App Development CLI (winapp CLI) is a command-line interface for managing Windows SDKs, packaging, generating app identity, manifests, certificates, and using build tools with any app framework."
        topic       = "overview"
    }
    "usage.md" = @{
        description = "Complete command reference for the Windows App Development CLI (winapp CLI) including setup, packaging, identity, certificates, signing, and utility commands."
        topic       = "reference"
    }
    "ui-automation.md" = @{
        description = "Inspect and interact with running Windows application UIs from the command line using winapp CLI UI automation commands."
        topic       = "reference"
    }
}

# Known repo-only link targets (not under docs/, linked to GitHub)
$RepoOnlyLinkTargets = @(
    "samples/dotnet-app"
    "samples/wpf-app"
    "samples/cpp-app"
    "samples/electron"
    "samples/electron-winml"
    "samples/electron-winml/winMlAddon"
    "samples/electron-winml/winMlAddon/addon.cs"
    "samples/electron-winml/src/index.js"
    "samples/rust-app"
    "samples/tauri-app"
    "samples/flutter-app"
)

# ─── Step 0: Resolve parameters ────────────────────────────────────────────────

if (-not $OutputPath) {
    $OutputPath = Join-Path $ProjectRoot "artifacts\mslearn-docs"
}

if (-not $Version) {
    $versionJson = Get-Content (Join-Path $ProjectRoot "version.json") -Raw | ConvertFrom-Json
    $Version = $versionJson.version
}

$msDate = Get-Date -Format "MM/dd/yyyy"

Write-Step "Porting docs for v$Version"
Write-Info "Output: $OutputPath"

# ─── Step 1: Auto-discover files to port ────────────────────────────────────────

Write-Step "Discovering docs to port"

# Find all .md files under docs/
$allDocFiles = Get-ChildItem (Join-Path $ProjectRoot "docs") -Recurse -File |
    Where-Object { $_.Extension -eq ".md" -or $_.Extension -eq ".png" -or $_.Extension -eq ".jpg" -or $_.Extension -eq ".gif" }

# Build file mapping by auto-discovery
$FileMapping = [ordered]@{}
$ImageMapping = [ordered]@{}

foreach ($file in $allDocFiles) {
    $repoRelPath = $file.FullName.Substring($ProjectRoot.Length + 1) -replace '\\', '/'
    $docsRelPath = $repoRelPath.Substring("docs/".Length)  # path relative to docs/

    # Handle images first — they're always included if referenced by ported docs
    if ($file.Extension -in @(".png", ".jpg", ".gif")) {
        $parentDir = ($docsRelPath -replace '[^/]+$', '').TrimEnd('/')
        if ($parentDir -eq "images") {
            $destRel = "media/$($file.Name)"
        } elseif ($parentDir -match '^(.*)/images$') {
            $destRel = "$($Matches[1])/media/$($file.Name)"
        } else {
            $destRel = "$parentDir/media/$($file.Name)"
        }
        $ImageMapping[$repoRelPath] = $destRel
        continue
    }

    # Check always-excluded paths (non-doc content like cli-schema.json, fragments/)
    $excluded = $false
    foreach ($excl in $AlwaysExcludePaths) {
        if ($excl.EndsWith('/')) {
            if ($docsRelPath.StartsWith($excl)) { $excluded = $true; break }
        } else {
            if ($docsRelPath -eq $excl) { $excluded = $true; break }
        }
    }
    if ($excluded) { continue }

    # Check for <!-- mslearn: true --> marker (opt-in model)
    $fileHead = Get-Content $file.FullName -TotalCount 5 -ErrorAction SilentlyContinue | Out-String
    if ($fileHead -notmatch '<!--\s*mslearn:\s*true\s*-->') {
        continue  # not marked for MS Learn
    }

    # Compute output path
    # Special case: README.md → index.md
    if ($docsRelPath -eq "README.md") {
        $destRel = "index.md"
    }
    # Flatten electron subfolder: guides/electron/foo.md → guides/electron-foo.md
    elseif ($docsRelPath -match '^guides/electron/(.+)$') {
        $destRel = "guides/electron-$($Matches[1])"
    }
    else {
        $destRel = $docsRelPath
    }

    $FileMapping[$repoRelPath] = $destRel
    Write-Info "  $repoRelPath -> $destRel"
}

Write-Info "Discovered $($FileMapping.Count) doc files + $($ImageMapping.Count) images"

# ─── Step 2: Prepare output directory ───────────────────────────────────────────

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputPath "guides") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputPath "guides\media") -Force | Out-Null

# ─── Step 3: Build the canonical link resolution map ────────────────────────────

Write-Step "Building link resolution map"

# Map from repo-relative source path → output-relative path (for files we port)
# Map from repo-relative source path → GitHub URL (for files we don't port)
$LinkMap = @{}

# Add all ported files
foreach ($entry in $FileMapping.GetEnumerator()) {
    $LinkMap[$entry.Key] = @{ Type = "ported"; Path = $entry.Value }
}

# Add image files
foreach ($entry in $ImageMapping.GetEnumerator()) {
    $LinkMap[$entry.Key] = @{ Type = "ported"; Path = $entry.Value }
}

# Add known repo-only link targets (not under docs/, linked to GitHub)
foreach ($path in $RepoOnlyLinkTargets) {
    $isFile = $path -match '\.\w+$'
    $ghPath = if ($isFile) { "$GitHubRepoBase/blob/main/$path" } else { "$GitHubRepoBase/tree/main/$path" }
    $LinkMap[$path] = @{ Type = "github"; Url = $ghPath }
}

# Add excluded docs as GitHub links (so links to them still work)
foreach ($excl in $AlwaysExcludePaths) {
    if ($excl.EndsWith('/')) { continue }  # skip directory exclusions
    $exclRepoPath = "docs/$excl"
    if (-not $LinkMap.ContainsKey($exclRepoPath)) {
        $LinkMap[$exclRepoPath] = @{ Type = "github"; Url = "$GitHubRepoBase/blob/main/$exclRepoPath" }
    }
}

Write-Info "Map has $($LinkMap.Count) entries ($($FileMapping.Count) ported + $($ImageMapping.Count) images, $($RepoOnlyLinkTargets.Count) repo-only)"

# ─── Link resolution function ──────────────────────────────────────────────────

function Resolve-DocLink {
    param(
        [string]$Href,
        [string]$SourceRepoPath  # repo-relative path of the file containing the link
    )

    # Skip external URLs, anchors-only, mailto, and MS Learn absolute paths
    if ($Href -match '^https?://' -or $Href -match '^mailto:' -or $Href -match '^#') {
        return $null  # null = leave unchanged
    }

    # MS Learn cross-docs relative paths (start with /windows/, /dotnet/, etc.) — leave as-is
    if ($Href -match '^/') {
        return $null
    }

    # Separate anchor from path
    $anchor = ""
    if ($Href -match '^([^#]*)(.*)$') {
        $hrefPath = $Matches[1]
        $anchor = $Matches[2]
    }

    if (-not $hrefPath) {
        return $null  # anchor-only link
    }

    # Resolve relative path against source file's directory
    $sourceDir = ($SourceRepoPath -replace '[^/]+$', '').TrimEnd('/')
    $resolved = "$sourceDir/$hrefPath" -replace '\\', '/'

    # Normalize: resolve ../ segments
    $parts = $resolved -split '/'
    $normalized = [System.Collections.Generic.List[string]]::new()
    foreach ($part in $parts) {
        if ($part -eq '..') {
            if ($normalized.Count -gt 0) {
                $normalized.RemoveAt($normalized.Count - 1)
            }
        }
        elseif ($part -ne '.' -and $part -ne '') {
            $normalized.Add($part)
        }
    }
    $repoPath = ($normalized -join '/').TrimEnd('/')

    # Look up in the map
    if ($LinkMap.ContainsKey($repoPath)) {
        $target = $LinkMap[$repoPath]
        if ($target.Type -eq "ported") {
            # Compute relative path from source output file to target output file
            $sourceOutputPath = $FileMapping[$SourceRepoPath]
            $sourceOutputDir = ($sourceOutputPath -replace '[^/]+$', '').TrimEnd('/')
            $targetOutputPath = $target.Path

            # Compute relative path
            $sourceParts = @(($sourceOutputDir -split '/') | Where-Object { $_ })
            $targetParts = @(($targetOutputPath -split '/') | Where-Object { $_ })

            # Find common prefix length
            $commonLen = 0
            for ($i = 0; $i -lt [Math]::Min($sourceParts.Count, $targetParts.Count - 1); $i++) {
                if ($sourceParts[$i] -eq $targetParts[$i]) { $commonLen++ } else { break }
            }

            # Build relative path
            $upCount = $sourceParts.Count - $commonLen
            $relParts = @()
            for ($i = 0; $i -lt $upCount; $i++) { $relParts += ".." }
            for ($i = $commonLen; $i -lt $targetParts.Count; $i++) { $relParts += $targetParts[$i] }

            $relPath = ($relParts -join '/')
            if (-not $relPath) { $relPath = "." }
            return "$relPath$anchor"
        }
        elseif ($target.Type -eq "github") {
            return "$($target.Url)$anchor"
        }
    }

    # Not in map — check if it looks like a repo path and convert to GitHub URL
    if ($repoPath -and -not ($repoPath -match '^\w+://')) {
        Write-Warn "  Unmapped link in $SourceRepoPath : $Href (resolved: $repoPath)"
        $isFile = $repoPath -match '\.\w+$'
        $ghBase = if ($isFile) { "$GitHubRepoBase/blob/main" } else { "$GitHubRepoBase/tree/main" }
        return "$ghBase/$repoPath$anchor"
    }

    return $null
}

# ─── learn.microsoft.com URL rewriter ──────────────────────────────────────────

function Rewrite-LearnUrls {
    param([string]$Content)

    # Convert https://learn.microsoft.com[/en-us]/path/... → /path/...
    $Content = [regex]::Replace($Content, 'https://learn\.microsoft\.com(?:/en-us)?((?:/[\w-]+)+[^\s\)\]"]*)', '$1')

    return $Content
}

# ─── Front matter generator ────────────────────────────────────────────────────

function Get-FrontMatter {
    param(
        [string]$Content,
        [string]$DestRelPath
    )

    # Extract title from first # heading
    $title = "winapp CLI"
    if ($Content -match '(?m)^#\s+(.+)$') {
        $title = $Matches[1].Trim()
    }

    # Get overrides
    $overrides = $FrontMatterOverrides[$DestRelPath]
    $description = if ($overrides -and $overrides.description) { $overrides.description }
                   else { $title }
    $topic = if ($overrides -and $overrides.topic) { $overrides.topic }
             else { "how-to" }

    # Quote values that contain YAML-special characters (colons, brackets, etc.)
    $safeTitle = if ($title -match '[:\[\]{}#&*!|>''"%@`]') { "`"$($title -replace '"', '\"')`"" } else { $title }
    $safeDesc  = if ($description -match '[:\[\]{}#&*!|>''"%@`]') { "`"$($description -replace '"', '\"')`"" } else { $description }

    $lines = @(
        "---"
        "title: $safeTitle"
        "description: $safeDesc"
        "ms.date: $msDate"
        "ms.topic: $topic"
        "---"
        ""
    )
    return ($lines -join "`r`n") + "`r`n"
}

# ─── Step 3: Process and copy files ─────────────────────────────────────────────

Write-Step "Processing files"

foreach ($entry in $FileMapping.GetEnumerator()) {
    $sourcePath = Join-Path $ProjectRoot ($entry.Key -replace '/', '\')
    $destRelPath = $entry.Value
    $destPath = Join-Path $OutputPath ($destRelPath -replace '/', '\')
    $destPath = [System.IO.Path]::GetFullPath($destPath)
    if (-not (Test-Path $sourcePath)) {
        Write-Warn "  MISSING: $($entry.Key) — skipping"
        continue
    }

    # Read content
    $content = Get-Content $sourcePath -Raw

    # Strip the mslearn marker comment
    $content = $content -replace '(?m)^\s*<!--\s*mslearn:\s*true\s*-->\s*\r?\n?', ''

    # Rewrite learn.microsoft.com URLs to relative paths
    $content = Rewrite-LearnUrls $content

    # Protect code blocks from link rewriting by temporarily replacing them
    $codeBlocks = [System.Collections.Generic.List[string]]::new()
    $content = [regex]::Replace($content, '(?ms)(```[^\n]*\n.*?```)', {
        param($match)
        $idx = $codeBlocks.Count
        $codeBlocks.Add($match.Value)
        return "%%CODEBLOCK_${idx}%%"
    })

    # Protect inline code spans from placeholder escaping
    $inlineCode = [System.Collections.Generic.List[string]]::new()
    $content = [regex]::Replace($content, '(`[^`]+`)', {
        param($match)
        $idx = $inlineCode.Count
        $inlineCode.Add($match.Value)
        return "%%INLINECODE_${idx}%%"
    })

    # Escape bare <placeholder> patterns that MS Learn treats as HTML tags.
    # Matches <word>, <word-word>, <word word> not already inside backticks or code blocks.
    $content = [regex]::Replace($content, '<([\w][\w\s-]*)>', '&lt;$1&gt;')

    # Rewrite image links first (before regular links, since ![...]() also matches [...]() regex)
    $content = [regex]::Replace($content, '!\[([^\]]*)\]\(([^)]+)\)', {
        param($match)
        $alt = $match.Groups[1].Value
        $href = $match.Groups[2].Value
        $newHref = Resolve-DocLink -Href $href -SourceRepoPath $entry.Key
        if ($null -ne $newHref) {
            return "![$alt]($newHref)"
        }
        return $match.Value
    })

    # Rewrite markdown links (excluding images which start with !)
    $content = [regex]::Replace($content, '(?<!!)(\[([^\]]*)\]\(([^)]+)\))', {
        param($match)
        $text = $match.Groups[2].Value
        $href = $match.Groups[3].Value
        $newHref = Resolve-DocLink -Href $href -SourceRepoPath $entry.Key
        if ($null -ne $newHref) {
            return "[$text]($newHref)"
        }
        return $match.Groups[1].Value
    })

    # Restore inline code spans
    for ($i = 0; $i -lt $inlineCode.Count; $i++) {
        $content = $content.Replace("%%INLINECODE_${i}%%", $inlineCode[$i])
    }

    # Restore code blocks
    for ($i = 0; $i -lt $codeBlocks.Count; $i++) {
        $content = $content.Replace("%%CODEBLOCK_${i}%%", $codeBlocks[$i])
    }

    # Add YAML front matter
    $frontMatter = Get-FrontMatter -Content $content -DestRelPath $destRelPath
    $content = $frontMatter + $content

    # Write to output
    $destDir = Split-Path $destPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Set-Content -Path $destPath -Value $content -Encoding UTF8 -NoNewline

    Write-Info "  $($entry.Key) -> $destRelPath"
}

# ─── Step 4: Copy images (only those referenced by ported docs) ────────────────

Write-Step "Copying referenced images"

# Collect all written markdown content to check image references
$allPortedContent = Get-ChildItem $OutputPath -Recurse -File -Filter "*.md" | ForEach-Object {
    Get-Content $_.FullName -Raw
} | Out-String

foreach ($entry in $ImageMapping.GetEnumerator()) {
    $sourcePath = Join-Path $ProjectRoot ($entry.Key -replace '/', '\')
    $destPath = Join-Path $OutputPath ($entry.Value -replace '/', '\')
    $imageName = Split-Path $entry.Value -Leaf

    # Only copy if the image filename appears in any ported doc
    if ($allPortedContent -notmatch [regex]::Escape($imageName)) {
        Write-Info "  SKIPPED (unreferenced): $($entry.Key)"
        continue
    }

    if (-not (Test-Path $sourcePath)) {
        Write-Warn "  MISSING: $($entry.Key) — skipping"
        continue
    }

    $destDir = Split-Path $destPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }

    Copy-Item $sourcePath $destPath -Force
    Write-Info "  $($entry.Key) -> $($entry.Value)"
}

# ─── Step 5: Generate guides/index.md ───────────────────────────────────────────

Write-Step "Generating guides/index.md"

$guidesIndex = @"
---
title: winapp CLI framework guides
description: Step-by-step guides for using the winapp CLI with .NET, C++, Electron, Rust, Tauri, Flutter, and other frameworks.
ms.date: $msDate
ms.topic: overview
---

# Framework guides

These guides walk you through using the winapp CLI with your app framework — from project setup to debugging with package identity to packaging as MSIX.

| Framework | Guide |
|-----------|-------|
| .NET / WPF / WinForms | [Get started with .NET](dotnet.md) |
| C++ (CMake) | [Get started with C++](cpp.md) |
| Electron | [Get started with Electron](electron-index.md) |
| Rust | [Get started with Rust](rust.md) |
| Tauri | [Get started with Tauri](tauri.md) |
| Flutter | [Get started with Flutter](flutter.md) |

## Additional guides

- [Packaging an EXE/CLI](packaging-cli.md) — Package an existing executable as MSIX
- [Shell Completion](shell-completion.md) — Enable tab completion for commands, options, and values

## Electron deep-dive guides

After completing the [Electron setup guide](electron-setup.md):

| Guide | Description |
|-------|-------------|
| [Package for distribution](electron-packaging.md) | Create an MSIX package for your Electron app |
| [Phi Silica addon](electron-phi-silica-addon.md) | On-device AI with the Phi Silica model |
| [WinML addon](electron-winml-addon.md) | Machine learning inference with Windows ML |
| [C++ notification addon](electron-cpp-notification-addon.md) | Native Windows notifications from Electron |
"@

$guidesIndexPath = Join-Path $OutputPath "guides\index.md"
Set-Content -Path $guidesIndexPath -Value $guidesIndex -NoNewline -Encoding UTF8
Write-Info "  Generated guides/index.md"

# ─── Step 6: Summary ───────────────────────────────────────────────────────────

Write-Step "Done"

$fileCount = (Get-ChildItem $OutputPath -Recurse -File | Where-Object { $_.Extension -eq ".md" }).Count
$imageCount = (Get-ChildItem $OutputPath -Recurse -File | Where-Object { $_.Extension -ne ".md" }).Count

Write-Ok "Ported $fileCount markdown files + $imageCount images to: $OutputPath"
Write-Info "Ready to commit to MicrosoftDocs/windows-dev-docs-pr under hub/apps/dev-tools/winapp-cli/"
