<#
.SYNOPSIS
Shared PowerShell helpers for sample tests.

.DESCRIPTION
This module provides common test helper functions used by each sample's test.ps1 script.
Import with: Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
#>

# ============================================================================
# Logging Helpers
# ============================================================================

function Write-TestHeader {
    param([string]$Message)
    Write-Host "`n$('='*80)" -ForegroundColor Cyan
    Write-Host "TEST: $Message" -ForegroundColor Cyan
    Write-Host "$('='*80)`n" -ForegroundColor Cyan
}

function Write-TestStep {
    param([string]$Message, [int]$Step)
    Write-Host "[$Step] $Message" -ForegroundColor Yellow
}

function Write-TestSuccess {
    param([string]$Message)
    Write-Host "  ✓ $Message" -ForegroundColor Green
}

function Write-TestError {
    param([string]$Message)
    Write-Host "  ✗ $Message" -ForegroundColor Red
}

# ============================================================================
# Assertion Helpers
# ============================================================================

function Assert-ExitCode {
    <#
    .SYNOPSIS
    Asserts the last exit code was 0, throwing with the given message if not.
    #>
    param(
        [string]$FailMessage,
        [int]$Expected = 0
    )
    if ($LASTEXITCODE -ne $Expected) {
        Write-TestError "$FailMessage (exit code: $LASTEXITCODE, expected: $Expected)"
        throw $FailMessage
    }
}

function Assert-Command {
    <#
    .SYNOPSIS
    Runs a command string via Invoke-Expression, asserts exit code 0, and returns output.
    #>
    param(
        [string]$Command,
        [string]$FailMessage
    )
    Write-Verbose "Running: $Command"
    $output = Invoke-Expression $Command
    if ($LASTEXITCODE -ne 0) {
        Write-TestError $FailMessage
        throw $FailMessage
    }
    Write-TestSuccess $Command
    return $output
}

function Assert-FileExists {
    param(
        [string]$Path,
        [string]$Description
    )
    if (-not (Test-Path $Path)) {
        Write-TestError "$Description not found at $Path"
        throw "$Description not found at $Path"
    }
    Write-TestSuccess "$Description exists: $Path"
}

function Assert-DirectoryExists {
    param(
        [string]$Path,
        [string]$Description
    )
    if (-not (Test-Path $Path -PathType Container)) {
        Write-TestError "$Description not found at $Path"
        throw "$Description not found at $Path"
    }
    Write-TestSuccess "$Description exists: $Path"
}

function Assert-OutputContains {
    <#
    .SYNOPSIS
    Asserts that the given output string contains the expected substring.
    #>
    param(
        [string]$Output,
        [string]$Expected,
        [string]$Description
    )
    if ($Output -notmatch [regex]::Escape($Expected)) {
        Write-TestError "$Description — expected output to contain '$Expected'"
        throw "$Description — expected output to contain '$Expected'"
    }
    Write-TestSuccess "$Description — output contains '$Expected'"
}

# ============================================================================
# Winapp CLI Helpers
# ============================================================================

function Resolve-WinappCliPath {
    <#
    .SYNOPSIS
    Resolves the winapp CLI path from artifacts or local build.

    .DESCRIPTION
    Given -WinappPath, finds the npm tarball or package directory suitable for
    `npm install`. Returns the resolved absolute path. If nothing is provided,
    falls back to the default local build location.
    #>
    param(
        [string]$WinappPath
    )

    $repoRoot = (Resolve-Path "$PSScriptRoot\..").Path

    if (-not $WinappPath) {
        $WinappPath = Join-Path $repoRoot "artifacts\npm"
        if (-not (Test-Path $WinappPath)) {
            $WinappPath = Join-Path $repoRoot "src\winapp-npm"
        }
    }

    if (-not (Test-Path $WinappPath)) {
        throw "Winapp path not found: $WinappPath"
    }

    $resolved = (Resolve-Path $WinappPath).Path

    # If directory contains a .tgz, return the tgz path
    if (Test-Path $resolved -PathType Container) {
        $tgz = Get-ChildItem -Path $resolved -Filter "*.tgz" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($tgz) {
            return $tgz.FullName
        }
        if (Test-Path (Join-Path $resolved "package.json")) {
            return $resolved
        }
        throw "No .tgz or package.json found in $resolved"
    }

    # Direct file path (e.g., a .tgz)
    return $resolved
}

