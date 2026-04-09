#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build and install the winapp CLI globally from the current code.
.DESCRIPTION
    One-step script to build the native CLI and install it as a global
    'winapp' command available in any PowerShell terminal.
.EXAMPLE
    .\scripts\use.ps1
#>

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot | Split-Path -Parent

# Ensure Node.js/npm is installed
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Host "npm not found. Installing Node.js via winget..." -ForegroundColor Yellow
    winget install OpenJS.NodeJS --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "Node.js installation failed" }
    # Refresh PATH for current session
    $env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw "npm still not found after install. Please restart your terminal and try again."
    }
}

Push-Location $ProjectRoot
try {
    # 1. Build native CLI (skip tests, MSIX, NuGet, npm)
    Write-Host "[1/3] Building native CLI..." -ForegroundColor Cyan
    & "$PSScriptRoot\build-cli.ps1" -SkipTests -SkipMsix -SkipNuGet -SkipNpm
    if ($LASTEXITCODE -ne 0) { throw "CLI build failed" }

    # 2. Prepare npm package
    Write-Host "[2/3] Preparing npm package..." -ForegroundColor Cyan
    Push-Location "src\winapp-npm"
    try {
        npm ci --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }

        npm run generate-commands
        if ($LASTEXITCODE -ne 0) { throw "generate-commands failed" }

        npm run compile
        if ($LASTEXITCODE -ne 0) { throw "npm compile failed" }

        npm run build-copy-only
        if ($LASTEXITCODE -ne 0) { throw "build-copy-only failed" }

        # 3. Install globally
        Write-Host "[3/3] Installing winapp globally..." -ForegroundColor Cyan
        npm install -g .
        if ($LASTEXITCODE -ne 0) { throw "npm install -g failed" }
    } finally {
        Pop-Location
    }

    Write-Host "`n✅ Done! 'winapp' is now available globally." -ForegroundColor Green
    Write-Host "Try: winapp --help" -ForegroundColor Gray
} catch {
    Write-Error "Build failed: $_"
    exit 1
} finally {
    Pop-Location
}
