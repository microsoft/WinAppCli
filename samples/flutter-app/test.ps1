<#
.SYNOPSIS
Test script for the flutter-app sample.

.DESCRIPTION
Restores packages, builds the Flutter Windows desktop app, and packages
the output as an MSIX.

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

$ctx = New-SampleTestContext -SampleName "flutter-app" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0

try {
    Push-Location $ctx.SampleDir

    # ------------------------------------------------------------------
    # Prerequisites
    # ------------------------------------------------------------------
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "flutter" -DisplayName "Flutter SDK"
    Assert-Prerequisite "npm" -DisplayName "npm"

    # ------------------------------------------------------------------
    # Install winapp globally
    # ------------------------------------------------------------------
    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ------------------------------------------------------------------
    # Restore packages
    # ------------------------------------------------------------------
    Write-TestStep "Getting Flutter dependencies..." (++$step)
    Assert-Command "flutter pub get" "flutter pub get failed"

    Write-TestStep "Restoring Windows App SDK packages..." (++$step)
    Assert-Command "winapp restore" "winapp restore failed"
    Assert-DirectoryExists ".winapp" ".winapp directory"

    # ------------------------------------------------------------------
    # Build Flutter Windows app
    # ------------------------------------------------------------------
    Write-TestStep "Building Flutter Windows app..." (++$step)
    Assert-Command "flutter build windows" "flutter build windows failed"

    $buildOutput = Join-Path $ctx.SampleDir "build\windows\x64\runner\Release"
    Assert-DirectoryExists $buildOutput "Flutter build output"
    Assert-FileExists (Join-Path $buildOutput "flutter_app.exe") "flutter_app.exe"

    # ------------------------------------------------------------------
    # Prepare distribution folder and package MSIX
    # ------------------------------------------------------------------
    Write-TestStep "Preparing distribution folder..." (++$step)
    $distDir = Join-Path $ctx.SampleDir "dist"
    if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
    Copy-Item $buildOutput -Destination $distDir -Recurse

    Write-TestStep "Generating development certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "Failed to generate dev certificate"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack dist --cert devcert.pfx" "winapp pack failed"

    # ------------------------------------------------------------------
    # Validate MSIX was created
    # ------------------------------------------------------------------
    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory $ctx.SampleDir -Description "flutter-app MSIX package"

    Complete-SampleTest -Context $ctx

} finally {
    Pop-Location
    if (-not $SkipCleanup) {
        Remove-Item -Path (Join-Path $ctx.SampleDir "build") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "dist") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir ".winapp") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
        Get-ChildItem -Path $ctx.SampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
