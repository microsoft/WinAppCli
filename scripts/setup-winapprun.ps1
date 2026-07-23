#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Set up the winapp run command for selfhosting.
.DESCRIPTION
    This script installs (or upgrades) the following from the local artifacts folder:
      1. winapp MSIX package (architecture-matched) + its signing certificate
      2. Registers a local NuGet feed for Microsoft.Windows.SDK.BuildTools.WinApp

    Re-running with newer files will upgrade everything in place.
.PARAMETER Elevated
    Internal flag -- set automatically when the script re-launches itself as admin.
.EXAMPLE
    .\setup-winapprun.ps1
#>

param(
    [Parameter(Mandatory = $false)]
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"

# ── Helpers ──────────────────────────────────────────────────────────────────

function Write-Step  { param([string]$msg) Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Skip  { param([string]$msg) Write-Host "   [SKIP] $msg" -ForegroundColor Yellow }
function Write-Err   { param([string]$msg) Write-Host "   [ERROR] $msg" -ForegroundColor Red }
function Write-Detail{ param([string]$msg) Write-Host "   $msg" -ForegroundColor Gray }

# ── Resolve paths relative to this script ────────────────────────────────────

$ScriptDir     = Split-Path $PSCommandPath -Parent
$MsixDir       = Join-Path $ScriptDir "msix-packages"
$NugetDir      = Join-Path $ScriptDir "nuget"

$NuGetFeedName = "WinAppCLI-Local"
$NuGetFeedPath = Join-Path $env:USERPROFILE ".winapp\local-nuget-feed"

# ── Trap for nice error output in elevated window ───────────────────────────

trap {
    Write-Host ""
    Write-Err $_
    if ($Elevated) { Read-Host "`nPress Enter to close" }
    exit 1
}

# ── Unblock all files (they may have a "downloaded from internet" mark) ──────

Write-Step "Unblocking downloaded files"
try {
    Get-ChildItem -Path $ScriptDir -Recurse -File | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
    Write-Ok "Files unblocked"
} catch {
    Write-Skip "Could not unblock files (non-fatal): $_"
}

# ── Elevation check ─────────────────────────────────────────────────────────
#  Installing the MSIX signing certificate into TrustedPeople requires admin.

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent()
)
$isAdmin = $currentPrincipal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host ""
    Write-Host "This script needs administrator privileges to trust the MSIX certificate." -ForegroundColor Yellow
    $response = Read-Host "Elevate to Administrator? (Y/N)"
    if ($response -notin @('Y', 'y')) {
        Write-Host "Cancelled." -ForegroundColor Yellow
        exit 1
    }

    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Elevated")
    try {
        Start-Process PowerShell -Verb RunAs -ArgumentList $arguments -WorkingDirectory (Get-Location)
        Write-Host "[INFO] Running in elevated window -- check that window for results." -ForegroundColor Cyan
        exit 0
    } catch {
        Write-Err "Failed to elevate: $_"
        exit 1
    }
}


Write-Host ""
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host "  winapp run Selfhost Setup / Upgrade" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan

# ═══════════════════════════════════════════════════════════════════════════════
# 1. MSIX package (architecture-matched)
# ═══════════════════════════════════════════════════════════════════════════════
Write-Step "Installing WinAppCLI MSIX package"

if (-not (Test-Path $MsixDir)) {
    Write-Err "msix-packages directory not found at: $MsixDir"
    exit 1
}

# Pick the right MSIX for this machine's architecture
$arch = switch ($env:PROCESSOR_ARCHITECTURE) {
    "AMD64" { "x64"   }
    "ARM64" { "arm64" }
    default { $env:PROCESSOR_ARCHITECTURE.ToLower() }
}

$msixFile = Get-ChildItem -Path $MsixDir -Filter "*$arch*.msix" | Select-Object -First 1
if (-not $msixFile) {
    $msixFile = Get-ChildItem -Path $MsixDir -Filter "*.msix" | Select-Object -First 1
}
if (-not $msixFile) {
    Write-Err "No .msix file found in $MsixDir"
    exit 1
}

Write-Detail "Package : $($msixFile.Name)"
Write-Detail "Arch    : $arch"

# ── Extract & trust the signing certificate ─────────────────────────────────
$signature = Get-AuthenticodeSignature -FilePath $msixFile.FullName
$cert = $null

if ($signature -and $signature.SignerCertificate) {
    $cert = $signature.SignerCertificate
    Write-Detail "Found certificate in package signature"
} else {
    # Fall back to extracting from the AppxSignature.p7x inside the MSIX
    Write-Detail "No signature in package, extracting from AppxSignature.p7x..."
    $tmpDir = Join-Path $env:TEMP "winapp-cert-$(Get-Random)"
    New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
    try {
        $zipCopy = Join-Path $tmpDir "package.zip"
        Copy-Item $msixFile.FullName $zipCopy -Force
        Expand-Archive -Path $zipCopy -DestinationPath $tmpDir -Force

        $p7x = Get-ChildItem -Path $tmpDir -Filter "AppxSignature.p7x" -Recurse | Select-Object -First 1
        if ($p7x) {
            $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
            $cms.Decode([System.IO.File]::ReadAllBytes($p7x.FullName))
            $cert = $cms.Certificates[0]
            Write-Detail "Extracted certificate from AppxSignature.p7x"
        }
    } finally {
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $cert) {
    Write-Err "Could not extract a signing certificate from $($msixFile.Name)"
    exit 1
}

Write-Detail "Cert    : $($cert.Subject)  (thumbprint $($cert.Thumbprint))"

$existing = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
            Where-Object { $_.Thumbprint -eq $cert.Thumbprint }

if ($existing) {
    Write-Skip "Certificate already trusted"
} else {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "LocalMachine")
    $store.Open("ReadWrite")
    $store.Add($cert)
    $store.Close()
    Write-Ok "Certificate installed into TrustedPeople store"
}

