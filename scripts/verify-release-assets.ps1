#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Asserts a staged release asset set carries exactly the names the release publishes.

.DESCRIPTION
    The renaming itself lives in .pipelines/templates/release-assets.yaml, which both the real
    release and the weekly dry run run - so there is only one copy of that logic and it cannot
    drift. This script is the other half: it checks the *result*.

    The names matter because they are load-bearing. The WinGet manifest submission is handed a
    fixed-length, fixed-order URL list built from them, and the installation instructions in the
    release notes hardcode them. A rename that silently stops matching produces versioned names,
    which breaks the WinGet submission (#568) and every documented download link.

    Only the dry run runs this: it needs a repo checkout, and 1ES release jobs cannot check out.

.PARAMETER StagingPath
    Directory holding the renamed assets. Expects msix-packages\, npmpackage\, nuget-packages\
    subdirectories and winappcli-<arch>.zip files, matching the dry run's staging layout.

.PARAMETER ExpectedArchitectures
    Architectures that must be present. Defaults to x64 and arm64.

.PARAMETER SkipZip
    Skip the winappcli-<arch>.zip checks.

.EXAMPLE
    .\scripts\verify-release-assets.ps1 -StagingPath .\artifacts\release-staging
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$StagingPath,

    [string[]]$ExpectedArchitectures = @('x64', 'arm64'),

    [switch]$SkipZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $StagingPath)) {
    throw "Staging path '$StagingPath' does not exist."
}

function Get-AssetNames {
    param([string]$Sub, [string]$Filter)

    $dir = Join-Path $StagingPath $Sub
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Filter $Filter -File | Select-Object -ExpandProperty Name | Sort-Object)
}

Write-Host "[VERIFY] Checking release asset names under '$StagingPath'..."

$errors = [System.Collections.Generic.List[string]]::new()

# --- MSIX ---------------------------------------------------------------------
# @() at the call site: PowerShell unrolls a single-element array on return, and .Count
# on a bare string throws under StrictMode.
$msix = @(Get-AssetNames -Sub 'msix-packages' -Filter '*.msix')
foreach ($arch in $ExpectedArchitectures) {
    $expected = "winappcli_$arch.msix"
    if ($msix -notcontains $expected) {
        $errors.Add("Missing MSIX asset '$expected'. Found: $($msix -join ', ')")
    }
}
# wingetcreate is handed a fixed-length URL list, so a stray extra MSIX shifts the
# installer/URL pairing and the submission fails (#568).
if ($msix.Count -ne $ExpectedArchitectures.Count) {
    $errors.Add("Expected exactly $($ExpectedArchitectures.Count) MSIX assets, found $($msix.Count): $($msix -join ', ')")
}

# --- npm ----------------------------------------------------------------------
$npm = @(Get-AssetNames -Sub 'npmpackage' -Filter '*.tgz')
if ($npm -notcontains 'microsoft-winappcli.tgz') {
    $errors.Add("Missing npm asset 'microsoft-winappcli.tgz'. Found: $($npm -join ', ')")
}

# --- NuGet --------------------------------------------------------------------
$nuget = @(Get-AssetNames -Sub 'nuget-packages' -Filter '*.nupkg')
if (-not $nuget) {
    $errors.Add('No .nupkg assets were produced.')
}
foreach ($package in $nuget) {
    # A version left in the name means the rename missed, and the published URL would change
    # every release.
    if ($package -match '\d+\.\d+\.\d+') {
        $errors.Add("NuGet asset '$package' still contains a version - the rename did not apply.")
    }
}

# --- Portable zips ------------------------------------------------------------
if (-not $SkipZip) {
    foreach ($arch in $ExpectedArchitectures) {
        $zip = Join-Path $StagingPath "winappcli-$arch.zip"
        if (-not (Test-Path $zip)) {
            $errors.Add("Missing portable archive 'winappcli-$arch.zip'. The WinGet manifest hardcodes its URL.")
        }
        elseif ((Get-Item $zip).Length -le 0) {
            $errors.Add("Portable archive 'winappcli-$arch.zip' is empty.")
        }
    }
}

if ($errors.Count -gt 0) {
    foreach ($e in $errors) {
        Write-Host "[VERIFY] ERROR: $e" -ForegroundColor Red
    }
    throw "Release asset verification failed with $($errors.Count) error(s)."
}

Write-Host '[VERIFY] All expected release assets are present.' -ForegroundColor Green
