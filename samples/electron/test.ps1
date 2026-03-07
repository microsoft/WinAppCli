<#
.SYNOPSIS
Test script for the electron sample.

.DESCRIPTION
Installs dependencies, builds C++ and C# native addons, packages the
Electron app with Forge, generates a certificate, and creates an MSIX.

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

$ctx = New-SampleTestContext -SampleName "electron" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0

try {
    Push-Location $ctx.SampleDir

    # ------------------------------------------------------------------
    # Prerequisites
    # ------------------------------------------------------------------
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "node" -DisplayName "Node.js"
    Assert-Prerequisite "npm" -DisplayName "npm"
    Assert-Prerequisite "dotnet" -DisplayName ".NET SDK"

    # ------------------------------------------------------------------
    # Set up npm cache to avoid ECOMPROMISED errors in CI
    # ------------------------------------------------------------------
    $npmCacheDir = Join-Path $ctx.SampleDir ".npm-cache"
    $null = New-Item -ItemType Directory -Path $npmCacheDir -Force
    $env:npm_config_cache = $npmCacheDir

    # ------------------------------------------------------------------
    # Install npm dependencies (skip postinstall to avoid debug-identity)
    # ------------------------------------------------------------------
    Write-TestStep "Installing npm dependencies..." (++$step)
    Assert-Command "npm install --ignore-scripts" "npm install failed"

    # ------------------------------------------------------------------
    # Install winapp as local dev dependency
    # ------------------------------------------------------------------
    Write-TestStep "Installing winapp npm package..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappNpmPackage -PackagePath $resolvedPkg

    # ------------------------------------------------------------------
    # Initialize winapp workspace (restore SDKs, generate config)
    # ------------------------------------------------------------------
    Write-TestStep "Initializing winapp workspace..." (++$step)
    Invoke-Winapp "init . --use-defaults --setup-sdks=stable" -FailMessage "winapp init failed"
    Assert-DirectoryExists ".winapp" ".winapp directory"

    # ------------------------------------------------------------------
    # Build C++ addon
    # ------------------------------------------------------------------
    Write-TestStep "Building C++ addon..." (++$step)
    Assert-Command "npm run build-addon" "C++ addon build failed"

    # ------------------------------------------------------------------
    # Build C# addon
    # ------------------------------------------------------------------
    Write-TestStep "Building C# addon..." (++$step)
    Assert-Command "npm run build-csAddon" "C# addon build failed"

    # ------------------------------------------------------------------
    # Package Electron app with Forge
    # ------------------------------------------------------------------
    Write-TestStep "Packaging Electron app..." (++$step)
    Assert-Command "npm run package" "Electron packaging failed"

    $outDir = Join-Path $ctx.SampleDir "out"
    Assert-DirectoryExists $outDir "Electron output directory"

    # Find the packaged app directory
    $appPackageDirs = Get-ChildItem -Path $outDir -Directory -ErrorAction SilentlyContinue
    if (-not $appPackageDirs) {
        Write-TestError "No app package directories found in $outDir"
        throw "Electron app packaging did not create output directory"
    }
    $appPackageDir = $appPackageDirs[0].FullName
    Write-TestSuccess "Electron app packaged to: $appPackageDir"

    # ------------------------------------------------------------------
    # Generate certificate and package MSIX
    # ------------------------------------------------------------------
    Write-TestStep "Generating development certificate..." (++$step)
    $certPath = New-DevCertificate

    Write-TestStep "Packaging as MSIX..." (++$step)
    Invoke-Winapp "pack `"$appPackageDir`" --cert `"$certPath`"" -FailMessage "winapp pack failed"

    # ------------------------------------------------------------------
    # Validate MSIX was created
    # ------------------------------------------------------------------
    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory $ctx.SampleDir -Description "electron MSIX package"

    Complete-SampleTest -Context $ctx

} finally {
    Pop-Location
    if (-not $SkipCleanup) {
        Remove-Item -Path (Join-Path $ctx.SampleDir "out") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir ".npm-cache") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir ".winapp") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "devcert.pfx") -Force -ErrorAction SilentlyContinue
        Get-ChildItem -Path $ctx.SampleDir -Filter "*.msix" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
