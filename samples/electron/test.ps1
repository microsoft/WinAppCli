<#
.SYNOPSIS
Test script for the electron sample freshness check.

.DESCRIPTION
Verifies the existing samples/electron code is not stale by installing
dependencies and validating structure. The from-scratch Electron guide
workflow (init, addon creation, Forge packaging, MSIX) is covered by
the dedicated E2E test in scripts/test-e2e-electron.ps1.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.
#>

[CmdletBinding()]
param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force

$ctx = New-SampleTestContext -SampleName "electron" -SampleDir $PSScriptRoot -WinappPath $WinappPath -Verbose:$VerbosePreference
$step = 0

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "node" -DisplayName "Node.js"
    Assert-Prerequisite "npm" -DisplayName "npm"

    # ==================================================================
    # Sample Build Check
    # ==================================================================
    Write-TestHeader "Electron Sample Freshness Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Installing sample dependencies..." (++$step)
    Assert-Command "npm install --ignore-scripts" "npm install failed"
    Assert-DirectoryExists "node_modules" "node_modules"

    Write-TestStep "Verifying sample structure..." (++$step)
    Assert-FileExists "package.json" "package.json"
    Assert-FileExists "forge.config.js" "forge.config.js"
    Assert-FileExists "appxmanifest.xml" "appxmanifest.xml"
    Write-TestSuccess "electron sample is valid and installable"

    Pop-Location

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        Remove-Item -Path (Join-Path $ctx.SampleDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
    }
}
