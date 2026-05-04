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

# File mapping: repo source path (relative to repo root, forward slash) → output path (relative to output dir)
# This is the single source of truth for all link resolution.
$FileMapping = [ordered]@{
    "docs/README.md"                                  = "index.md"
    "docs/usage.md"                                  = "usage.md"
    "docs/guides/dotnet.md"                          = "guides/dotnet.md"
    "docs/guides/cpp.md"                             = "guides/cpp.md"
    "docs/guides/flutter.md"                         = "guides/flutter.md"
    "docs/guides/rust.md"                            = "guides/rust.md"
    "docs/guides/tauri.md"                           = "guides/tauri.md"
    "docs/guides/packaging-cli.md"                   = "guides/packaging-cli.md"
    "docs/guides/electron/setup.md"                  = "guides/electron-setup.md"
    "docs/guides/electron/packaging.md"              = "guides/electron-packaging.md"
    "docs/guides/electron/phi-silica-addon.md"       = "guides/electron-phi-silica-addon.md"
    "docs/guides/electron/winml-addon.md"            = "guides/electron-winml-addon.md"
    "docs/guides/electron/cpp-notification-addon.md" = "guides/electron-cpp-notification-addon.md"
}

# Front matter overrides: output path → { title, description, ms.topic }
# Falls back to auto-detection if not specified.
$FrontMatterOverrides = @{
    "index.md" = @{
        description = "The Windows App Development CLI (winapp CLI) is a command-line interface for managing Windows SDKs, packaging, generating app identity, manifests, certificates, and using build tools with any app framework."
        topic       = "overview"
    }
    "usage.md" = @{
        description = "Complete command reference for the Windows App Development CLI (winapp CLI) including setup, packaging, identity, certificates, signing, and utility commands."
        topic       = "reference"
    }
    "guides/dotnet.md" = @{
        description = "Learn how to use the winapp CLI with a .NET application to debug with package identity and package your application as an MSIX."
        topic       = "how-to"
    }
    "guides/cpp.md" = @{
        description = "Learn how to use the winapp CLI with a C++ and CMake application to debug with package identity and package your application as an MSIX."
        topic       = "how-to"
    }
    "guides/flutter.md" = @{
        description = "Learn how to use the winapp CLI with a Flutter application to add package identity and package your app as an MSIX."
        topic       = "how-to"
    }
    "guides/rust.md" = @{
        description = "Learn how to use the winapp CLI with a Rust application to debug with package identity and package your application as an MSIX."
        topic       = "how-to"
    }
    "guides/tauri.md" = @{
        description = "Learn how to use the winapp CLI with a Tauri application to debug with package identity and package your application as an MSIX."
        topic       = "how-to"
    }
    "guides/packaging-cli.md" = @{
        description = "Step-by-step guide to packaging an existing EXE or CLI tool as an MSIX package using the winapp CLI."
        topic       = "how-to"
    }
    "guides/electron-setup.md" = @{
        description = "Set up your Electron development environment for Windows API development with the winapp CLI."
        topic       = "how-to"
    }
    "guides/electron-packaging.md" = @{
        description = "Package your Electron app as an MSIX for distribution using the winapp CLI."
        topic       = "how-to"
    }
    "guides/electron-phi-silica-addon.md" = @{
        description = "Create an Electron addon that uses the Phi Silica on-device AI model through the Windows App SDK."
        topic       = "how-to"
    }
    "guides/electron-winml-addon.md" = @{
        description = "Create an Electron addon that uses Windows ML for on-device machine learning inference."
        topic       = "how-to"
    }
    "guides/electron-cpp-notification-addon.md" = @{
        description = "Create a native C++ Electron addon that sends Windows notifications using the Windows SDK."
        topic       = "how-to"
    }
}

# Image files to copy: repo source path → output path
$ImageMapping = @{
    "docs/images/ai-dev-gallery-squeezenet.png"      = "guides/media/ai-dev-gallery-squeezenet.png"
    "docs/images/ai-dev-gallery-squeezenet-code.png"  = "guides/media/ai-dev-gallery-squeezenet-code.png"
}

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

