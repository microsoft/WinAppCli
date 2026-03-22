#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Package Windows App Development CLI as a NuGet package
.DESCRIPTION
    This script creates the Microsoft.Windows.SDK.BuildTools.WinApp NuGet package - MSBuild integration for 'dotnet run' with packaged apps.
    This package includes the CLI binaries from artifacts/cli and outputs the .nupkg file to artifacts/nuget.
.PARAMETER Version
    Version number for the NuGet package (e.g., "1.0.0" or "1.0.0-prerelease.73").
    If not specified, reads from version.json and calculates based on Stable flag.
.PARAMETER Stable
    Use stable build configuration (default: false, uses prerelease config)
.EXAMPLE
    .\scripts\package-nuget.ps1
    .\scripts\package-nuget.ps1 -Version "1.0.0" -Stable
    .\scripts\package-nuget.ps1 -Version "1.0.0-prerelease.73"
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
    $OutputPath = Join-Path $ProjectRoot "artifacts\nuget"
    $ExtrasProjectPath = Join-Path $ProjectRoot "src\winapp-NuGet"
    
    Write-Host "[NUGET] Starting NuGet package creation..." -ForegroundColor Green
    Write-Host "[INFO] Project root: $ProjectRoot" -ForegroundColor Gray
    Write-Host "[INFO] CLI binaries path: $CliBinariesPath" -ForegroundColor Gray
    Write-Host "[INFO] Output path: $OutputPath" -ForegroundColor Gray
    
    # Validate that the CLI binaries path exists
    if (-not (Test-Path $CliBinariesPath)) {
        Write-Error "CLI binaries path does not exist: $CliBinariesPath"
        Write-Error "Run 'scripts\build-cli.ps1' first to build the CLI binaries."
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

    # ============================================================================
    # Step 1: Build Microsoft.Windows.SDK.BuildTools.WinApp package
    # ============================================================================
    Write-Host ""
    Write-Host "[NUGET] Building Microsoft.Windows.SDK.BuildTools.WinApp package..." -ForegroundColor Blue
    
    # Create tools directory structure in the NuGet project
    $ExtrasToolsPath = Join-Path $ExtrasProjectPath "tools"
    
    # Clean and recreate tools directory
    if (Test-Path $ExtrasToolsPath) {
        Remove-Item $ExtrasToolsPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ExtrasToolsPath -Force | Out-Null
    
    # Copy CLI binaries to tools folder
    Write-Host "[COPY] Copying CLI binaries to tools folder..." -ForegroundColor Blue
    
    $ToolsX64Path = Join-Path $ExtrasToolsPath "win-x64"
    $ToolsArm64Path = Join-Path $ExtrasToolsPath "win-arm64"
    
    New-Item -ItemType Directory -Path $ToolsX64Path -Force | Out-Null
    New-Item -ItemType Directory -Path $ToolsArm64Path -Force | Out-Null
    
    # Copy all files except PDBs (includes native runtime dependencies like libSkiaSharp.dll)
    Get-ChildItem -Path $X64Path -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
        Copy-Item $_.FullName $ToolsX64Path -Force
    }
    Get-ChildItem -Path $Arm64Path -File | Where-Object { $_.Extension -ne '.pdb' } | ForEach-Object {
        Copy-Item $_.FullName $ToolsArm64Path -Force
    }
    
    Write-Host "[COPY] CLI binaries copied successfully" -ForegroundColor Green
    
    # Pack the NuGet package
    Write-Host "[PACK] Creating NuGet package..." -ForegroundColor Blue
    
    $ExtrasCsproj = Join-Path $ExtrasProjectPath "Microsoft.Windows.SDK.BuildTools.WinApp.csproj"
    
    dotnet pack $ExtrasCsproj -c Release -o $OutputPath /p:Version=$Version /p:PackageVersion=$Version
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to create Microsoft.Windows.SDK.BuildTools.WinApp NuGet package"
        exit 1
    }
    
    Write-Host "[NUGET] Microsoft.Windows.SDK.BuildTools.WinApp package created successfully!" -ForegroundColor Green

    # ============================================================================
    # Summary
    # ============================================================================
    Write-Host ""
    Write-Host "[SUCCESS] NuGet packages created successfully!" -ForegroundColor Green
    Write-Host "[VERSION] Package version: $Version" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Packages created:" -ForegroundColor White
    Get-ChildItem $OutputPath -Filter "*.nupkg" | ForEach-Object {
        $size = [math]::Round($_.Length / 1MB, 2)
        Write-Host "  * $($_.Name) ($size MB)" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "[INFO] To test locally, add the output path as a NuGet source:" -ForegroundColor Cyan
    Write-Host "  dotnet nuget add source `"$OutputPath`" --name WinAppLocal" -ForegroundColor Gray
    Write-Host ""
    Write-Host "[INFO] And create a new project:" -ForegroundColor Cyan
    Write-Host "  dotnet new winui -n MyApp" -ForegroundColor Gray
    Write-Host "  cd MyApp" -ForegroundColor Gray
    Write-Host "  dotnet run" -ForegroundColor Gray
}
finally
{
    # Restore original working directory
    Pop-Location
}
