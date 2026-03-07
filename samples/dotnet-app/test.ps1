<#
.SYNOPSIS
Test script for the dotnet-app sample.

.DESCRIPTION
Builds the .NET console app in Release mode, which triggers the automatic
MSIX packaging MSBuild target, and validates the output.

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

$ctx = New-SampleTestContext -SampleName "dotnet-app" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0

try {
    Push-Location $ctx.SampleDir

    # ------------------------------------------------------------------
    # Prerequisites
    # ------------------------------------------------------------------
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "dotnet" -DisplayName ".NET SDK"
    Assert-Prerequisite "npm" -DisplayName "npm"

    # ------------------------------------------------------------------
    # Install winapp globally (MSBuild targets call winapp directly)
    # ------------------------------------------------------------------
    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ------------------------------------------------------------------
    # Restore NuGet packages
    # ------------------------------------------------------------------
    Write-TestStep "Restoring NuGet packages..." (++$step)
    Assert-Command "dotnet restore" "dotnet restore failed"

    # ------------------------------------------------------------------
    # Generate dev certificate (required by Release MSBuild target)
    # ------------------------------------------------------------------
    Write-TestStep "Generating development certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "Failed to generate dev certificate"
    Assert-FileExists "devcert.pfx" "Development certificate"

    # ------------------------------------------------------------------
    # Build Release (triggers automatic MSIX packaging)
    # ------------------------------------------------------------------
    Write-TestStep "Building in Release mode (auto-packages MSIX)..." (++$step)
    Assert-Command "dotnet build -c Release" "dotnet build -c Release failed"

    # ------------------------------------------------------------------
    # Validate MSIX was created
    # ------------------------------------------------------------------
    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory $ctx.SampleDir -Description "dotnet-app MSIX package"

    Complete-SampleTest -Context $ctx

} finally {
    Pop-Location
    if (-not $SkipCleanup) {
        # Clean up generated artifacts (keep source files)
        Remove-Item -Path (Join-Path $ctx.SampleDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
        Get-ChildItem -Path $ctx.SampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
