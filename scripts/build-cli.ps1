#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build script for Windows App Development CLI, npm package, NuGet packages, and MSIX packages
.DESCRIPTION
    This script builds the Windows App Development CLI for both x64 and arm64 architectures,
    creates the npm package, NuGet package (BuildTools.WinApp), creates MSIX packages 
    with distribution package, and places all artifacts in an artifacts folder. 
    Run this script from the root of the project.
.PARAMETER SkipTests
    Skip running unit tests
.PARAMETER FailOnTestFailure
    Exit with error code if tests fail (default: true, stops build on test failures)
.PARAMETER SkipNpm
    Skip npm package creation
.PARAMETER SkipNuGet
    Skip NuGet package creation (BuildTools.WinApp)
.PARAMETER SkipMsix
    Skip MSIX packages creation
.PARAMETER SkipDocs
    Skip CLI schema and agent skills generation (useful in CI where docs are validated separately)
.PARAMETER Stable
    Use stable build configuration (default: false, uses prerelease config)
.EXAMPLE
    .\scripts\build-cli.ps1
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipTests
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipNpm
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipNuGet
.EXAMPLE
    .\scripts\build-cli.ps1 -SkipMsix
.EXAMPLE
    .\scripts\build-cli.ps1 -Stable
#>

param(
    [switch]$Clean = $false,
    [switch]$SkipTests = $false,
    [switch]$FailOnTestFailure = $true,
    [switch]$SkipNpm = $false,
    [switch]$SkipNuGet = $false,
    [switch]$SkipMsix = $false,
    [switch]$SkipDocs = $false,
    [switch]$Stable = $false
)

# Ensure we're running from the project root
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray

