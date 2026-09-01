#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Builds the winapp UI Automation library NuGet packages.
.DESCRIPTION
    Packs the two library packages that ship the UI Automation engine:

      * Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation
        Inspection, selectors, UIA pattern interaction, input injection and window capture.
        Multi-targets net10.0-windows (GDI capture) and net10.0-windows10.0.19041.0 (adds
        Windows Graphics Capture).

      * Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording
        Video recording to H.264 MP4. Split out so projects that only inspect and drive UI do
        not take a dependency on SkiaSharp, whose native binary is ~9 MB per architecture.

    These are ordinary libraries, so unlike package-nuget.ps1 (which wraps the published CLI
    binaries and therefore needs artifacts/cli to exist) this script builds from source and can
    run on its own.

    Output goes to artifacts/nuget, alongside the CLI tools package, so a single glob picks up
    everything for signing, artifact upload and publishing.
.PARAMETER Version
    Package version, e.g. "1.0.0" or "1.0.0-prerelease.73". Defaults to version.json plus a
    prerelease suffix unless -Stable is set.
.PARAMETER Stable
    Use the bare version from version.json with no prerelease suffix.
.PARAMETER SkipBuild
    Pack without rebuilding, using whatever is already in each project's Release output. Use this
    when the assemblies have been code-signed after the build and must be packed as-is.
.EXAMPLE
    .\scripts\generate-nuget.ps1
    .\scripts\generate-nuget.ps1 -Version "1.0.0" -Stable
    .\scripts\generate-nuget.ps1 -SkipBuild
#>

param(
    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [switch]$Stable = $false,

    [Parameter(Mandatory = $false)]
    [switch]$SkipBuild = $false
)

$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Push-Location $ProjectRoot
try
{
    $OutputPath = Join-Path $ProjectRoot "artifacts\nuget"

    $Projects = @(
        @{
            Name = 'Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation'
            Path = Join-Path $ProjectRoot 'src\winapp-CLI\WinApp.UIAutomation\WinApp.UIAutomation.csproj'
        },
        @{
            Name = 'Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording'
            Path = Join-Path $ProjectRoot 'src\winapp-CLI\WinApp.UIAutomation.Recording\WinApp.UIAutomation.Recording.csproj'
        }
    )

    Write-Host "[NUGET] Building UI Automation library packages..." -ForegroundColor Green

    # ============================================================================
    # Resolve the version
    # ============================================================================
    if ([string]::IsNullOrEmpty($Version)) {
        Write-Host "[VERSION] Calculating package version..." -ForegroundColor Blue

        $VersionJsonPath = Join-Path $ProjectRoot "version.json"
        if (-not (Test-Path $VersionJsonPath)) {
            Write-Error "version.json not found at $VersionJsonPath"
            exit 1
        }

        $BaseVersion = (Get-Content $VersionJsonPath | ConvertFrom-Json).version

        if ($Stable) {
            $Version = $BaseVersion
            Write-Host "[VERSION] Using stable version (no prerelease suffix)" -ForegroundColor Cyan
        } else {
            $BuildNumber = & (Join-Path $PSScriptRoot "get-build-number.ps1")
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to get build number"
                exit 1
            }
            $Version = "$BaseVersion-prerelease.$BuildNumber"
            Write-Host "[VERSION] Using prerelease version (with prerelease suffix)" -ForegroundColor Cyan
        }
    }

    Write-Host "[VERSION] Package version: $Version" -ForegroundColor Cyan

    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
        Write-Host "[SETUP] Created output directory: $OutputPath" -ForegroundColor Blue
    }

    # ============================================================================
    # Pack
    # ============================================================================
    foreach ($Project in $Projects)
    {
        Write-Host ""
        Write-Host "[PACK] $($Project.Name)..." -ForegroundColor Blue

        $packArgs = @(
            'pack', $Project.Path,
            '-c', 'Release',
            '-o', $OutputPath,
            "/p:Version=$Version",
            "/p:PackageVersion=$Version"
        )
        if ($SkipBuild) {
            # Pack exactly what is on disk — used when the assemblies were signed after building.
            $packArgs += '--no-build'
        }

        dotnet @packArgs

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create $($Project.Name) NuGet package"
            exit 1
        }

        Write-Host "[PACK] $($Project.Name) created" -ForegroundColor Green
    }

    # ============================================================================
    # Summary
    # ============================================================================
    Write-Host ""
    Write-Host "[SUCCESS] UI Automation library packages created!" -ForegroundColor Green
    Write-Host "[VERSION] Package version: $Version" -ForegroundColor Cyan
    Write-Host ""
    foreach ($Project in $Projects) {
        # Exact file name — a wildcard on the base package id also matches the .Recording one.
        $package = Join-Path $OutputPath "$($Project.Name).$Version.nupkg"
        if (Test-Path $package) {
            $size = [math]::Round((Get-Item $package).Length / 1MB, 2)
            Write-Host "  * $(Split-Path $package -Leaf) ($size MB)" -ForegroundColor Gray
        }
    }
}
finally
{
    Pop-Location
}
