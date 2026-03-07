<#
.SYNOPSIS
Test script for the cpp-app sample.

.DESCRIPTION
Restores Windows App SDK headers via winapp, builds the C++ app with CMake,
then packages it as an MSIX.

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

$ctx = New-SampleTestContext -SampleName "cpp-app" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0

try {
    Push-Location $ctx.SampleDir

    # ------------------------------------------------------------------
    # Prerequisites
    # ------------------------------------------------------------------
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "cmake" -DisplayName "CMake"
    Assert-Prerequisite "npm" -DisplayName "npm"

    # ------------------------------------------------------------------
    # Install winapp globally (CMakeLists.txt calls winapp commands)
    # ------------------------------------------------------------------
    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ------------------------------------------------------------------
    # Restore Windows App SDK headers
    # ------------------------------------------------------------------
    Write-TestStep "Restoring Windows App SDK packages..." (++$step)
    Assert-Command "winapp restore" "winapp restore failed"
    Assert-DirectoryExists ".winapp" ".winapp directory"

    # ------------------------------------------------------------------
    # Configure CMake (Release to avoid debug-identity requirement)
    # ------------------------------------------------------------------
    Write-TestStep "Configuring CMake project..." (++$step)
    Assert-Command "cmake -B build -DCMAKE_BUILD_TYPE=Release" "CMake configure failed"

    # ------------------------------------------------------------------
    # Build
    # ------------------------------------------------------------------
    Write-TestStep "Building C++ app..." (++$step)
    Assert-Command "cmake --build build --config Release" "CMake build failed"

    $buildOutput = Join-Path $ctx.SampleDir "build\Release"
    Assert-FileExists (Join-Path $buildOutput "cpp-app.exe") "cpp-app.exe"

    # ------------------------------------------------------------------
    # Generate certificate and package MSIX
    # ------------------------------------------------------------------
    Write-TestStep "Generating development certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "Failed to generate dev certificate"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack `"$buildOutput`" --manifest appxmanifest.xml --cert devcert.pfx" "winapp pack failed"

    # ------------------------------------------------------------------
    # Validate MSIX was created
    # ------------------------------------------------------------------
    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory $ctx.SampleDir -Description "cpp-app MSIX package"

    Complete-SampleTest -Context $ctx

} finally {
    Pop-Location
    if (-not $SkipCleanup) {
        Remove-Item -Path (Join-Path $ctx.SampleDir "build") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir ".winapp") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
        Get-ChildItem -Path $ctx.SampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
