<#
.SYNOPSIS
Shared PowerShell helpers for sample & guide Pester tests.

.DESCRIPTION
This module provides setup and CLI helper functions used by each sample's
test.Tests.ps1 Pester test file. Assertion and reporting functions are handled
by Pester's built-in Should assertions — this module only provides:
  - CLI path resolution and installation
  - Prerequisite checks
  - Temp directory management
  - winapp invocation helpers

Import with: Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
#>

# ============================================================================
# Winapp CLI Helpers
# ============================================================================

function Resolve-WinappCliPath {
    <#
    .SYNOPSIS
    Resolves the winapp CLI path from artifacts or local build.
    Returns the resolved absolute path to a .tgz or package directory.
    #>
    param(
        [string]$WinappPath
    )

    $repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

    if (-not $WinappPath) {
        # Default search order: CI artifact dir, local package-npm.ps1 output dir, then source dir.
        $defaultCandidates = @(
            (Join-Path $repoRoot "artifacts\npm"),
            (Join-Path $repoRoot "artifacts"),
            (Join-Path $repoRoot "src\winapp-npm")
        )
        $WinappPath = $defaultCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $WinappPath -or -not (Test-Path $WinappPath)) {
        throw "Winapp path not found: $WinappPath"
    }

    $resolved = (Resolve-Path $WinappPath).Path

    if (Test-Path $resolved -PathType Container) {
        $tgz = Get-ChildItem -Path $resolved -Filter "*.tgz" -ErrorAction SilentlyContinue |
            Sort-Object -Property LastWriteTime -Descending |
            Select-Object -First 1
        if ($tgz) { return $tgz.FullName }
        if (Test-Path (Join-Path $resolved "package.json")) { return $resolved }
        throw "No .tgz or package.json found in $resolved"
    }

    return $resolved
}

function Invoke-WinappCommand {
    <#
    .SYNOPSIS
    Invokes the winapp CLI with the given arguments and returns stdout lines.
    Resolution order: local node_modules/.bin/winapp -> winapp on PATH ->
    dotnet run against the repo CLI project (only when WINAPP_TEST_USE_DOTNET=1
    or no other winapp is available). Throws on non-zero exit code.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Arguments,
        [string]$FailMessage = "winapp $Arguments failed"
    )

    $npxWinapp = Join-Path (Get-Location) "node_modules\.bin\winapp.cmd"
    $pathWinapp = Get-Command winapp -ErrorAction SilentlyContinue
    $cliProject = Join-Path $PSScriptRoot "..\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj"
    $useDotnet = $env:WINAPP_TEST_USE_DOTNET -eq '1'

    if (Test-Path $npxWinapp) {
        $cmd = "npx winapp $Arguments"
    } elseif ($pathWinapp -and -not $useDotnet) {
        $cmd = "winapp $Arguments"
    } elseif (Test-Path $cliProject) {
        # Fall back to dotnet run when no installed winapp is on PATH, or when explicitly requested.
        $cmd = "dotnet run --project `"$cliProject`" -- $Arguments"
    } else {
        $cmd = "winapp $Arguments"
    }

    Write-Verbose "Running: $cmd"
    $output = Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0) { throw $FailMessage }
    return $output
}

function Install-WinappNpmPackage {
    <#
    .SYNOPSIS
    Installs the winapp npm package into the current project as a devDependency.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )
    Write-Verbose "Installing winapp from: $PackagePath"
    Invoke-Expression "npm install `"$PackagePath`" --save-dev"
    if ($LASTEXITCODE -ne 0) { throw "Failed to install winapp npm package" }
}

function Install-WinappGlobal {
    <#
    .SYNOPSIS
    Installs the winapp npm package globally so 'winapp' is available on PATH.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )
    Write-Verbose "Installing winapp globally from: $PackagePath"
    Invoke-Expression "npm install -g `"$PackagePath`""
    if ($LASTEXITCODE -ne 0) { throw "Failed to install winapp globally" }
}

# ============================================================================
# Prerequisite Checks
# ============================================================================

function Test-Prerequisite {
    <#
    .SYNOPSIS
    Tests whether a command-line tool is available on PATH. Returns $true/$false.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Command
    )
    $null = Get-Command $Command -ErrorAction SilentlyContinue
    return $?
}

# ============================================================================
# Temp Directory Helpers
# ============================================================================

function New-TempTestDirectory {
    <#
    .SYNOPSIS
    Creates a temporary directory for from-scratch guide workflow tests.
    Returns the absolute path.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Prefix
    )
    $tempBase = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-test"
    $null = New-Item -ItemType Directory -Path $tempBase -Force
    $tempDir = Join-Path $tempBase "$Prefix-$([System.IO.Path]::GetRandomFileName())"
    $null = New-Item -ItemType Directory -Path $tempDir -Force
    return $tempDir
}

function Remove-TempTestDirectory {
    <#
    .SYNOPSIS
    Removes a temporary test directory created by New-TempTestDirectory.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )
    if (Test-Path $Path) {
        Remove-Item -Path $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ============================================================================
# Exports
# ============================================================================

Export-ModuleMember -Function @(
    'Resolve-WinappCliPath'
    'Invoke-WinappCommand'
    'Install-WinappNpmPackage'
    'Install-WinappGlobal'
    'Test-Prerequisite'
    'New-TempTestDirectory'
    'Remove-TempTestDirectory'
)
