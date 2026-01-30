#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate that LLM documentation is up-to-date with CLI schema
.DESCRIPTION
    This script checks if the generated LLM documentation (docs/cli-schema.json and 
    docs/llm-context.md) matches what the CLI would generate. Use this locally before
    committing changes, or in CI to catch drift.
.PARAMETER CliPath
    Path to the winapp.exe CLI binary (default: artifacts/cli/win-x64/winapp.exe)
.PARAMETER FailOnDrift
    Exit with error code 1 if documentation is out of sync (default: true)
.EXAMPLE
    .\scripts\validate-llm-docs.ps1
.EXAMPLE
    .\scripts\validate-llm-docs.ps1 -FailOnDrift:$false
#>

param(
    [string]$CliPath = "",
    [switch]$FailOnDrift = $true
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent

if (-not $CliPath) {
    $CliPath = Join-Path $ProjectRoot "artifacts\cli\win-x64\winapp.exe"
}

$SchemaPath = Join-Path $ProjectRoot "docs\cli-schema.json"
$ContextPath = Join-Path $ProjectRoot "docs\llm-context.md"

# Verify CLI exists
if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

Write-Host "[VALIDATE] Checking LLM documentation..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray

# Check if doc files exist
if (-not (Test-Path $SchemaPath)) {
    Write-Host "::error::docs/cli-schema.json not found. Run 'scripts/build-cli.ps1' to build CLI and generate docs." -ForegroundColor Red
    if ($FailOnDrift) { exit 1 }
    exit 0
}

if (-not (Test-Path $ContextPath)) {
    Write-Host "::error::docs/llm-context.md not found. Run 'scripts/build-cli.ps1' to build CLI and generate docs." -ForegroundColor Red
    if ($FailOnDrift) { exit 1 }
    exit 0
}

# Generate fresh schema and compare
Write-Host "[VALIDATE] Generating fresh schema from CLI..." -ForegroundColor Blue
$FreshSchemaLines = & $CliPath --cli-schema
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to extract CLI schema"
    exit 1
}

# Join array lines into single string (CLI outputs pretty-printed JSON with newlines)
$FreshSchema = $FreshSchemaLines -join "`n"

$CommittedSchema = Get-Content $SchemaPath -Raw

# Normalize line endings for comparison
$FreshSchemaNormalized = $FreshSchema -replace "`r`n", "`n"
$CommittedSchemaNormalized = $CommittedSchema -replace "`r`n", "`n"