function Invoke-Winapp {
    <#
    .SYNOPSIS
    Invokes the winapp CLI with the given arguments.

    .DESCRIPTION
    Uses npx winapp if an npm package was installed in the current project,
    otherwise falls back to dotnet run with the WinApp.Cli project.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Arguments,
        [string]$FailMessage = "winapp $Arguments failed"
    )

    # Prefer npx if available in the project
    $npxWinapp = Join-Path (Get-Location) "node_modules\.bin\winapp.cmd"
    if (Test-Path $npxWinapp) {
        $cmd = "npx winapp $Arguments"
    } else {
        # Fallback to dotnet run
        $cliProject = Join-Path $PSScriptRoot "..\src\winapp-CLI\WinApp.Cli\WinApp.Cli.csproj"
        if (Test-Path $cliProject) {
            $cmd = "dotnet run --project `"$cliProject`" -- $Arguments"
        } else {
            # Last resort: assume winapp is on PATH
            $cmd = "winapp $Arguments"
        }
    }

    return Assert-Command -Command $cmd -FailMessage $FailMessage
}

function Install-WinappNpmPackage {
    <#
    .SYNOPSIS
    Installs the winapp npm package into the current project from a path or artifacts folder.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Write-Verbose "Installing winapp from: $PackagePath"
    Assert-Command "npm install `"$PackagePath`" --save-dev" "Failed to install winapp npm package"
    Assert-FileExists (Join-Path (Get-Location) "node_modules\.bin\winapp.cmd") "winapp CLI binary"
}

function Install-WinappGlobal {
    <#
    .SYNOPSIS
    Installs the winapp npm package globally so 'winapp' is available on PATH.
    Use for non-Node samples (C++, .NET, Rust, Flutter) that call winapp from
    build systems or the command line directly.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Write-Verbose "Installing winapp globally from: $PackagePath"
    Assert-Command "npm install -g `"$PackagePath`"" "Failed to install winapp globally"

    # Verify winapp is now on PATH
    try {
        $winappVersion = & winapp --version 2>&1 | Select-Object -First 1
        Write-TestSuccess "winapp installed globally: $winappVersion"
    } catch {
        Write-TestError "winapp not found on PATH after global install"
        throw "winapp global install did not put CLI on PATH"
    }
}

# ============================================================================
# Prerequisite Checks
# ============================================================================

function Assert-Prerequisite {
    <#
    .SYNOPSIS
    Asserts that a command-line tool is available on PATH.
    #>
    param(
        [string]$Command,
        [string]$DisplayName = $Command,
        [string]$VersionFlag = "--version"
    )
    try {
        $version = & $Command $VersionFlag 2>&1 | Select-Object -First 1
        Write-TestSuccess "$DisplayName found: $version"
    } catch {
        Write-TestError "$DisplayName is not installed or not in PATH"
        throw "$DisplayName is required but not found"
    }
}

# ============================================================================
# Test Environment Management
# ============================================================================

function New-SampleTestContext {
    <#
    .SYNOPSIS
    Initializes a test context for a sample test. Returns a context hashtable.

    .DESCRIPTION
    Sets strict mode, resolves the sample directory, and prepares the context
    object used by all sample tests. Does NOT create temporary directories —
    sample tests run in-place against the sample's own source directory.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$SampleName,
        [string]$WinappPath,
        [switch]$Verbose
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    if ($Verbose) { $VerbosePreference = 'Continue' }

    $sampleDir = $PSScriptRoot  # test.ps1 lives alongside sample files
    $repoRoot = (Resolve-Path "$sampleDir\..\..").Path

    $ctx = @{
        SampleName = $SampleName
        SampleDir  = $sampleDir
        RepoRoot   = $repoRoot
        WinappPath = $WinappPath
        StartTime  = Get-Date
    }

    Write-TestHeader "$SampleName Sample Test"
    Write-Verbose "Sample directory: $($ctx.SampleDir)"
    Write-Verbose "Repo root: $($ctx.RepoRoot)"

    return $ctx
}

function Complete-SampleTest {
    <#
    .SYNOPSIS
    Reports success and elapsed time for a sample test.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]$Context
    )
    $elapsed = (Get-Date) - $Context.StartTime
    Write-Host "`n$('='*80)" -ForegroundColor Green
    Write-Host "$($Context.SampleName) SAMPLE TEST COMPLETED SUCCESSFULLY ($([math]::Round($elapsed.TotalSeconds, 1))s)" -ForegroundColor Green
    Write-Host "$('='*80)`n" -ForegroundColor Green
}

# ============================================================================
# MSIX Packaging Helpers
# ============================================================================

function Assert-MsixCreated {
    <#
    .SYNOPSIS
    Asserts that at least one .msix file exists in the given directory.
    #>
    param(
        [string]$Directory,
        [string]$Description = "MSIX package"
    )
    $msixFiles = Get-ChildItem -Path $Directory -Filter "*.msix" -ErrorAction SilentlyContinue
    if (-not $msixFiles) {
        Write-TestError "No .msix file found in $Directory"
        throw "$Description not found in $Directory"
    }
    Write-TestSuccess "$Description created: $($msixFiles[0].Name)"
    return $msixFiles[0].FullName
}

function New-DevCertificate {
    <#
    .SYNOPSIS
    Generates a development certificate using winapp cert generate.
    Returns the path to the generated .pfx file.
    #>
    param(
        [string]$OutputDir = (Get-Location)
    )
    Invoke-Winapp "cert generate" -FailMessage "Failed to generate development certificate"
    $certPath = Join-Path $OutputDir "devcert.pfx"
    Assert-FileExists $certPath "Development certificate"
    return $certPath
}

# ============================================================================
# Temp Directory Helpers (for from-scratch guide tests)
# ============================================================================

function New-TempTestDirectory {
    <#
    .SYNOPSIS
    Creates a temporary directory for from-scratch guide workflow tests.
    Returns the absolute path to the new directory.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Prefix
    )
    $tempBase = Join-Path ([System.IO.Path]::GetTempPath()) "winapp-test"
    $null = New-Item -ItemType Directory -Path $tempBase -Force
    $tempDir = Join-Path $tempBase "$Prefix-$([System.IO.Path]::GetRandomFileName())"
    $null = New-Item -ItemType Directory -Path $tempDir -Force
    Write-TestSuccess "Created temp directory: $tempDir"
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
        Write-TestSuccess "Cleaned up temp directory: $Path"
    }
}

