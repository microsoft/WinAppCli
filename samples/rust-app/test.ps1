<#
.SYNOPSIS
Test script for the rust-app sample.

.DESCRIPTION
Builds the Rust app with Cargo, then packages the binary as an MSIX using
winapp pack.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.

.PARAMETER Verbose
Enable verbose output.
#>

param(
    [string]$WinappPath,
    [switch]$SkipCleanup,
    [switch]$Verbose
)

Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force

$ctx = New-SampleTestContext -SampleName "rust-app" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0

try {
    Push-Location $ctx.SampleDir

    # ------------------------------------------------------------------
    # Prerequisites
    # ------------------------------------------------------------------
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "cargo" -DisplayName "Rust/Cargo"
    Assert-Prerequisite "npm" -DisplayName "npm"

    # ------------------------------------------------------------------
    # Install winapp globally
    # ------------------------------------------------------------------
    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ------------------------------------------------------------------
    # Build Rust app
    # ------------------------------------------------------------------
    Write-TestStep "Building Rust app (release)..." (++$step)
    Assert-Command "cargo build --release" "cargo build --release failed"

    $rustExe = Join-Path $ctx.SampleDir "target\release\rust-app.exe"
    Assert-FileExists $rustExe "rust-app.exe"

    # ------------------------------------------------------------------
    # Prepare MSIX layout directory
    # ------------------------------------------------------------------
    Write-TestStep "Preparing MSIX layout..." (++$step)
    $msixDir = Join-Path $ctx.SampleDir "msix"
    if (Test-Path $msixDir) { Remove-Item $msixDir -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $msixDir -Force
    Copy-Item $rustExe -Destination $msixDir

    # ------------------------------------------------------------------
    # Generate certificate and package MSIX
    # ------------------------------------------------------------------
    Write-TestStep "Generating development certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip --manifest appxmanifest.xml" "Failed to generate dev certificate"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack msix --manifest appxmanifest.xml --cert devcert.pfx" "winapp pack failed"

    # ------------------------------------------------------------------
    # Validate MSIX was created
    # ------------------------------------------------------------------
    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory $ctx.SampleDir -Description "rust-app MSIX package"

    Complete-SampleTest -Context $ctx

} finally {
    Pop-Location
    if (-not $SkipCleanup) {
        Remove-Item -Path (Join-Path $ctx.SampleDir "target") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "msix") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
        Get-ChildItem -Path $ctx.SampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
