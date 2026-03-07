<#
.SYNOPSIS
Test script for the rust-app sample and Rust guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/rust.md guide from scratch — creates a new
  Rust project, runs winapp init, builds, and packages as MSIX.
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

$ctx = New-SampleTestContext -SampleName "rust-app" -SampleDir $PSScriptRoot -WinappPath $WinappPath -Verbose:$VerbosePreference
$step = 0
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "cargo" -DisplayName "Rust/Cargo"
    Assert-Prerequisite "npm" -DisplayName "npm"

    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ==================================================================
    # Phase 1 — Guide Workflow (from scratch)
    # ==================================================================
    Write-TestHeader "Phase 1: Rust Guide Workflow (from scratch)"

    $tempDir = New-TempTestDirectory -Prefix "rust-guide"
    Push-Location $tempDir

    Write-TestStep "Creating new Rust project..." (++$step)
    Assert-Command "cargo new test-rust-app" "cargo new failed"
    Push-Location "test-rust-app"

    Write-TestStep "Running winapp init..." (++$step)
    Assert-Command "winapp init --use-defaults --setup-sdks=none" "winapp init failed"
    Assert-WinappInitOutput -ExpectManifest

    Write-TestStep "Building Rust app (release)..." (++$step)
    Assert-Command "cargo build --release" "cargo build --release failed"
    Assert-FileExists "target\release\test-rust-app.exe" "test-rust-app.exe"

    Write-TestStep "Preparing MSIX layout..." (++$step)
    $null = New-Item -ItemType Directory -Path "dist" -Force
    Copy-Item "target\release\test-rust-app.exe" -Destination "dist\"

    Write-TestStep "Generating dev certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "cert generate failed"

    Write-TestStep "Verifying certificate info..." (++$step)
    Assert-CertInfo -CertPath "devcert.pfx"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack dist --manifest appxmanifest.xml --cert devcert.pfx" "winapp pack failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory (Get-Location) -Description "Guide rust-app MSIX"

    Pop-Location  # back to tempDir
    Pop-Location  # back to original

    # ==================================================================
    # Phase 2 — Sample Build Check
    # ==================================================================
    Write-TestHeader "Phase 2: Sample Build Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Building existing sample..." (++$step)
    Assert-Command "cargo build" "Sample cargo build failed"
    Assert-FileExists "target\debug\rust-app.exe" "rust-app.exe"

    Pop-Location

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        if ($tempDir) { Remove-TempTestDirectory -Path $tempDir }
        Remove-Item -Path (Join-Path $ctx.SampleDir "target") -Recurse -Force -ErrorAction SilentlyContinue
    }
}
