<#
.SYNOPSIS
Test script for the tauri-app sample and Tauri guide workflow.

.DESCRIPTION
Phase 1: Follows the docs/guides/tauri.md guide — copies sample to temp dir,
  installs deps, runs winapp init, builds, and packages as MSIX.
Phase 2: Quick build of the existing sample to verify it is not stale.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.
#>

[CmdletBinding()]
param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force

$ctx = New-SampleTestContext -SampleName "tauri-app" -SampleDir $PSScriptRoot -WinappPath $WinappPath -Verbose:$VerbosePreference
$step = 0
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "node" -DisplayName "Node.js"
    Assert-Prerequisite "npm" -DisplayName "npm"
    Assert-Prerequisite "cargo" -DisplayName "Rust/Cargo"

    Write-TestStep "Installing winapp CLI..." (++$step)
    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath
    Install-WinappGlobal -PackagePath $resolvedPkg

    # ==================================================================
    # Phase 1 — Guide Workflow (copy sample to temp, run full flow)
    # Tauri scaffolding via npm create tauri-app is interactive and slow,
    # so we copy the existing sample as a starting point (matching the
    # guide's "start from a Tauri template" step).
    # ==================================================================
    Write-TestHeader "Phase 1: Tauri Guide Workflow"

    $tempDir = New-TempTestDirectory -Prefix "tauri-guide"
    $tempApp = Join-Path $tempDir "tauri-app"

    Write-TestStep "Copying sample to temp directory..." (++$step)
    Copy-Item -Path $ctx.SampleDir -Destination $tempApp -Recurse -Exclude @('.gitignore', 'node_modules', 'src-tauri\target')
    Push-Location $tempApp

    Write-TestStep "Installing npm dependencies..." (++$step)
    Assert-Command "npm install" "npm install failed"

    Write-TestStep "Running winapp init..." (++$step)
    Assert-Command "winapp init --use-defaults --setup-sdks=none" "winapp init failed"
    Assert-WinappInitOutput -ExpectManifest

    Write-TestStep "Building Tauri app (release)..." (++$step)
    Assert-Command "cargo build --release --manifest-path src-tauri\Cargo.toml" "Tauri cargo build failed"

    $tauriExe = Join-Path $tempApp "src-tauri\target\release\tauri-app.exe"
    Assert-FileExists $tauriExe "tauri-app.exe"

    Write-TestStep "Preparing MSIX layout..." (++$step)
    $null = New-Item -ItemType Directory -Path "msix-layout" -Force
    Copy-Item $tauriExe -Destination "msix-layout\"

    Write-TestStep "Generating dev certificate..." (++$step)
    Assert-Command "winapp cert generate --if-exists skip --manifest appxmanifest.xml" "cert generate failed"

    Write-TestStep "Packaging as MSIX..." (++$step)
    Assert-Command "winapp pack msix-layout --manifest appxmanifest.xml --cert devcert.pfx" "winapp pack failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory (Get-Location) -Description "Guide tauri-app MSIX"

    Pop-Location  # back to original

    # ==================================================================
    # Phase 2 — Sample Build Check
    # ==================================================================
    Write-TestHeader "Phase 2: Sample Build Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Installing sample npm dependencies..." (++$step)
    Assert-Command "npm install" "npm install failed"

    Write-TestStep "Building sample Rust backend..." (++$step)
    Assert-Command "cargo build --manifest-path src-tauri\Cargo.toml" "Sample cargo build failed"
    Assert-FileExists "src-tauri\target\debug\tauri-app.exe" "tauri-app.exe"

    Pop-Location

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        if ($tempDir) { Remove-TempTestDirectory -Path $tempDir }
        Remove-Item -Path (Join-Path $ctx.SampleDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Path (Join-Path $ctx.SampleDir "src-tauri\target") -Recurse -Force -ErrorAction SilentlyContinue
    }
}
