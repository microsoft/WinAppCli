<#
.SYNOPSIS
Test script for the electron sample and Electron guide workflows.

.DESCRIPTION
Phase 1: Follows the Electron setup + packaging guides from scratch — creates a
  new Electron app, installs winapp, runs init, creates C++ and C# addons from
  scratch, builds addons, packages with Forge, and creates an MSIX.
Phase 2: Quick npm install of the existing sample to verify it is not stale.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) to install.

.PARAMETER SkipCleanup
Keep generated artifacts after test completes.

.PARAMETER Verbose
Enable verbose output.
#>

param(
    [string]$WinappPath,
    [switch]$SkipCleanup,
    [switch]$Verbose
)

Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force

$ctx = New-SampleTestContext -SampleName "electron" -WinappPath $WinappPath -Verbose:$Verbose
$step = 0
$tempDir = $null

try {
    # ==================================================================
    # Prerequisites
    # ==================================================================
    Write-TestStep "Checking prerequisites..." (++$step)
    Assert-Prerequisite "node" -DisplayName "Node.js"
    Assert-Prerequisite "npm" -DisplayName "npm"
    Assert-Prerequisite "dotnet" -DisplayName ".NET SDK"

    $resolvedPkg = Resolve-WinappCliPath -WinappPath $WinappPath

    # ==================================================================
    # Phase 1 — Guide Workflow (from scratch)
    # ==================================================================
    Write-TestHeader "Phase 1: Electron Guide Workflow (from scratch)"

    $tempDir = New-TempTestDirectory -Prefix "electron-guide"
    Push-Location $tempDir

    # Set up npm cache to avoid ECOMPROMISED errors in CI
    $npmCacheDir = Join-Path $tempDir ".npm-cache"
    $null = New-Item -ItemType Directory -Path $npmCacheDir -Force
    $env:npm_config_cache = $npmCacheDir

    Write-TestStep "Creating new Electron app..." (++$step)
    $maxRetries = 3
    $created = $false
    for ($i = 1; $i -le $maxRetries; $i++) {
        Write-Verbose "Attempt $i of $maxRetries..."
        if ($i -gt 1) {
            Remove-Item -Path (Join-Path $tempDir "electron-app") -Recurse -Force -ErrorAction SilentlyContinue
            npm cache clean --force 2>$null
            Start-Sleep -Seconds 2
        }
        Invoke-Expression "npx -y create-electron-app@7.11.1 electron-app --template=webpack"
        if ($LASTEXITCODE -eq 0) { $created = $true; break }
    }
    if (-not $created) { throw "Failed to create Electron app after $maxRetries attempts" }
    Write-TestSuccess "Electron app created"

    Push-Location "electron-app"

    # Configure package.json for MSIX
    $pkgJson = Get-Content "package.json" | ConvertFrom-Json
    $pkgJson | Add-Member -MemberType NoteProperty -Name "displayName" -Value "WinApp Test App" -Force
    $pkgJson | Add-Member -MemberType NoteProperty -Name "description" -Value "Guide test" -Force
    $pkgJson | ConvertTo-Json -Depth 10 | Set-Content "package.json"

    Write-TestStep "Installing winapp npm package..." (++$step)
    Install-WinappNpmPackage -PackagePath $resolvedPkg

    Write-TestStep "Running winapp init..." (++$step)
    Invoke-Winapp "init . --use-defaults --setup-sdks=stable" -FailMessage "winapp init failed"
    Assert-WinappInitOutput -ExpectWinappYaml -ExpectManifest -ExpectDotWinapp

    Write-TestStep "Creating C++ addon from scratch..." (++$step)
    Invoke-Winapp "node create-addon --template cpp --name testCppAddon" -FailMessage "create C++ addon failed"
    Assert-DirectoryExists "testCppAddon" "C++ addon directory"
    Assert-FileExists "testCppAddon\binding.gyp" "binding.gyp"

    Write-TestStep "Building C++ addon..." (++$step)
    Assert-Command "npm run build-testCppAddon" "C++ addon build failed"

    Write-TestStep "Creating C# addon from scratch..." (++$step)
    Invoke-Winapp "node create-addon --template cs --name testCsAddon" -FailMessage "create C# addon failed"
    Assert-DirectoryExists "testCsAddon" "C# addon directory"
    Assert-FileExists "testCsAddon\testCsAddon.csproj" "C# addon csproj"

    Write-TestStep "Building C# addon..." (++$step)
    Assert-Command "npm run build-testCsAddon" "C# addon build failed"

    Write-TestStep "Packaging Electron app..." (++$step)
    Assert-Command "npm run package" "Electron packaging failed"

    $outDir = Join-Path (Get-Location) "out"
    Assert-DirectoryExists $outDir "Electron output directory"
    $appPackageDir = (Get-ChildItem -Path $outDir -Directory | Select-Object -First 1).FullName
    Write-TestSuccess "Packaged to: $appPackageDir"

    Write-TestStep "Generating dev certificate..." (++$step)
    $certPath = New-DevCertificate

    Write-TestStep "Verifying certificate info..." (++$step)
    Assert-CertInfo -CertPath $certPath

    Write-TestStep "Packaging as MSIX..." (++$step)
    Invoke-Winapp "pack `"$appPackageDir`" --cert `"$certPath`"" -FailMessage "winapp pack failed"

    Write-TestStep "Validating MSIX output..." (++$step)
    Assert-MsixCreated -Directory (Get-Location) -Description "Guide electron MSIX"

    Pop-Location  # back to tempDir
    Pop-Location  # back to original

    # ==================================================================
    # Phase 2 — Sample Build Check
    # ==================================================================
    Write-TestHeader "Phase 2: Sample Build Check"
    Push-Location $ctx.SampleDir

    Write-TestStep "Installing sample dependencies..." (++$step)
    Assert-Command "npm install --ignore-scripts" "npm install failed"
    Assert-DirectoryExists "node_modules" "node_modules"
    Write-TestSuccess "electron sample dependencies install successfully"

    Pop-Location

    Complete-SampleTest -Context $ctx

} finally {
    Set-Location $ctx.SampleDir
    if (-not $SkipCleanup) {
        if ($tempDir) { Remove-TempTestDirectory -Path $tempDir }
        Remove-Item -Path (Join-Path $ctx.SampleDir "node_modules") -Recurse -Force -ErrorAction SilentlyContinue
    }
}
