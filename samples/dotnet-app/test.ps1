<#
.SYNOPSIS
Test script for the dotnet-app sample and .NET guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/dotnet.md guide from scratch — creates a new
  .NET console project, runs winapp init, builds in Release (auto-packages MSIX).
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

$ctx = New-SampleTestContext -SampleName "dotnet-app" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "dotnet" -DisplayName ".NET SDK"
    Assert-Prerequisite "npm" -DisplayName "npm"

    # Install winapp globally (MSBuild targets call winapp directly)
    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ==================================================================
    # Phase 1 — Guide Workflow (from scratch)
    # ==================================================================
    Write-TestHeader "Phase 1: .NET Guide Workflow (from scratch)"

    $tempDir = New-TempTestDirectory -Prefix "dotnet-guide"
    Push-Location $tempDir

    Write-TestStep "Creating new .NET console project..." (++$step)
    Assert-Command "dotnet new console -n test-dotnet-app" "dotnet new console failed"
    Push-Location "test-dotnet-app"

    Write-TestStep "Running winapp init..." (++$step)
    Assert-Command "winapp init --use-defaults" "winapp init failed"
    Assert-WinappInitOutput -ExpectWinappYaml -ExpectManifest

    Write-TestStep "Generating dev certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip" "cert generate failed"
    Assert-FileExists "devcert.pfx" "Development certificate"

    Write-TestStep "Verifying certificate info..." (++$step)
    Assert-CertInfo -CertPath "devcert.pfx"

    Write-TestStep "Building in Release mode (auto-packages MSIX)..." (++$step)
    Assert-Command "dotnet build -c Release" "dotnet build -c Release failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory (Get-Location) -Description "Guide dotnet-app MSIX"

    Pop-Location  # back to tempDir
    Pop-Location  # back to original

    # ==================================================================
    # Phase 2 — Sample Build Check
    # ==================================================================
    Write-TestHeader "Phase 2: Sample Build Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Building existing sample (Debug, skip identity)..." (++$step)
    Assert-Command "dotnet restore" "dotnet restore failed"
    Assert-Command "dotnet build -c Debug /p:ApplyDebugIdentity=false" "Sample build failed"
    Write-TestSuccess "dotnet-app sample builds successfully"

    Pop-Location

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        if ($tempDir) { Remove-TempTestDirectory -Path $tempDir }
        Remove-Item -Path (Join-Path $ctx.SampleDir "bin") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "obj") -Recurse -Force -ErrorAction SilentlyContinue
    }
}