Push-Location $ProjectRoot
try
{
    # Define paths
    $CliSolutionDir = "src\winapp-CLI"
    $CliSolutionPath = "$CliSolutionDir\winapp.sln"
    $CliProjectPath = "$CliSolutionDir\WinApp.Cli\WinApp.Cli.csproj"
    $CliTestsProjectPath = "$CliSolutionDir\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj"
    $ArtifactsPath = "artifacts"
    $TestResultsPath = "TestResults"

    Write-Host "[*] Starting Windows SDK build process..." -ForegroundColor Green
    Write-Host "Project root: $ProjectRoot" -ForegroundColor Gray
    if ($Stable) {
        Write-Host "Build mode: STABLE (no prerelease suffix)" -ForegroundColor Cyan
    } else {
        Write-Host "Build mode: PRERELEASE (with prerelease suffix)" -ForegroundColor Cyan
    }

    Write-Host "[CLEAN] Cleaning artifacts and test results..." -ForegroundColor Yellow
    if (Test-Path $ArtifactsPath) {
        Remove-Item $ArtifactsPath -Recurse -Force
    }
    if (Test-Path $TestResultsPath) {
        Remove-Item $TestResultsPath -Recurse -Force
    }

    # Create artifacts directory
    Write-Host "[SETUP] Creating artifacts directory..." -ForegroundColor Blue
    New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null

    # Step 1: Calculate version
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
    $BuildNumber = & "$PSScriptRoot\get-build-number.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to get build number"
        exit 1
    }

    # Determine prerelease label based on current branch
    # - main and rel/* branches use "prerelease" (default)
    # - all other branches use a sanitized branch name (e.g., dev/my-feature -> dev-my-feature)
    $PrereleaseLabel = & "$PSScriptRoot\get-prerelease-label.ps1"
    Write-Host "[VERSION] Prerelease label: $PrereleaseLabel" -ForegroundColor Gray

    # Construct full version based on Stable flag
    if ($Stable) {
        # Stable build: use semantic version without prerelease suffix (e.g., "0.1.0")
        $FullVersion = $BaseVersion
        Write-Host "[VERSION] Using stable version (no prerelease suffix)" -ForegroundColor Cyan
    } else {
        # Prerelease build: add prerelease label suffix (e.g., "0.1.0-prerelease.73" or "0.1.0-dev-my-feature.73")
        $FullVersion = "$BaseVersion-$PrereleaseLabel.$BuildNumber"
        Write-Host "[VERSION] Using prerelease version (with $PrereleaseLabel suffix)" -ForegroundColor Cyan
    }
    Write-Host "[VERSION] Package version: $FullVersion" -ForegroundColor Cyan

    # Extract semantic version components for assembly versioning
    # BaseVersion should be in format major.minor.patch (e.g., "0.1.0")
    $VersionParts = $BaseVersion -split '\.'
    $MajorVersion = $VersionParts[0]
    $MinorVersion = $VersionParts[1]
    $PatchVersion = $VersionParts[2]

    # Assembly version uses format: major.minor.patch.buildnumber (e.g., "0.1.0.73")
    $AssemblyVersion = "$MajorVersion.$MinorVersion.$PatchVersion.$BuildNumber"
    Write-Host "[VERSION] Assembly version: $AssemblyVersion" -ForegroundColor Cyan

    # InformationalVersion shows in --version output (e.g., "0.1.0-prerelease.73")
    $InformationalVersion = $FullVersion

    # Step 2: Publish CLI for x64 and arm64 (implicitly builds the CLI project)
    Write-Host "[PUBLISH] Publishing CLI for x64..." -ForegroundColor Blue
    dotnet publish $CliProjectPath -c Release -r win-x64 --self-contained -o "$ArtifactsPath\cli\win-x64" `
        /p:Version=$AssemblyVersion `
        /p:AssemblyVersion=$AssemblyVersion `
        /p:FileVersion=$AssemblyVersion `
        /p:InformationalVersion=$InformationalVersion `
        /p:IncludeSourceRevisionInInformationalVersion=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish CLI for x64"
        exit 1
    }

    Write-Host "[PUBLISH] Publishing CLI for arm64..." -ForegroundColor Blue
    dotnet publish $CliProjectPath -c Release -r win-arm64 --self-contained -o "$ArtifactsPath\cli\win-arm64" `
        /p:Version=$AssemblyVersion `
        /p:AssemblyVersion=$AssemblyVersion `
        /p:FileVersion=$AssemblyVersion `
        /p:InformationalVersion=$InformationalVersion `
        /p:IncludeSourceRevisionInInformationalVersion=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish CLI for arm64"
        exit 1
    }

    # Step 3: Build test project (CLI is already built from publish, this mainly compiles tests)
    Write-Host "[BUILD] Building CLI solution..." -ForegroundColor Blue
    dotnet build $CliSolutionPath -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build CLI solution"
        exit 1
    }

    # Step 4: Build Node CLI so E2E tests that invoke node cli.js can run
    if (-not $SkipNpm) {
        Write-Host "[BUILD] Building Node CLI (for tests)..." -ForegroundColor Blue
        Push-Location (Join-Path $ProjectRoot "src\winapp-npm")
        try {
            npm ci --ignore-scripts
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "npm ci failed, Node E2E tests will be skipped"
            } else {
                npm run generate-commands
                npm run compile
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Node CLI compile failed, Node E2E tests will be skipped"
                } else {
                    Write-Host "[BUILD] Node CLI built successfully" -ForegroundColor Green
                }
            }
        } finally {
            Pop-Location
        }
    }

    # Step 5: Run tests (unless skipped)
    if (-not $SkipTests) {
        Write-Host "[TEST] Running tests..." -ForegroundColor Blue
        dotnet run --project $CliTestsProjectPath -c Release --no-build --results-directory $CliSolutionDir\TestResults --report-trx --coverage --coverage-output-format cobertura
        $TestExitCode = $LASTEXITCODE
    
        # Copy test results to artifacts BEFORE checking for failure - find all TRX files
        Write-Host "[TEST] Collecting test results..." -ForegroundColor Blue
        New-Item -ItemType Directory -Path "$ArtifactsPath\TestResults" -Force | Out-Null
        $TrxFiles = Get-ChildItem -Path $CliSolutionDir -Filter "*.trx" -Recurse -File
        if ($TrxFiles) {
            foreach ($trxFile in $TrxFiles) {
                Copy-Item $trxFile.FullName "$ArtifactsPath\TestResults\" -Force
                Write-Host "[TEST] Copied: $($trxFile.Name)" -ForegroundColor Gray
            }
            Write-Host "[TEST] Test results copied successfully ($($TrxFiles.Count) file(s))" -ForegroundColor Green
        } else {
            Write-Warning "No TRX test result files found in $CliSolutionDir"
        }

        # Copy coverage XML files to artifacts
        $CoverageFiles = Get-ChildItem -Path $CliSolutionDir -Filter "*.cobertura.xml" -Recurse -File
        if ($CoverageFiles) {
            foreach ($coverageFile in $CoverageFiles) {
                Copy-Item $coverageFile.FullName "$ArtifactsPath\TestResults\" -Force
                Write-Host "[TEST] Copied coverage: $($coverageFile.Name)" -ForegroundColor Gray
            }
            Write-Host "[TEST] Coverage results copied successfully ($($CoverageFiles.Count) file(s))" -ForegroundColor Green
        } else {
            Write-Warning "No coverage XML files found in $CliSolutionDir"
        }

        # Now check test results and decide whether to exit
        if ($TestExitCode -ne 0) {
            Write-Warning "Tests failed with exit code $TestExitCode"
            if ($FailOnTestFailure) {
                Write-Error "Stopping build due to test failures (FailOnTestFailure flag set)"
                exit 1
            } else {
                Write-Host "[TEST] Continuing build despite test failures..." -ForegroundColor Yellow
            }
        } else {
            Write-Host "[TEST] Tests passed!" -ForegroundColor Green
        }
    } else {
        Write-Host "[TEST] Skipping tests (SkipTests flag set)" -ForegroundColor Yellow
    }

    # Step 6: Generate CLI schema and agent skills (optional)
    if (-not $SkipDocs) {
        Write-Host ""
        Write-Host "[DOCS] Generating CLI schema and agent skills..." -ForegroundColor Blue
        
        $GenerateLlmDocsScript = Join-Path $PSScriptRoot "generate-llm-docs.ps1"
        $CliExePath = Join-Path $ProjectRoot "$ArtifactsPath\cli\win-x64\winapp.exe"
        
        & $GenerateLlmDocsScript -CliPath $CliExePath -CalledFromBuildScript
        
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "CLI schema and agent skills generation failed, but continuing..."
        } else {
            Write-Host "[DOCS] CLI schema and agent skills generated successfully!" -ForegroundColor Green
        }
    } else {
        Write-Host ""
        Write-Host "[DOCS] Skipping CLI schema and agent skills generation (-SkipDocs)" -ForegroundColor Yellow
    }

    # Step 7: Create npm package (optional)
    if (-not $SkipNpm) {
        Write-Host ""
        Write-Host "[NPM] Creating npm package..." -ForegroundColor Blue
    
        $PackageNpmScript = Join-Path $PSScriptRoot "package-npm.ps1"

        & $PackageNpmScript -Version $FullVersion -Stable:$Stable

        if ($LASTEXITCODE -ne 0) {
            Write-Error "npm package creation failed"
            exit 1
        }

        # Generate npm API documentation from TypeScript source (after npm build so codegen is fresh)
        Write-Host "[NPM] Generating npm API documentation..." -ForegroundColor Blue
        Push-Location (Join-Path $ProjectRoot "src\winapp-npm")
        try {
            npm run generate-docs
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "npm API documentation generation failed, but continuing..."
            } else {
                Write-Host "[NPM] npm API documentation generated successfully!" -ForegroundColor Green
            }
        } finally {
            Pop-Location
        }
    } else {
        Write-Host ""
        Write-Host "[NPM] Skipping npm package creation (use -SkipNpm:`$false to enable)" -ForegroundColor Gray
    }

    # Step 8: Create NuGet packages (optional)
    if (-not $SkipNuGet) {
        Write-Host ""
        Write-Host "[NUGET] Creating NuGet packages..." -ForegroundColor Blue
    
        $PackageNuGetScript = Join-Path $PSScriptRoot "package-nuget.ps1"

        & $PackageNuGetScript -Version $FullVersion -Stable:$Stable

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "NuGet packages creation failed, but continuing..."
        } else {
            Write-Host "[NUGET] NuGet packages created successfully!" -ForegroundColor Green
        }
    } else {
        Write-Host ""
        Write-Host "[NUGET] Skipping NuGet packages creation (use -SkipNuGet:`$false to enable)" -ForegroundColor Gray
    }

    # Step 9: Create MSIX packages (optional)
    if (-not $SkipMsix) {
        Write-Host ""
        Write-Host "[MSIX] Creating MSIX packages..." -ForegroundColor Blue
    
        # MSIX version is always 4-part numeric: major.minor.patch.buildNumber
        $MsixVersion = "$BaseVersion.$BuildNumber"
    
        # Pass branch tag so MSIX filename reflects the branch (e.g., winappcli-dev-my-feature_0.2.0.73_x64.msix)
        $MsixTag = if (-not $Stable -and $PrereleaseLabel -ne 'prerelease') { $PrereleaseLabel } else { $null }
    
        $PackageMsixScript = Join-Path $PSScriptRoot "package-msix.ps1"
        $CliBinariesPath = Join-Path (Join-Path $ProjectRoot $ArtifactsPath) "cli"

        $MsixArgs = @{
            CliBinariesPath = $CliBinariesPath
            Version = $MsixVersion
            Stable = $Stable
        }
        if ($MsixTag) { $MsixArgs['Tag'] = $MsixTag }
        & $PackageMsixScript @MsixArgs

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "MSIX packages creation failed, but continuing..."
        } else {
            Write-Host "[MSIX] MSIX packages created successfully!" -ForegroundColor Green
        }
    } else {
        Write-Host ""
        Write-Host "[MSIX] Skipping MSIX packages creation (use -SkipMsix:`$false to enable)" -ForegroundColor Gray
    }

    # Build process complete - all artifacts are ready

    # Copy install-dev script into artifacts so the folder is self-contained
    Write-Host ""
    Write-Host "[INSTALL] Copying setup-winapprun.ps1 to artifacts..." -ForegroundColor Blue
    $InstallDevScript = Join-Path $PSScriptRoot "setup-winapprun.ps1"
    if (Test-Path $InstallDevScript) {
        Copy-Item $InstallDevScript -Destination $ArtifactsPath -Force
        Write-Host "[INSTALL] setup-winapprun.ps1 copied to artifacts" -ForegroundColor Green
    } else {
        Write-Warning "setup-winapprun.ps1 not found at $InstallDevScript"
    }

    # Display results
    Write-Host ""
    Write-Host "[SUCCESS] Build completed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "[VERSION] Package version: $FullVersion" -ForegroundColor Cyan
    Write-Host "[INFO] Artifacts created in: $ArtifactsPath" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Contents:" -ForegroundColor White
    Get-ChildItem $ArtifactsPath | ForEach-Object {
        $size = if ($_.PSIsContainer) { "(folder)" } else { "($([math]::Round($_.Length / 1MB, 2)) MB)" }
        Write-Host "  * $($_.Name) $size" -ForegroundColor Gray
    }

    Write-Host ""
    Write-Host "[DONE] Ready for distribution!" -ForegroundColor Green
}
finally
{
    # Restore original working directory
    Pop-Location
}
