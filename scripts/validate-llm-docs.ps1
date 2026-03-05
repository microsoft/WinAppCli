#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validate that CLI schema and agent skills are up-to-date
.DESCRIPTION
    This script checks if the generated CLI schema (docs/cli-schema.json) and agent skills
    match what the CLI would generate. Use this locally before
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

# Verify CLI exists
if (-not (Test-Path $CliPath)) {
    Write-Error "CLI not found at: $CliPath"
    Write-Error "Build the CLI first with: .\scripts\build-cli.ps1"
    exit 1
}

Write-Host "[VALIDATE] Checking CLI schema and agent skills..." -ForegroundColor Blue
Write-Host "CLI path: $CliPath" -ForegroundColor Gray

# Read base version from version.json — used to fix up the version in freshly generated
# output so the comparison still catches stale committed versions even when the CLI binary
# was built without a real version (e.g. plain dotnet publish defaults to 1.0.0).
$BaseVersion = (Get-Content (Join-Path $ProjectRoot "version.json") | ConvertFrom-Json).version

# Check if doc files exist
if (-not (Test-Path $SchemaPath)) {
    Write-Host "::error::docs/cli-schema.json not found. Run 'scripts/build-cli.ps1' to build CLI and generate docs." -ForegroundColor Red
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
    
    # The CLI binary may not have the real version (plain dotnet publish defaults to 1.0.0).
    # Replace the fresh version with the base version from version.json so the comparison
    # still validates that committed docs carry the correct version.
    $FreshObj.version = $BaseVersion
    
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

# Validate agent skills by regenerating to a temp location and comparing
Write-Host "[VALIDATE] Checking agent skills..." -ForegroundColor Blue
$TempDocsPath = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-llm-docs-validate"
if (Test-Path $TempDocsPath) {
    Remove-Item $TempDocsPath -Recurse -Force
}
New-Item -ItemType Directory -Path $TempDocsPath -Force | Out-Null

try {
    # Generate to temp location (docs + skills)
    $GenerateScript = Join-Path $PSScriptRoot "generate-llm-docs.ps1"
    $TempSkills = Join-Path $TempDocsPath "skills\winapp-cli"
    & $GenerateScript -CliPath $CliPath -DocsPath $TempDocsPath -SkillsPath $TempSkills | Out-Null
    
    # Fix up the version in the freshly generated skill files.
    # The CLI binary may not have the real version (plain dotnet publish defaults to 1.0.0).
    # Only replace the version in the YAML frontmatter — not in example commands/templates
    # which may legitimately contain the same string (e.g. "MyApp_1.0.0_x64").
    $TempSchemaPath = Join-Path $TempDocsPath "cli-schema.json"
    if (Test-Path $TempSchemaPath) {
        $tempSchemaObj = (Get-Content $TempSchemaPath -Raw) | ConvertFrom-Json -Depth 100
        $freshCliVersion = $tempSchemaObj.version
        if ($freshCliVersion -and $freshCliVersion -ne $BaseVersion) {
            Get-ChildItem -Path $TempSkills -Filter "*.md" -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                $content = Get-Content $_.FullName -Raw
                $content = $content -replace "(?m)^(version:\s*)$([regex]::Escape($freshCliVersion))$", "`${1}$BaseVersion"
                [System.IO.File]::WriteAllText($_.FullName, $content)
            }
        }
    }
    
    $SkillNames = @("setup", "package", "identity", "signing", "manifest", "troubleshoot", "frameworks")
    $SkillsDrift = $false
    
    foreach ($skillName in $SkillNames) {
        $FreshSkill = Join-Path $TempDocsPath "skills\winapp-cli\$skillName\SKILL.md"
        $CommittedSkill = Join-Path $ProjectRoot ".github\plugin\skills\winapp-cli\$skillName\SKILL.md"
        
        if (-not (Test-Path $CommittedSkill)) {
            Write-Host "::error::skill '$skillName' not found at $CommittedSkill" -ForegroundColor Red
            $SkillsDrift = $true
            continue
        }
        
        if (Test-Path $FreshSkill) {
            $freshContent = (Get-Content $FreshSkill -Raw) -replace "`r`n", "`n"
            $committedContent = (Get-Content $CommittedSkill -Raw) -replace "`r`n", "`n"
            
            if ($freshContent -ne $committedContent) {
                Write-Host "::error::skill '$skillName' is out of sync!" -ForegroundColor Red
                $SkillsDrift = $true
            }
        } else {
            Write-Host "::error::freshly generated skill '$skillName' not found at $FreshSkill (generation or template issue?)" -ForegroundColor Red
            $SkillsDrift = $true
        }
    }
    
    if ($SkillsDrift) {
        Write-Host ""
        Write-Host "Run 'scripts/build-cli.ps1' locally to rebuild CLI and regenerate all docs/skills, then commit." -ForegroundColor Yellow
        Write-Host ""
        if ($FailOnDrift) {
            exit 1
        }
    } else {
        Write-Host "[VALIDATE] Agent skills are up-to-date" -ForegroundColor Green
    }
}
finally {
    if (Test-Path $TempDocsPath) {
        Remove-Item $TempDocsPath -Recurse -Force
    }
}

Write-Host "[VALIDATE] CLI schema and agent skills are up-to-date!" -ForegroundColor Green

# Warn about potential stale artifacts
Write-Host ""
Write-Host "[VALIDATE] Note: This validated against CLI binaries in artifacts/cli/." -ForegroundColor Gray
Write-Host "[VALIDATE] If you changed CLI code, run 'scripts/build-cli.ps1' first to rebuild." -ForegroundColor Gray

exit 0