# Compare JSON semantically (ignore formatting differences like indentation)
$SchemaDrift = $false
try {
    $FreshObj = $FreshSchemaNormalized | ConvertFrom-Json -Depth 100
    $CommittedObj = $CommittedSchemaNormalized | ConvertFrom-Json -Depth 100
    
    # Re-serialize both with consistent formatting for comparison
    $FreshReserialized = $FreshObj | ConvertTo-Json -Depth 100 -Compress
    $CommittedReserialized = $CommittedObj | ConvertTo-Json -Depth 100 -Compress
    
    $SchemaDrift = $FreshReserialized -ne $CommittedReserialized
    
    if (-not $SchemaDrift -and $FreshSchemaNormalized -ne $CommittedSchemaNormalized) {
        Write-Host "[VALIDATE] docs/cli-schema.json has formatting differences but content is identical (OK)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "[VALIDATE] Warning: Could not parse JSON for semantic comparison, falling back to string comparison" -ForegroundColor Yellow
    $SchemaDrift = $FreshSchemaNormalized -ne $CommittedSchemaNormalized
}

if ($SchemaDrift) {
    Write-Host "::error::docs/cli-schema.json is out of sync with CLI!" -ForegroundColor Red
    Write-Host ""
    Write-Host "The committed schema doesn't match what the CLI generates." -ForegroundColor Yellow
    Write-Host "Run 'scripts/build-cli.ps1' locally to rebuild CLI and regenerate docs, then commit the changes." -ForegroundColor Yellow
    Write-Host ""
    
    # Show diff details for debugging
    Write-Host "[DEBUG] Schema differences:" -ForegroundColor Cyan
    Write-Host "  Fresh schema length: $($FreshSchemaNormalized.Length) chars" -ForegroundColor Gray
    Write-Host "  Committed schema length: $($CommittedSchemaNormalized.Length) chars" -ForegroundColor Gray
    
    # Parse and compare versions if possible
    try {
        $FreshObj = $FreshSchemaNormalized | ConvertFrom-Json
        $CommittedObj = $CommittedSchemaNormalized | ConvertFrom-Json
        Write-Host "  Fresh version: $($FreshObj.version)" -ForegroundColor Gray
        Write-Host "  Committed version: $($CommittedObj.version)" -ForegroundColor Gray
    } catch {
        Write-Host "  (Could not parse JSON for version comparison)" -ForegroundColor Gray
    }
    
    # Find first differing line
    $FreshLines = $FreshSchemaNormalized -split "`n"
    $CommittedLines = $CommittedSchemaNormalized -split "`n"
    Write-Host "  Fresh schema lines: $($FreshLines.Count)" -ForegroundColor Gray
    Write-Host "  Committed schema lines: $($CommittedLines.Count)" -ForegroundColor Gray
    
    $MaxLines = [Math]::Max($FreshLines.Count, $CommittedLines.Count)
    $DiffCount = 0
    $MaxDiffsToShow = 5
    for ($i = 0; $i -lt $MaxLines -and $DiffCount -lt $MaxDiffsToShow; $i++) {
        $FreshLine = if ($i -lt $FreshLines.Count) { $FreshLines[$i] } else { "<EOF>" }
        $CommittedLine = if ($i -lt $CommittedLines.Count) { $CommittedLines[$i] } else { "<EOF>" }
        
        if ($FreshLine -ne $CommittedLine) {
            $DiffCount++
            Write-Host ""
            Write-Host "  [Line $($i + 1)] Difference #$DiffCount" -ForegroundColor Yellow
            Write-Host "    Expected: $($FreshLine.Substring(0, [Math]::Min(100, $FreshLine.Length)))$(if ($FreshLine.Length -gt 100) { '...' })" -ForegroundColor Green
            Write-Host "    Actual: $($CommittedLine.Substring(0, [Math]::Min(100, $CommittedLine.Length)))$(if ($CommittedLine.Length -gt 100) { '...' })" -ForegroundColor Red
        }
    }
    Write-Host ""
    
    if ($FailOnDrift) {
        exit 1
    }
} else {
    Write-Host "[VALIDATE] docs/cli-schema.json is up-to-date" -ForegroundColor Green
}

# For llm-context.md, regenerate to a temp location and compare
Write-Host "[VALIDATE] Checking llm-context.md..." -ForegroundColor Blue
$TempDocsPath = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-llm-docs-validate"
if (Test-Path $TempDocsPath) {
    Remove-Item $TempDocsPath -Recurse -Force
}
New-Item -ItemType Directory -Path $TempDocsPath -Force | Out-Null

try {
    # Generate to temp location
    $GenerateScript = Join-Path $PSScriptRoot "generate-llm-docs.ps1"
    & $GenerateScript -CliPath $CliPath -DocsPath $TempDocsPath | Out-Null
    
    # Compare llm-context.md with line ending normalization
    $FreshContext = Get-Content (Join-Path $TempDocsPath "llm-context.md") -Raw
    $CommittedContext = Get-Content $ContextPath -Raw
    
    $FreshContextNormalized = $FreshContext -replace "`r`n", "`n"
    $CommittedContextNormalized = $CommittedContext -replace "`r`n", "`n"
    
    if ($FreshContextNormalized -ne $CommittedContextNormalized) {
        Write-Host "::error::docs/llm-context.md is out of sync with CLI schema!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Run 'scripts/build-cli.ps1' locally to rebuild CLI and regenerate docs, then commit the changes." -ForegroundColor Yellow
        Write-Host ""
        
        # Show diff details for debugging
        Write-Host "[DEBUG] Showing differences:" -ForegroundColor Cyan
        Write-Host "  Fresh file length: $($FreshContextNormalized.Length) chars" -ForegroundColor Gray
        Write-Host "  Committed file length: $($CommittedContextNormalized.Length) chars" -ForegroundColor Gray
        
        # Split into lines for comparison
        $FreshLines = $FreshContextNormalized -split "`n"
        $CommittedLines = $CommittedContextNormalized -split "`n"
        Write-Host "  Fresh file lines: $($FreshLines.Count)" -ForegroundColor Gray
        Write-Host "  Committed file lines: $($CommittedLines.Count)" -ForegroundColor Gray
        
        # Find first differing line
        $MaxLines = [Math]::Max($FreshLines.Count, $CommittedLines.Count)
        $DiffCount = 0
        $MaxDiffsToShow = 10
        for ($i = 0; $i -lt $MaxLines -and $DiffCount -lt $MaxDiffsToShow; $i++) {
            $FreshLine = if ($i -lt $FreshLines.Count) { $FreshLines[$i] } else { "<EOF>" }
            $CommittedLine = if ($i -lt $CommittedLines.Count) { $CommittedLines[$i] } else { "<EOF>" }
            
            if ($FreshLine -ne $CommittedLine) {
                $DiffCount++
                Write-Host ""
                Write-Host "  [Line $($i + 1)] Difference #$DiffCount" -ForegroundColor Yellow
                Write-Host "    Expected (fresh): $($FreshLine.Substring(0, [Math]::Min(120, $FreshLine.Length)))$(if ($FreshLine.Length -gt 120) { '...' })" -ForegroundColor Green
                Write-Host "    Actual (committed): $($CommittedLine.Substring(0, [Math]::Min(120, $CommittedLine.Length)))$(if ($CommittedLine.Length -gt 120) { '...' })" -ForegroundColor Red
            }
        }
        
        if ($DiffCount -eq 0) {
            Write-Host "  [DEBUG] No line-by-line differences found, but content differs (possible whitespace/encoding issue)" -ForegroundColor Yellow
            # Check for BOM or other encoding differences
            $FreshBytes = [System.Text.Encoding]::UTF8.GetBytes($FreshContextNormalized.Substring(0, [Math]::Min(100, $FreshContextNormalized.Length)))
            $CommittedBytes = [System.Text.Encoding]::UTF8.GetBytes($CommittedContextNormalized.Substring(0, [Math]::Min(100, $CommittedContextNormalized.Length)))
            Write-Host "  Fresh first bytes: $($FreshBytes[0..9] -join ', ')" -ForegroundColor Gray
            Write-Host "  Committed first bytes: $($CommittedBytes[0..9] -join ', ')" -ForegroundColor Gray
        } elseif ($DiffCount -ge $MaxDiffsToShow) {
            $TotalDiffs = ($FreshLines | ForEach-Object -Begin { $idx = 0 } -Process { 
                if ($idx -lt $CommittedLines.Count -and $_ -ne $CommittedLines[$idx]) { 1 }
                $idx++
            } | Measure-Object -Sum).Sum
            Write-Host ""
            Write-Host "  ... and more differences (showing first $MaxDiffsToShow of ~$TotalDiffs)" -ForegroundColor Yellow
        }
        Write-Host ""
        
        if ($FailOnDrift) {
            exit 1
        }
    } else {
        Write-Host "[VALIDATE] docs/llm-context.md is up-to-date" -ForegroundColor Green
    }
}
finally {
    if (Test-Path $TempDocsPath) {
        Remove-Item $TempDocsPath -Recurse -Force
    }
}

Write-Host "[VALIDATE] LLM documentation is up-to-date!" -ForegroundColor Green

# Warn about potential stale artifacts
Write-Host ""
Write-Host "[VALIDATE] Note: This validated against CLI binaries in artifacts/cli/." -ForegroundColor Gray
Write-Host "[VALIDATE] If you changed CLI code, run 'scripts/build-cli.ps1' first to rebuild." -ForegroundColor Gray

exit 0
