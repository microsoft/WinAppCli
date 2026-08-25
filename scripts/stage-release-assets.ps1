#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Renames built packages to the unversioned asset names the GitHub release publishes.

.DESCRIPTION
    The release attaches assets under stable, version-free names so the WinGet manifest
    and the docs can hardcode their URLs. This script owns those renames.

    It is shared by the real release (.pipelines/release.yml, Release_GitHub) and the
    weekly dry run (.pipelines/dryrun.yml), so the dry run exercises the actual renaming
    code rather than a copy of it that can drift. Renaming has broken releases before -
    see PR #201 and #568 (wingetcreate installer count mismatch).

    -Verify additionally asserts that the resulting asset set is exactly what the WinGet
    submission and the release notes expect. Without it the renames are best-effort, which
    is how the inline version behaved.

.PARAMETER MsixPath
    Directory holding winappcli_<version>_<arch>.msix files.

.PARAMETER NpmPath
    Directory holding microsoft-winappcli-<version>.tgz.

.PARAMETER NuGetPath
    Directory holding <id>.<version>.nupkg files.

.PARAMETER Verify
    Assert the expected asset names exist after renaming. Throws when any are missing.

.PARAMETER ExpectedArchitectures
    Architectures that must be present when -Verify is used. Defaults to x64 and arm64.

.EXAMPLE
    .\scripts\stage-release-assets.ps1 -MsixPath ./msix -NpmPath ./npm -NuGetPath ./nuget -Verify
#>

param(
    [string]$MsixPath,
    [string]$NpmPath,
    [string]$NuGetPath,
    [switch]$Verify,
    [string[]]$ExpectedArchitectures = @('x64', 'arm64')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Rename-Assets {
    param(
        [string]$Path,
        [string]$Filter,
        [scriptblock]$NewName,
        [string]$Label
    )

    if (-not $Path) {
        return @()
    }

    if (-not (Test-Path $Path)) {
        Write-Host "[STAGE] $Label`: '$Path' does not exist - skipping"
        return @()
    }

    $renamed = @()
    foreach ($file in Get-ChildItem $Path -Filter $Filter -File) {
        $target = & $NewName $file.Name
        if ($target -eq $file.Name) {
            Write-Host "[STAGE] $Label`: $($file.Name) already named correctly"
        }
        else {
            Rename-Item -Path $file.FullName -NewName $target
            Write-Host "[STAGE] $Label`: $($file.Name) -> $target"
        }
        $renamed += $target
    }

    if (-not $renamed) {
        Write-Host "[STAGE] $Label`: no '$Filter' files found in '$Path'"
    }

    # Callers wrap this in @() - PowerShell unrolls a single-element array on return, and the
    # .Count checks below fail under StrictMode when that leaves a bare string.
    return $renamed
}

# winappcli_0.6.3.12_x64.msix -> winappcli_x64.msix
$msix = @(Rename-Assets -Path $MsixPath -Filter '*.msix' -Label 'MSIX' -NewName {
        param($name) $name -replace 'winappcli_(.+?)_(.+)', 'winappcli_$2'
    })

# microsoft-winappcli-0.6.3.tgz -> microsoft-winappcli.tgz
$npm = @(Rename-Assets -Path $NpmPath -Filter '*.tgz' -Label 'NPM' -NewName {
        param($name) $name -replace 'microsoft-winappcli-(.+?)\.tgz', 'microsoft-winappcli.tgz'
    })

# BuildTools.WinApp.0.6.3.nupkg -> BuildTools.WinApp.nupkg
$nuget = @(Rename-Assets -Path $NuGetPath -Filter '*.nupkg' -Label 'NuGet' -NewName {
        param($name) $name -replace '\.(\d+\.\d+\.\d+.*)\.nupkg$', '.nupkg'
    })

if (-not $Verify) {
    return
}

Write-Host ''
Write-Host '[STAGE] Verifying release asset names...'

$errors = @()

if ($MsixPath) {
    foreach ($arch in $ExpectedArchitectures) {
        $expected = "winappcli_$arch.msix"
        if ($msix -notcontains $expected) {
            $errors += "Missing MSIX asset '$expected'. Found: $($msix -join ', ')"
        }
    }
    # wingetcreate is given a fixed-length URL list, so a stray extra MSIX shifts the
    # installer/URL pairing and the submission fails (#568).
    if ($msix.Count -ne $ExpectedArchitectures.Count) {
        $errors += "Expected exactly $($ExpectedArchitectures.Count) MSIX assets, found $($msix.Count): $($msix -join ', ')"
    }
}

if ($NpmPath) {
    if ($npm -notcontains 'microsoft-winappcli.tgz') {
        $errors += "Missing npm asset 'microsoft-winappcli.tgz'. Found: $($npm -join ', ')"
    }
}

if ($NuGetPath) {
    if (-not $nuget) {
        $errors += 'No .nupkg assets were produced.'
    }
    foreach ($package in $nuget) {
        # A version left in the name means the regex missed, and the published URL would
        # change every release.
        if ($package -match '\d+\.\d+\.\d+') {
            $errors += "NuGet asset '$package' still contains a version - the rename did not apply."
        }
    }
}

if ($errors) {
    foreach ($e in $errors) {
        Write-Host "[STAGE] ERROR: $e" -ForegroundColor Red
    }
    throw "Release asset verification failed with $($errors.Count) error(s)."
}

Write-Host '[STAGE] All expected release assets are present.' -ForegroundColor Green
