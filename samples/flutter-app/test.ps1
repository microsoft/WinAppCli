<#
.SYNOPSIS
Test script for the flutter-app sample and Flutter guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/flutter.md guide from scratch — creates a new
  Flutter project, runs winapp init, builds, and packages as MSIX.
Phase 2: Quick build of the existing sample to verify it is not stale.

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

$ctx = New-SampleTestContext -SampleName "flutter-app" -SampleDir $PSScriptRoot -WinappPath $WinappPath -Verbose:$VerbosePreference
$step = 0
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "flutter" -DisplayName "Flutter SDK"
    Assert-Prerequisite "npm" -DisplayName "npm"

    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ==================================================================
    # Phase 1 — Guide Workflow (from scratch)
    # ==================================================================
    Write-TestHeader "Phase 1: Flutter Guide Workflow (from scratch)"

    $tempDir = New-TempTestDirectory -Prefix "flutter-guide"
    Push-Location $tempDir

    Write-TestStep "Creating new Flutter project..." (++$step)
    Assert-Command "flutter create test_flutter_app --platforms=windows" "flutter create failed"
    Push-Location "test_flutter_app"

    Write-TestStep "Running winapp init..." (++$step)
    Assert-Command "winapp init --use-defaults --setup-sdks=stable" "winapp init failed"
    Assert-WinappInitOutput -ExpectWinappYaml -ExpectManifest -ExpectDotWinapp

    Write-TestStep "Building Flutter Windows app..." (++$step)
    Assert-Command "flutter build windows" "flutter build windows failed"

    $buildOutput = "build\windows\x64\runner\Release"
    Assert-DirectoryExists $buildOutput "Flutter build output"

    Write-TestStep "Preparing distribution folder..." (++$step)
    Copy-Item $buildOutput -Destination "dist" -Recurse

    Write-TestStep "Generating dev certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "cert generate failed"

    Write-TestStep "Verifying certificate info..." (++$step)
    Assert-CertInfo -CertPath "devcert.pfx"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack dist --cert devcert.pfx" "winapp pack failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory (Get-Location) -Description "Guide flutter-app MSIX"

    Pop-Location  # back to tempDir
    Pop-Location  # back to original

    # ==================================================================
    # Phase 2 — Sample Build Check
    # ==================================================================
    Write-TestHeader "Phase 2: Sample Build Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Getting sample Flutter dependencies..." (++$step)
    Assert-Command "flutter pub get" "flutter pub get failed"

    Write-TestStep "Restoring sample SDK packages..." (++$step)
    Assert-Command "winapp restore" "winapp restore failed"

    Write-TestStep "Building existing sample..." (++$step)
    Assert-Command "flutter build windows" "Sample flutter build failed"
    Assert-FileExists "build\windows\x64\runner\Release\flutter_app.exe" "flutter_app.exe"

    Pop-Location

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        if ($tempDir) { Remove-TempTestDirectory -Path $tempDir }
        Remove-Item -Path (Join-Path $ctx.SampleDir "build") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir ".winapp") -Recurse -Force -ErrorAction SilentlyContinue
    }
}
