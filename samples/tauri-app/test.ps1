<#
.SYNOPSIS
Test script for the tauri-app sample.

.DESCRIPTION
Installs npm and Rust dependencies, builds the Tauri app, and packages the
output as an MSIX.

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

$ctx = New-SampleTestContext -SampleName "tauri-app" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0

try {
    Push-Location $ctx.SampleDir

    # ------------------------------------------------------------------
    # Prerequisites
    # ------------------------------------------------------------------
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "node" -DisplayName "Node.js"
    Assert-Prerequisite "npm" -DisplayName "npm"
    Assert-Prerequisite "cargo" -DisplayName "Rust/Cargo"

    # ------------------------------------------------------------------
    # Install winapp globally
    # ------------------------------------------------------------------
    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ------------------------------------------------------------------
    # Install npm dependencies
    # ------------------------------------------------------------------
    Write-TestStep "Installing npm dependencies..." (++$step)
    Assert-Command "npm install" "npm install failed"

    # ------------------------------------------------------------------
    # Build Tauri app (cargo build for the Rust backend)
    # ------------------------------------------------------------------
    Write-TestStep "Building Tauri app..." (++$step)
    Assert-Command "cargo build --release --manifest-path src-tauri\Cargo.toml" "Tauri cargo build failed"

    $tauriExe = Join-Path $ctx.SampleDir "src-tauri\target\release\tauri-app.exe"
    Assert-FileExists $tauriExe "tauri-app.exe"

    # ------------------------------------------------------------------
    # Generate certificate and package MSIX
    # ------------------------------------------------------------------
    Write-TestStep "Generating development certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip --manifest appxmanifest.xml" "Failed to generate dev certificate"

    Write-TestStep "Preparing MSIX layout..." (++$step)
    $msixDir = Join-Path $ctx.SampleDir "msix-layout"
    if (Test-Path $msixDir) { Remove-Item $msixDir -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $msixDir -Force
    Copy-Item $tauriExe -Destination $msixDir

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack msix-layout --manifest appxmanifest.xml --cert devcert.pfx" "winapp pack failed"

    # ------------------------------------------------------------------
    # Validate MSIX was created
    # ------------------------------------------------------------------
    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory $ctx.SampleDir -Description "tauri-app MSIX package"

    Complete-SampleTest -Context $ctx

} finally {
    Pop-Location
    if (-not $SkipCleanup) {
        Remove-Item -Path (Join-Path $ctx.SampleDir "src-tauri\target") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "msix-layout") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
        Get-ChildItem -Path $ctx.SampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