# ── Check for existing winapp packages that could conflict ──────────────────
$existingPackages = Get-AppxPackage | Where-Object { $_.Name -eq 'winapp' -or $_.Name -eq 'winapp-dev' }
if ($existingPackages) {
    Write-Detail ""
    Write-Detail "Found existing winapp package(s):"
    foreach ($pkg in $existingPackages) {
        Write-Detail "  - $($pkg.Name) v$($pkg.Version)"
    }
    $response = Read-Host "Uninstall existing package(s) before installing? (Y/N)"
    if ($response -eq 'Y' -or $response -eq 'y') {
        foreach ($pkg in $existingPackages) {
            Write-Detail "Removing $($pkg.Name)..."
            Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
            Write-Detail "  - Removed $($pkg.Name)"
        }
        Write-Detail ""
    }
}

# ── Install / upgrade the MSIX ──────────────────────────────────────────────
try {
    Add-AppxPackage -Path $msixFile.FullName -ForceApplicationShutdown -ErrorAction Stop
    Write-Ok "MSIX installed -- 'winapp' command is now available"
} catch {
    Write-Err "MSIX install failed: $_"
    Write-Detail "Try:  Add-AppxPackage -Path '$($msixFile.FullName)' -ForceApplicationShutdown"
}

# ═══════════════════════════════════════════════════════════════════════════════
# 2. Local NuGet feed for BuildTools.MSIX.Extras
# ═══════════════════════════════════════════════════════════════════════════════
Write-Step "Setting up local NuGet feed for BuildTools.MSIX.Extras"

$extrasPkg = Get-ChildItem -Path $NugetDir -Filter "Microsoft.Windows.SDK.BuildTools.WinApp.*.nupkg" -ErrorAction SilentlyContinue |
             Select-Object -First 1
if (-not $extrasPkg) {
    Write-Skip "No BuildTools.MSIX.Extras nupkg found in $NugetDir -- skipping"
} else {
    Write-Detail "Package : $($extrasPkg.Name)"

    # Ensure the local feed folder exists
    if (-not (Test-Path $NuGetFeedPath)) {
        New-Item -ItemType Directory -Path $NuGetFeedPath -Force | Out-Null
        Write-Detail "Created feed folder: $NuGetFeedPath"
    }

    # Copy the nupkg into the feed (overwrite to support upgrades)
    Copy-Item -Path $extrasPkg.FullName -Destination $NuGetFeedPath -Force
    Write-Ok "Copied $($extrasPkg.Name) -> $NuGetFeedPath"

    # Register (or update) the NuGet source globally
    $dotnetCli = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCli) {
        Write-Skip "'dotnet' not found -- register the NuGet source manually:"
        Write-Detail "  dotnet nuget add source `"$NuGetFeedPath`" --name $NuGetFeedName"
    } else {
        $existingSource = & dotnet nuget list source 2>&1 | Out-String
        if ($existingSource -match [regex]::Escape($NuGetFeedName)) {
            # Source already exists -- update its path (in case the folder moved)
            try {
                & dotnet nuget update source $NuGetFeedName --source $NuGetFeedPath 2>&1 | Out-Null
                Write-Ok "NuGet source '$NuGetFeedName' updated -> $NuGetFeedPath"
            } catch {
                Write-Detail "Could not update source, removing and re-adding..."
                & dotnet nuget remove source $NuGetFeedName 2>&1 | Out-Null
                & dotnet nuget add source $NuGetFeedPath --name $NuGetFeedName 2>&1 | Out-Null
                Write-Ok "NuGet source '$NuGetFeedName' re-registered -> $NuGetFeedPath"
            }
        } else {
            & dotnet nuget add source $NuGetFeedPath --name $NuGetFeedName 2>&1 | Out-Null
            Write-Ok "NuGet source '$NuGetFeedName' registered -> $NuGetFeedPath"
        }
    }
}

# ═══════════════════════════════════════════════════════════════════════════════
# Done
# ═══════════════════════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "=======================================" -ForegroundColor Green
Write-Host "  All done!" -ForegroundColor Green
Write-Host "=======================================" -ForegroundColor Green
Write-Host ""
Write-Host "  winapp CLI  : winapp --version" -ForegroundColor Gray
Write-Host "  NuGet feed  : dotnet nuget list source" -ForegroundColor Gray
Write-Host ""

if ($Elevated) {
    Write-Host ""
    Read-Host "Press Enter to close"
}
