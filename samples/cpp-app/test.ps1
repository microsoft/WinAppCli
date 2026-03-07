<#
.SYNOPSIS
Test script for the cpp-app sample and C++/CMake guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/cpp.md guide from scratch — creates a minimal
  C++ project, runs winapp init, builds with CMake, and packages as MSIX.
Phase 2: Quick build of the existing sample to verify it is not stale.

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
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "cmake" -DisplayName "CMake"
    Assert-Prerequisite "npm" -DisplayName "npm"

    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ==================================================================
    # Phase 1 — Guide Workflow (from scratch)
    # ==================================================================
    Write-TestHeader "Phase 1: C++/CMake Guide Workflow (from scratch)"

    $tempDir = New-TempTestDirectory -Prefix "cpp-guide"
    Push-Location $tempDir

    Write-TestStep "Creating minimal C++ project..." (++$step)
    # Minimal main.cpp that uses Windows APIs (matches guide)
    @'
#include <windows.h>
#include <iostream>
int main() {
    std::cout << "Hello from C++ app" << std::endl;
    return 0;
}
'@ | Set-Content "main.cpp"

    @'
cmake_minimum_required(VERSION 3.20)
project(test-cpp-app LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
add_executable(test-cpp-app main.cpp)
'@ | Set-Content "CMakeLists.txt"

    Write-TestStep "Running winapp init..." (++$step)
    Assert-Command "winapp init --use-defaults --setup-sdks=stable" "winapp init failed"
    Assert-WinappInitOutput -ExpectWinappYaml -ExpectManifest -ExpectDotWinapp

    Write-TestStep "Configuring CMake project..." (++$step)
    Assert-Command "cmake -B build -DCMAKE_BUILD_TYPE=Release" "CMake configure failed"

    Write-TestStep "Building C++ app..." (++$step)
    Assert-Command "cmake --build build --config Release" "CMake build failed"

    Write-TestStep "Generating dev certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "cert generate failed"

    Write-TestStep "Verifying certificate info..." (++$step)
    Assert-CertInfo -CertPath "devcert.pfx"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack build\Release --manifest appxmanifest.xml --cert devcert.pfx" "winapp pack failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory (Get-Location) -Description "Guide cpp-app MSIX"

    Pop-Location  # back to original

    # ==================================================================
    # Phase 2 — Sample Build Check
    # ==================================================================
    Write-TestHeader "Phase 2: Sample Build Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Restoring sample SDK packages..." (++$step)
    Assert-Command "winapp restore" "winapp restore failed"

    Write-TestStep "Building existing sample..." (++$step)
    Assert-Command "cmake -B build -DCMAKE_BUILD_TYPE=Release" "Sample CMake configure failed"
    Assert-Command "cmake --build build --config Release" "Sample CMake build failed"
    Assert-FileExists "build\Release\cpp-app.exe" "cpp-app.exe"

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
