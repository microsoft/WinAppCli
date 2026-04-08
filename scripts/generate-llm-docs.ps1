#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate CLI schema and agent skills from CLI binary
.DESCRIPTION
    This script generates docs/cli-schema.json and SKILL.md files
    from the CLI's --cli-schema output. Run after building the CLI to keep documentation in sync.
.PARAMETER CliPath
    Path to the winapp.exe CLI binary (default: artifacts/cli/win-x64/winapp.exe)
.PARAMETER DocsPath
    Path to the docs folder (default: docs)
.PARAMETER SkillsPath
    Path to the skills output folder (default: .github/plugin/skills/winapp-cli)
.EXAMPLE
    .\scripts\generate-llm-docs.ps1
.EXAMPLE
    .\scripts\generate-llm-docs.ps1 -CliPath ".\bin\Debug\winapp.exe"
#>

param(
    [string]$CliPath = "",
    [string]$DocsPath = "",
    [string]$SkillsPath = "",
    [switch]$CalledFromBuildScript = $false
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent

# Track if running with default paths (likely direct invocation)
$UsingDefaultPaths = (-not $CliPath -and -not $DocsPath)

if (-not $CliPath) {
    $CliPath = Join-Path $ProjectRoot "artifacts\cli\win-x64\winapp.exe"
}

if (-not $DocsPath) {
    $DocsPath = Join-Path $ProjectRoot "docs"
}

if (-not $SkillsPath) {
    $SkillsPath = Join-Path $ProjectRoot ".github\plugin\skills\winapp-cli"
}

$SchemaOutputPath = Join-Path $DocsPath "cli-schema.json"

# Verify CLI exists
if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

Write-Host "[DOCS] Generating CLI schema and agent skills..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray
Write-Host "Docs path: $DocsPath" -ForegroundColor Gray

# Step 1: Generate CLI schema JSON
Write-Host "[DOCS] Extracting CLI schema..." -ForegroundColor Blue
$SchemaJsonLines = & $CliPath --cli-schema
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to extract CLI schema"
    exit 1
}

# Join array lines into single string with LF line endings (CLI outputs pretty-printed JSON)
# Ensure exactly one trailing newline for consistency
$SchemaJson = ($SchemaJsonLines -join "`n").TrimEnd() + "`n"