# ─── Step 1: Prepare output directory ───────────────────────────────────────────

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputPath "guides") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $OutputPath "guides\media") -Force | Out-Null

# ─── Step 2: Build the canonical link resolution map ────────────────────────────

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

# Add known repo-only files (not ported, link to GitHub)
$repoOnlyFiles = @(
    "docs/debugging.md"
    "docs/dotnet-run-support.md"
    "docs/electron-get-started.md"
    "docs/npm-usage.md"
    "docs/ui-automation.md"
    "docs/telemetry.md"
    "docs/guides/shell-completion.md"
    "docs/guides/claude-code-plugin.md"
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

foreach ($path in $repoOnlyFiles) {
    $isFile = $path -match '\.\w+$'
    $ghPath = if ($isFile) { "$GitHubRepoBase/blob/main/$path" } else { "$GitHubRepoBase/tree/main/$path" }
    $LinkMap[$path] = @{ Type = "github"; Url = $ghPath }
}

Write-Info "Map has $($LinkMap.Count) entries ($($FileMapping.Count) ported, $($repoOnlyFiles.Count) repo-only)"

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

    # Convert https://learn.microsoft.com[/en-us]/windows/... → /windows/...
    $Content = [regex]::Replace($Content, 'https://learn\.microsoft\.com(?:/en-us)?(/windows/[^\s\)\]"]+)', '$1')

    return $Content
}

# ─── Front matter generator ────────────────────────────────────────────────────

function Get-FrontMatter {
    param(
        [string]$Content,
        [string]$OutputPath
    )

    # Extract title from first # heading
    $title = "winapp CLI"
    if ($Content -match '(?m)^#\s+(.+)$') {
        $title = $Matches[1].Trim()
    }

    # Get overrides
    $overrides = $FrontMatterOverrides[$OutputPath]
    $description = if ($overrides -and $overrides.description) { $overrides.description }
                   else { $title }
    $topic = if ($overrides -and $overrides.topic) { $overrides.topic }
             else { "how-to" }

    return @"
---
title: $title
description: $description
ms.date: $msDate
ms.topic: $topic
---

"@
}

# ─── Step 3: Process and copy files ─────────────────────────────────────────────

Write-Step "Processing files"

foreach ($entry in $FileMapping.GetEnumerator()) {
    $sourcePath = Join-Path $ProjectRoot ($entry.Key -replace '/', '\')
    $destRelPath = $entry.Value
    $destPath = Join-Path $OutputPath ($destRelPath -replace '/', '\')

    if (-not (Test-Path $sourcePath)) {
        Write-Warn "  MISSING: $($entry.Key) — skipping"
        continue
    }

    # Read content
    $content = Get-Content $sourcePath -Raw

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

    # Restore code blocks
    for ($i = 0; $i -lt $codeBlocks.Count; $i++) {
        $content = $content.Replace("%%CODEBLOCK_${i}%%", $codeBlocks[$i])
    }

    # Add YAML front matter
    $frontMatter = Get-FrontMatter -Content $content -OutputPath $destRelPath
    $content = $frontMatter + $content

    # Write to output
    $destDir = Split-Path $destPath -Parent
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    }
    Set-Content -Path $destPath -Value $content -NoNewline -Encoding UTF8

    Write-Info "  $($entry.Key) -> $destRelPath"
}

# ─── Step 4: Copy images ───────────────────────────────────────────────────────

Write-Step "Copying images"

foreach ($entry in $ImageMapping.GetEnumerator()) {
    $sourcePath = Join-Path $ProjectRoot ($entry.Key -replace '/', '\')
    $destPath = Join-Path $OutputPath ($entry.Value -replace '/', '\')

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
| Electron | [Set up Electron for Windows](electron-setup.md) |
| Rust | [Get started with Rust](rust.md) |
| Tauri | [Get started with Tauri](tauri.md) |
| Flutter | [Get started with Flutter](flutter.md) |

## Additional guides

- [Packaging an EXE/CLI](packaging-cli.md) — Package an existing executable as MSIX

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