function Assert-WinappInitOutput {
    <#
    .SYNOPSIS
    Validates that winapp init created the expected files in the current directory.
    Checks for winapp.yaml, appxmanifest.xml, and .winapp/ directory.
    #>
    param(
        [string]$Directory = (Get-Location),
        [switch]$ExpectWinappYaml = $true,
        [switch]$ExpectManifest = $true,
        [switch]$ExpectDotWinapp
    )
    if ($ExpectWinappYaml) {
        Assert-FileExists (Join-Path $Directory "winapp.yaml") "winapp.yaml config"
    }
    if ($ExpectManifest) {
        Assert-FileExists (Join-Path $Directory "appxmanifest.xml") "AppxManifest"
    }
    if ($ExpectDotWinapp) {
        Assert-DirectoryExists (Join-Path $Directory ".winapp") ".winapp SDK directory"
    }
}

function Assert-CertInfo {
    <#
    .SYNOPSIS
    Runs winapp cert info on a certificate and validates the output is non-empty.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$CertPath
    )
    $output = Invoke-Winapp "cert info `"$CertPath`"" -FailMessage "winapp cert info failed"
    if (-not $output) {
        Write-TestError "winapp cert info produced no output"
        throw "winapp cert info produced no output"
    }
    Write-TestSuccess "winapp cert info returned certificate details"
}

# ============================================================================
# Exports
# ============================================================================

Export-ModuleMember -Function @(
    'Write-TestHeader'
    'Write-TestStep'
    'Write-TestSuccess'
    'Write-TestError'
    'Assert-ExitCode'
    'Assert-Command'
    'Assert-FileExists'
    'Assert-DirectoryExists'
    'Assert-OutputContains'
    'Resolve-WinappCliPath'
    'Invoke-Winapp'
    'Install-WinappNpmPackage'
    'Install-WinappGlobal'
    'Assert-Prerequisite'
    'New-SampleTestContext'
    'Complete-SampleTest'
    'Assert-MsixCreated'
    'New-DevCertificate'
    'New-TempTestDirectory'
    'Remove-TempTestDirectory'
    'Assert-WinappInitOutput'
    'Assert-CertInfo'
)