# Save schema JSON with consistent LF line endings
[System.IO.File]::WriteAllText($SchemaOutputPath, $SchemaJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "[DOCS] Saved: $SchemaOutputPath" -ForegroundColor Green

# Parse schema for markdown generation
$Schema = $SchemaJson | ConvertFrom-Json

# ==============================================================================
# Step 2: Generate SKILL.md files for agent context
# ==============================================================================
Write-Host ""
Write-Host "[SKILLS] Generating agent skill files..." -ForegroundColor Blue

$CliVersion = $Schema.version
$SkillTemplatesDir = Join-Path (Split-Path $PSScriptRoot) "docs\fragments\skills\winapp-cli"

# Output directory for generated skills (use parameter or default)
$SkillsDir = $SkillsPath

# Skill → CLI command mapping for auto-generated options/arguments tables
# Each skill maps to one or more CLI commands whose options/arguments should be included
$SkillCommandMap = @{
    "setup"        = @("init", "restore", "update", "run")
    "package"      = @("package", "create-external-catalog")
    "identity"     = @("create-debug-identity")
    "signing"      = @("cert generate", "cert install", "cert info", "sign")
    "manifest"     = @("manifest generate", "manifest update-assets", "manifest add-alias")
    "troubleshoot"    = @("get-winapp-path", "tool", "store")
    "frameworks"      = @()       # No auto-generated command sections — links to guides
    "ui-automation"   = @("ui status", "ui inspect", "ui search", "ui get-property", "ui get-value", "ui screenshot", "ui invoke", "ui click", "ui set-value", "ui focus", "ui scroll-into-view", "ui scroll", "ui wait-for", "ui list-windows", "ui get-focused")
}

# Validate that all CLI commands are covered by at least one skill
$allMappedCommands = $SkillCommandMap.Values | ForEach-Object { $_ } | Where-Object { $_ }
$allSchemaCommands = @()
foreach ($cmd in $Schema.subcommands.PSObject.Properties) {
    if ($cmd.Value.subcommands) {
        foreach ($sub in $cmd.Value.subcommands.PSObject.Properties) {
            $allSchemaCommands += "$($cmd.Name) $($sub.Name)"
        }
    } else {
        $allSchemaCommands += $cmd.Name
    }
}
$unmappedCommands = $allSchemaCommands | Where-Object { $_ -notin $allMappedCommands }
if ($unmappedCommands) {
    Write-Warning "The following CLI commands are not mapped to any skill in `$SkillCommandMap:"
    foreach ($cmd in $unmappedCommands) {
        Write-Warning "  - $cmd"
    }
    Write-Warning "Add them to `$SkillCommandMap in generate-llm-docs.ps1 so their options appear in SKILL.md files."
}

# Function to resolve a command path like "cert generate" from the schema
function Get-SchemaCommand {
    param([string]$CommandPath, [PSObject]$RootSchema)
    
    $parts = $CommandPath -split ' '
    $current = $RootSchema
    foreach ($part in $parts) {
        if ($current.subcommands -and $current.subcommands.PSObject.Properties[$part]) {
            $current = $current.subcommands.$part
        } else {
            return $null
        }
    }
    return $current
}

# Function to format a command's arguments as a markdown table
function Format-ArgumentsTable {
    param([PSObject]$Command)
    
    if (-not $Command.arguments) { return "" }
    
    $output = @()
    $output += "| Argument | Required | Description |"
    $output += "|----------|----------|-------------|"
    
    $sortedArgs = $Command.arguments.PSObject.Properties | Sort-Object { $_.Value.order }
    foreach ($arg in $sortedArgs) {
        $required = if ($arg.Value.arity.minimum -gt 0) { "Yes" } else { "No" }
        $output += "| ``<$($arg.Name)>`` | $required | $($arg.Value.description) |"
    }
    
    return ($output -join "`n")
}

# Function to format a command's options as a markdown table
function Format-OptionsTable {
    param([PSObject]$Command)
    
    if (-not $Command.options) { return "" }
    
    $visibleOptions = $Command.options.PSObject.Properties | Where-Object { 
        -not $_.Value.hidden -and $_.Name -notin @('--verbose', '--quiet', '-v', '-q')
    }
    
    if (-not $visibleOptions) { return "" }
    
    $output = @()
    $output += "| Option | Description | Default |"
    $output += "|--------|-------------|---------|"
    
    foreach ($opt in $visibleOptions) {
        $default = if ($opt.Value.hasDefaultValue -and $null -ne $opt.Value.defaultValue -and $opt.Value.defaultValue -ne "" -and $opt.Value.defaultValue -ne "False") {
            "``$($opt.Value.defaultValue)``"
        } else {
            "(none)"
        }
        $output += "| ``$($opt.Name)`` | $($opt.Value.description) | $default |"
    }
    
    return ($output -join "`n")
}

# Function to generate auto-generated command sections for a skill
function Format-CommandSections {
    param([string[]]$CommandPaths, [PSObject]$RootSchema)
    
    if (-not $CommandPaths -or $CommandPaths.Count -eq 0) { return "" }
    
    $output = @()
    
    foreach ($cmdPath in $CommandPaths) {
        $cmd = Get-SchemaCommand -CommandPath $cmdPath -RootSchema $RootSchema
        if (-not $cmd) {
            Write-Warning "Command '$cmdPath' not found in schema"
            continue
        }
        
        $output += ""
        $output += "### ``winapp $cmdPath``"
        $output += ""
        $output += $cmd.description
        
        # Aliases
        if ($cmd.aliases -and $cmd.aliases.Count -gt 0) {
            $aliasStr = ($cmd.aliases | ForEach-Object { "``$_``" }) -join ", "
            $output += ""
            $output += "**Aliases:** $aliasStr"
        }
        
        # Arguments table
        $argsTable = Format-ArgumentsTable -Command $cmd
        if ($argsTable) {
            $output += ""
            $output += "#### Arguments"
            $output += "<!-- auto-generated from cli-schema.json -->"
            $output += $argsTable
        }
        
        # Options table
        $optsTable = Format-OptionsTable -Command $cmd
        if ($optsTable) {
            $output += ""
            $output += "#### Options"
            $output += "<!-- auto-generated from cli-schema.json -->"
            $output += $optsTable
        }
    }
    
    return ($output -join "`n")
}

# Generate each skill
$SkillNames = @("setup", "package", "identity", "signing", "manifest", "troubleshoot", "frameworks", "ui-automation")
$SkillDescriptions = @{
    "setup"        = "Set up a Windows app project for MSIX packaging, Windows SDK access, or Windows API usage. Use when adding Windows support to an Electron, .NET, C++, Rust, Flutter, or Tauri project, or restoring SDK packages after cloning."
    "package"      = "Package a Windows app as an MSIX installer for distribution or testing. Use when creating a Windows installer, packaging an Electron/Flutter/.NET/Rust/C++/Tauri app for Windows, building an MSIX, distributing a desktop app, packaging a console app or CLI tool, or adding MSIX packaging to a build script or CI/CD pipeline."
    "identity"     = "Enable Windows package identity for desktop apps to access Windows APIs like push notifications, background tasks, share target, and startup tasks. Use when adding Windows notifications, background tasks, or other identity-requiring Windows features to a desktop app."
    "signing"      = "Create and manage code signing certificates for Windows apps and MSIX packages. Use when generating a certificate, signing a Windows app or installer, or fixing certificate trust issues."
    "manifest"     = "Create and edit Windows app manifest files (appxmanifest.xml) that define app identity, capabilities, and visual assets, or generate new assets from existing images. Use when creating a Windows app manifest for any app type (GUI, console, CLI tool, service), adding Windows capabilities, generating new app icons and assets, or adding execution aliases, file associations, protocol handlers, or other app extensions."
    "troubleshoot" = "Diagnose and fix common Windows app packaging, signing, identity, and SDK errors. Use when encountering errors with MSIX packaging, certificate signing, Windows SDK setup, or app installation."
    "frameworks"      = "Framework-specific Windows development guidance for Electron, .NET (WPF, WinForms), C++, Rust, Flutter, and Tauri. Use when packaging or adding Windows features to an Electron app, .NET desktop app, Flutter app, Tauri app, Rust app, or C++ app."
    "ui-automation"   = "Inspect and interact with running Windows app UIs from the command line using UI Automation (UIA). Use when an AI agent or developer needs to inspect a UI element tree, find controls, take screenshots, click buttons, read or set text, or verify UI state in a running Windows app. Works with any framework: WinUI 3, WPF, WinForms, Win32, Electron."
}

foreach ($skillName in $SkillNames) {
    $templatePath = Join-Path $SkillTemplatesDir "$skillName.md"
    
    if (-not (Test-Path $templatePath)) {
        Write-Warning "Skill template not found: $templatePath"
        continue
    }
    
    $templateContent = Get-Content $templatePath -Raw
    $commandPaths = $SkillCommandMap[$skillName]
    $description = $SkillDescriptions[$skillName]
    
    # Build the SKILL.md content
    $skillContent = @"
---
name: winapp-$skillName
description: $description
version: $CliVersion
---

"@
    
    # Add template content (hand-written workflows, examples, troubleshooting)
    $skillContent += $templateContent
    
    # Add auto-generated command sections
    $commandSections = Format-CommandSections -CommandPaths $commandPaths -RootSchema $Schema
    if ($commandSections) {
        $skillContent += "`n`n## Command Reference`n"
        $skillContent += $commandSections
    }
    
    # Normalize line endings and ensure trailing newline
    $skillContent = $skillContent -replace "`r`n", "`n"
    $skillContent = $skillContent.TrimEnd() + "`n"
    
    # Write to output directory
    $skillDir = Join-Path $SkillsDir $skillName
    if (-not (Test-Path $skillDir)) {
        New-Item -ItemType Directory -Path $skillDir -Force | Out-Null
    }
    $skillPath = Join-Path $skillDir "SKILL.md"
    [System.IO.File]::WriteAllText($skillPath, $skillContent, [System.Text.UTF8Encoding]::new($false))
    
    Write-Host "[SKILLS]   $skillName - generated" -ForegroundColor Gray
}

# Update plugin.json version to match CLI version (only when outputting to the default skills path)
$DefaultSkillsPath = Join-Path $ProjectRoot ".github\plugin\skills\winapp-cli"
if ($SkillsDir -eq $DefaultSkillsPath) {
    $PluginJsonPath = Join-Path $ProjectRoot ".github\plugin\plugin.json"
    if (Test-Path $PluginJsonPath) {
        $pluginJson = Get-Content $PluginJsonPath -Raw | ConvertFrom-Json
        $pluginJson.version = $CliVersion
        $pluginJsonContent = $pluginJson | ConvertTo-Json -Depth 10
        # Normalize line endings
        $pluginJsonContent = $pluginJsonContent -replace "`r`n", "`n"
        $pluginJsonContent = $pluginJsonContent.TrimEnd() + "`n"
        [System.IO.File]::WriteAllText($PluginJsonPath, $pluginJsonContent, [System.Text.UTF8Encoding]::new($false))
        Write-Host "[SKILLS] Updated plugin.json version to $CliVersion" -ForegroundColor Gray
    }
}

Write-Host "[SKILLS] Generated $($SkillNames.Count) skills in:" -ForegroundColor Green
Write-Host "  .github/plugin/skills/winapp-cli/" -ForegroundColor Gray

Write-Host ""
Write-Host "[DOCS] All documentation and skills generated successfully!" -ForegroundColor Green

# Warn if running directly (not from build-cli.ps1)
if (-not $CalledFromBuildScript -and $UsingDefaultPaths) {
    Write-Host ""
    Write-Host "[DOCS] Warning: Running generate-llm-docs.ps1 directly may use stale CLI binaries." -ForegroundColor Yellow
    Write-Host "[DOCS] For accurate docs, run 'scripts/build-cli.ps1' which rebuilds and regenerates docs." -ForegroundColor Yellow
}
