#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Package Windows App Development CLI as VS Code extension (VSIX)
.DESCRIPTION
    This script creates a VSIX package from pre-built CLI binaries for x64 and arm64 architectures.
    Uses artifacts/cli for binaries and outputs to artifacts directory.
.PARAMETER Version
    Version number for the VSIX package (e.g., "0.1.0" or "0.1.0-prerelease.73").
    If not specified, reads from version.json and calculates based on Stable flag.
.PARAMETER Stable
    Use stable build configuration (default: false, uses prerelease config)
.EXAMPLE
    .\scripts\package-vsc.ps1
    .\scripts\package-vsc.ps1 -Version "0.1.0" -Stable
    .\scripts\package-vsc.ps1 -Version "0.1.0-prerelease.73"
#>

param(
    [Parameter(Mandatory=$false)]
    [string]$Version,

    [Parameter(Mandatory=$false)]
    [switch]$Stable = $false
)

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Push-Location $ProjectRoot
try
{
    # Define standard paths
    $CliBinariesPath = Join-Path $ProjectRoot "artifacts\cli"
    $OutputPath = Join-Path $ProjectRoot "artifacts"

    Write-Host "[VSC] Starting VS Code extension packaging..." -ForegroundColor Green
    Write-Host "[INFO] Project root: $ProjectRoot" -ForegroundColor Gray
    Write-Host "[INFO] CLI binaries path: $CliBinariesPath" -ForegroundColor Gray
    Write-Host "[INFO] Output path: $OutputPath" -ForegroundColor Gray

    # Validate that the CLI binaries path exists
    if (-not (Test-Path $CliBinariesPath)) {
        Write-Error "CLI binaries path does not exist: $CliBinariesPath. Run build-cli.ps1 first."
        exit 1
    }

    # Validate that required architecture folders exist
    $X64Path = Join-Path $CliBinariesPath "win-x64"
    $Arm64Path = Join-Path $CliBinariesPath "win-arm64"

    if (-not (Test-Path $X64Path)) {
        Write-Error "win-x64 folder not found at: $X64Path"
        exit 1
    }

    if (-not (Test-Path $Arm64Path)) {
        Write-Error "win-arm64 folder not found at: $Arm64Path"
        exit 1
    }

    Write-Host "[VALIDATE] Found CLI binaries:" -ForegroundColor Green
    Write-Host "  - x64: $X64Path" -ForegroundColor Gray
    Write-Host "  - arm64: $Arm64Path" -ForegroundColor Gray

    # Validate that the main executable exists in both folders
    $X64Exe = Join-Path $X64Path "winapp.exe"
    $Arm64Exe = Join-Path $Arm64Path "winapp.exe"

    if (-not (Test-Path $X64Exe)) {
        Write-Error "winapp.exe not found in x64 folder: $X64Exe"
        exit 1
    }

    if (-not (Test-Path $Arm64Exe)) {
        Write-Error "winapp.exe not found in arm64 folder: $Arm64Exe"
        exit 1
    }

    Write-Host "[VALIDATE] All required files found!" -ForegroundColor Green

    # Calculate version if not provided
    if ([string]::IsNullOrEmpty($Version)) {
        Write-Host "[VERSION] Calculating package version..." -ForegroundColor Blue

        # Read base version from version.json
        $VersionJsonPath = "$ProjectRoot\version.json"
        if (-not (Test-Path $VersionJsonPath)) {
            Write-Error "version.json not found at $VersionJsonPath"
            exit 1
        }

        $VersionJson = Get-Content $VersionJsonPath | ConvertFrom-Json
        $BaseVersion = $VersionJson.version

        # Get build number
        $GetBuildNumberScript = Join-Path $PSScriptRoot "get-build-number.ps1"
        $BuildNumber = & $GetBuildNumberScript
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to get build number"
            exit 1
        }

        # Construct full version based on Stable flag
        if ($Stable) {
            $Version = $BaseVersion
            Write-Host "[VERSION] Using stable version (no prerelease suffix)" -ForegroundColor Cyan
        } else {
            $Version = "$BaseVersion-prerelease.$BuildNumber"
            Write-Host "[VERSION] Using prerelease version (with prerelease suffix)" -ForegroundColor Cyan
        }
    }

    Write-Host "[VERSION] Package version: $Version" -ForegroundColor Cyan

    # Ensure output directory exists
    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
        Write-Host "[SETUP] Created output directory: $OutputPath" -ForegroundColor Blue
    }

    # Navigate to VSC project directory
    $VscProjectPath = Join-Path $ProjectRoot "src\winapp-VSC"
    if (-not (Test-Path $VscProjectPath)) {
        Write-Error "VS Code extension project path does not exist: $VscProjectPath"
        exit 1
    }

    Write-Host "[VSC] Preparing VS Code extension..." -ForegroundColor Blue

    Push-Location $VscProjectPath

    # Clean bin and out directories
    npm run clean
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "npm clean failed, continuing..."
    }

    # Install dependencies
    Write-Host "[VSC] Installing dependencies..." -ForegroundColor Blue
    npm ci
    if ($LASTEXITCODE -ne 0) {
        Write-Error "npm ci failed"
        Pop-Location
        exit 1
    }

    # Compile TypeScript
    Write-Host "[VSC] Compiling TypeScript..." -ForegroundColor Blue
    npm run compile
    if ($LASTEXITCODE -ne 0) {
        Write-Error "TypeScript compilation failed"
        Pop-Location
        exit 1
    }

    # Copy CLI binaries from artifacts
    Write-Host "[VSC] Copying CLI binaries to extension..." -ForegroundColor Blue
    $VscBinPath = "bin"
    New-Item -ItemType Directory -Path "$VscBinPath\win-x64" -Force | Out-Null
    New-Item -ItemType Directory -Path "$VscBinPath\win-arm64" -Force | Out-Null

    Copy-Item "$CliBinariesPath\win-x64\*.exe" "$VscBinPath\win-x64\" -Force
    Copy-Item "$CliBinariesPath\win-arm64\*.exe" "$VscBinPath\win-arm64\" -Force

    # Copy LICENSE from project root
    Copy-Item "$ProjectRoot\LICENSE" "LICENSE" -Force

    # Backup original package.json
    Write-Host "[VSC] Setting package version to $Version..." -ForegroundColor Blue
    $PackageJsonPath = "package.json"
    Copy-Item $PackageJsonPath "$PackageJsonPath.backup" -Force

    # Update package.json version temporarily
    $PackageJson = Get-Content $PackageJsonPath | ConvertFrom-Json
    $PackageJson.version = $Version
    $PackageJson | ConvertTo-Json -Depth 100 | Set-Content $PackageJsonPath

    # Check if vsce is available, install if needed
    $VsceCmd = Get-Command vsce -ErrorAction SilentlyContinue
    if (-not $VsceCmd) {
        Write-Host "[VSC] Installing @vscode/vsce..." -ForegroundColor Blue
        npm install -g @vscode/vsce
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to install @vscode/vsce"
            # Restore package.json before exiting
            Move-Item "$PackageJsonPath.backup" $PackageJsonPath -Force
            Pop-Location
            exit 1
        }
    }

    # Package the VSIX
    Write-Host "[PACK] Creating VSIX package..." -ForegroundColor Blue

    $RelativeOutputPath = [System.IO.Path]::GetRelativePath($VscProjectPath, $OutputPath)

    vsce package --no-dependencies -o "$RelativeOutputPath\winapp-$Version.vsix"
    $PackResult = $LASTEXITCODE

    # Restore original package.json
    Write-Host "[VSC] Restoring original package.json..." -ForegroundColor Blue
    if (Test-Path "$PackageJsonPath.backup") {
        Move-Item "$PackageJsonPath.backup" $PackageJsonPath -Force
    }

    # Remove copied LICENSE
    if (Test-Path "LICENSE") {
        Remove-Item "LICENSE" -Force
    }

    Pop-Location

    if ($PackResult -ne 0) {
        Write-Error "Failed to create VSIX package"
        exit 1
    }

    # Find the created VSIX and report success
    $CreatedVsix = Get-ChildItem -Path $OutputPath -Filter "winapp-*.vsix" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if ($CreatedVsix) {
        $VsixSize = [math]::Round($CreatedVsix.Length / 1MB, 2)
        Write-Host ""
        Write-Host "[SUCCESS] VS Code extension packaged successfully!" -ForegroundColor Green
        Write-Host "[INFO] Package: $($CreatedVsix.Name) ($VsixSize MB)" -ForegroundColor Cyan
        Write-Host "[INFO] Location: $($CreatedVsix.FullName)" -ForegroundColor Cyan
    } else {
        Write-Warning "VSIX was created but could not be located in $OutputPath"
    }

    Write-Host "[DONE] VS Code extension packaging complete!" -ForegroundColor Green
}
finally
{
    # Restore original working directory
    Pop-Location
}
