#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Automate the release process for the WinApp VS Code Extension
.DESCRIPTION
    This script automates the VS Code extension release workflow:
    1. Verifies you are on the main branch with a clean working tree and latest changes
    2. Reads and optionally overrides the extension version from src/winapp-VSC/package.json
    3. Creates and pushes a vsc-rel/v{version} branch to origin (triggers the VSC release pipeline)
    4. Returns to main, bumps the patch version in src/winapp-VSC/package.json
    5. Creates a PR to merge the version bump back into main

    Prerequisites:
    - Git must be installed and authenticated with push access to origin
    - GitHub CLI (gh) must be installed and authenticated (for PR creation)
.PARAMETER SkipConfirmation
    Skip the interactive confirmation prompt before creating the release branch
.PARAMETER DryRun
    Show what would happen without making any changes (no branches created, no pushes, no PRs)
.PARAMETER Version
    Override the VS Code extension version instead of reading from package.json.
    Must be Major.Minor.Patch format.
.EXAMPLE
    .\scripts\start-vsc-release.ps1
.EXAMPLE
    .\scripts\start-vsc-release.ps1 -SkipConfirmation
.EXAMPLE
    .\scripts\start-vsc-release.ps1 -DryRun
.EXAMPLE
    .\scripts\start-vsc-release.ps1 -Version 0.2.0
#>

param(
    [switch]$SkipConfirmation = $false,
    [switch]$DryRun = $false,
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot | Split-Path -Parent
$VscPackageJsonPath = Join-Path $ProjectRoot "src\winapp-VSC\package.json"

# ─── Helpers ────────────────────────────────────────────────────────────────────

function Write-Step  { param([string]$msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Info  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Gray }
function Write-Ok    { param([string]$msg) Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Yellow }

function Confirm-Step {
    param([string]$Prompt)
    if ($script:SkipConfirmation -or $script:DryRun) { return }
    Write-Host ""
    $response = Read-Host "  $Prompt (y/N)"
    if ($response -notin @("y", "Y", "yes", "Yes")) {
        Write-Warn "Release cancelled by user."
        exit 0
    }
}

function Invoke-GitOrDryRun {
    param([string]$Description, [string[]]$Arguments)
    if ($DryRun) {
        Write-Warn "[DRY RUN] git $($Arguments -join ' ')"
    } else {
        Write-Info "git $($Arguments -join ' ')"
        & git @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
        }
    }
}

function Invoke-GhOrDryRun {
    param([string]$Description, [string[]]$Arguments)
    if ($DryRun) {
        Write-Warn "[DRY RUN] gh $($Arguments -join ' ')"
    } else {
        Write-Info "gh $($Arguments -join ' ')"
        & gh @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
        }
    }
}

# ─── Pre-flight checks ─────────────────────────────────────────────────────────

Push-Location $ProjectRoot
try {
    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Magenta
    Write-Host "║   WinApp VS Code Extension - Release         ║" -ForegroundColor Magenta
    Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Magenta

    if ($DryRun) {
        Write-Host ""
        Write-Warn "DRY RUN MODE — no changes will be made"
    }

    # 1. Check we are on main
    Write-Step "Checking current branch..."
    $currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($currentBranch -ne "main") {
        Write-Error "You must be on the 'main' branch to start a release. Current branch: '$currentBranch'. Please run: git checkout main"
        exit 1
    }
    Write-Ok "On branch: main"

    # 2. Check for clean working tree
    Write-Step "Checking working tree..."
    $status = git status --porcelain
    if ($status) {
        Write-Error "Working tree is not clean. Please commit or stash your changes first.`n$status"
        exit 1
    }
    Write-Ok "Working tree is clean"

    # 3. Pull latest from origin
    Write-Step "Pulling latest from origin/main..."
    Invoke-GitOrDryRun -Description "Fetch and pull latest" -Arguments @("pull", "--ff-only", "origin", "main")
    Write-Ok "Up to date with origin/main"

    # 4. Determine the release version
    if ($Version) {
        Write-Step "Using extension version override: $Version"
        $releaseVersion = $Version.Trim()
    } else {
        Write-Step "Reading version from src/winapp-VSC/package.json..."
        if (-not (Test-Path $VscPackageJsonPath)) {
            Write-Error "package.json not found at: $VscPackageJsonPath"
            exit 1
        }

        $packageJson = Get-Content $VscPackageJsonPath -Raw | ConvertFrom-Json
        $releaseVersion = $packageJson.version
        if (-not $releaseVersion) {
            Write-Error "Could not read 'version' property from package.json"
            exit 1
        }
    }
    Write-Ok "Extension version: $releaseVersion"

    # Validate version format
    if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') {
        Write-Error "Version '$releaseVersion' is not in the expected Major.Minor.Patch format"
        exit 1
    }

    $versionParts = $releaseVersion -split '\.'
    $major = [int]$versionParts[0]
    $minor = [int]$versionParts[1]
    $patch = [int]$versionParts[2]

    $releaseBranch = "vsc-rel/v$releaseVersion"
    $nextPatch = $patch + 1
    $nextVersion = "$major.$minor.$nextPatch"
    $bumpBranch = "bump/vsc-v$nextVersion"

    # 5. Check that the release branch doesn't already exist
    Write-Step "Checking for existing release branch..."
    $remoteBranches = git ls-remote --heads origin "vsc-rel/v*" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to list remote release branches from origin"
        exit 1
    }

    $releasedVersions = @()
    foreach ($line in ($remoteBranches -split "`n")) {
        if ($line -match 'refs/heads/vsc-rel/v(.+)$') {
            $releasedVersions += $matches[1].Trim()
        }
    }

    if ($releasedVersions -contains $releaseVersion) {
        Write-Error "Release branch '$releaseBranch' already exists on origin. Has this version already been released?"
        exit 1
    }
    Write-Ok "Branch '$releaseBranch' does not exist yet — good to go"

    Confirm-Step "Is '$releaseVersion' the correct version to release?"

    # 6. Confirm with user
    Write-Host ""
    Write-Host "  ┌─────────────────────────────────────────────┐" -ForegroundColor White
    Write-Host "  │  VSC Extension Release Plan                  │" -ForegroundColor White
    Write-Host "  │                                             │" -ForegroundColor White
    Write-Host "  │  Release version : $releaseVersion$((' ' * (25 - $releaseVersion.Length)))│" -ForegroundColor White
    Write-Host "  │  Release branch  : $releaseBranch$((' ' * (25 - $releaseBranch.Length)))│" -ForegroundColor White
    Write-Host "  │  Next dev version: $nextVersion$((' ' * (25 - $nextVersion.Length)))│" -ForegroundColor White
    Write-Host "  │  Bump branch     : $bumpBranch$((' ' * (25 - $bumpBranch.Length)))│" -ForegroundColor White
    Write-Host "  │                                             │" -ForegroundColor White
    Write-Host "  │  Steps:                                     │" -ForegroundColor White
    Write-Host "  │  1. Create & push '$releaseBranch'$((' ' * (10 - $releaseBranch.Length)))│" -ForegroundColor White
    Write-Host "  │  2. Bump version to $nextVersion on main$((' ' * (9 - $nextVersion.Length)))│" -ForegroundColor White
    Write-Host "  │  3. Create PR to merge bump into main       │" -ForegroundColor White
    Write-Host "  └─────────────────────────────────────────────┘" -ForegroundColor White

    Confirm-Step "Does this release plan look correct? Proceed?"

    # ─── Step 1: Create and push the release branch ─────────────────────────────

    Write-Step "Step 1/3: Creating release branch '$releaseBranch'..."
    Invoke-GitOrDryRun -Description "Create release branch" -Arguments @("checkout", "-b", $releaseBranch)
    Write-Ok "Local branch '$releaseBranch' created"

    # If -Version was provided, update the package.json on the release branch
    if ($Version) {
        Write-Step "Writing extension version '$releaseVersion' into package.json on release branch..."

        if ($DryRun) {
            Write-Warn "[DRY RUN] Would update src/winapp-VSC/package.json version to $releaseVersion and commit on $releaseBranch"
        } else {
            $vscPackageJson = Get-Content $VscPackageJsonPath -Raw | ConvertFrom-Json
            $vscCurrentVersion = $vscPackageJson.version
            $vscPackageJson.version = $releaseVersion
            $updatedJson = $vscPackageJson | ConvertTo-Json -Depth 100
            Set-Content -Path $VscPackageJsonPath -Value $updatedJson -NoNewline
            Write-Info "Updated VS Code extension version: $vscCurrentVersion -> $releaseVersion"
        }
        Invoke-GitOrDryRun -Description "Stage version update" -Arguments @("add", $VscPackageJsonPath)
        Invoke-GitOrDryRun -Description "Commit version update" -Arguments @("commit", "-m", "Set VS Code extension version to $releaseVersion for release")
    }

    Confirm-Step "Push '$releaseBranch' to origin? This will kick off the VSC release pipeline"

    Invoke-GitOrDryRun -Description "Push release branch" -Arguments @("push", "-u", "origin", $releaseBranch)
    Write-Ok "Release branch '$releaseBranch' pushed to origin"

    # ─── Step 2: Go back to main and bump the version ───────────────────────────

    Write-Step "Step 2/3: Bumping extension version to $nextVersion..."
    Invoke-GitOrDryRun -Description "Switch back to main" -Arguments @("checkout", "main")

    # Create the bump branch from main
    Invoke-GitOrDryRun -Description "Create bump branch" -Arguments @("checkout", "-b", $bumpBranch)

    # Update package.json
    if ($DryRun) {
        Write-Warn "[DRY RUN] Would update src/winapp-VSC/package.json: $releaseVersion -> $nextVersion"
    } else {
        $vscPackageJson = Get-Content $VscPackageJsonPath -Raw | ConvertFrom-Json
        $vscPackageJson.version = $nextVersion
        $updatedJson = $vscPackageJson | ConvertTo-Json -Depth 100
        Set-Content -Path $VscPackageJsonPath -Value $updatedJson -NoNewline
        Write-Info "Updated package.json: $releaseVersion -> $nextVersion"
    }

    Invoke-GitOrDryRun -Description "Stage version bump" -Arguments @("add", $VscPackageJsonPath)
    Invoke-GitOrDryRun -Description "Commit version bump" -Arguments @("commit", "-m", "Bump VS Code extension version to $nextVersion for development")

    Confirm-Step "Push version bump branch '$bumpBranch' to origin and create PR?"

    Invoke-GitOrDryRun -Description "Push bump branch" -Arguments @("push", "-u", "origin", $bumpBranch)
    Write-Ok "Bump branch '$bumpBranch' pushed to origin"

    # ─── Step 3: Create a PR for the version bump ───────────────────────────────

    Write-Step "Step 3/3: Creating pull request..."

    $prTitle = "Bump VS Code extension version to $nextVersion for development"
    $prBody  = "Auto-generated version bump after releasing VS Code extension v$releaseVersion.`n`nThis PR bumps the patch version in ``src/winapp-VSC/package.json`` from ``$releaseVersion`` to ``$nextVersion`` so that prerelease builds pick up the new version number."

    $ghAvailable = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $ghAvailable -and -not $DryRun) {
        $encodedTitle = [System.Uri]::EscapeDataString($prTitle)
        $encodedBody  = [System.Uri]::EscapeDataString($prBody)
        $prUrl = "https://github.com/microsoft/winappcli/compare/main...$($bumpBranch)?expand=1&title=$encodedTitle&body=$encodedBody"
        Write-Warn "GitHub CLI (gh) is not installed. Open this link to create the PR:"
        Write-Host ""
        Write-Host "    $prUrl" -ForegroundColor Yellow
        Write-Host ""
    } else {
        Invoke-GhOrDryRun -Description "Create pull request" -Arguments @(
            "pr", "create",
            "--base", "main",
            "--head", $bumpBranch,
            "--title", $prTitle,
            "--body", $prBody
        )
        Write-Ok "Pull request created"
    }

    # ─── Done ───────────────────────────────────────────────────────────────────

    Invoke-GitOrDryRun -Description "Switch back to main" -Arguments @("checkout", "main")

    Write-Host ""
    Write-Host "╔══════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║  VSC Extension release started!              ║" -ForegroundColor Green
    Write-Host "╠══════════════════════════════════════════════╣" -ForegroundColor Green
    Write-Host "║                                              ║" -ForegroundColor Green
    Write-Host "║  • Release branch '$releaseBranch' pushed$((' ' * (10 - $releaseBranch.Length)))║" -ForegroundColor Green
    Write-Host "║  • Version bump PR created for $nextVersion$((' ' * (10 - $nextVersion.Length)))║" -ForegroundColor Green
    Write-Host "║                                              ║" -ForegroundColor Green
    Write-Host "║  Next steps:                                 ║" -ForegroundColor Green
    Write-Host "║  1. Monitor the VSC release pipeline         ║" -ForegroundColor Green
    Write-Host "║  2. Review & merge the version bump PR       ║" -ForegroundColor Green
    Write-Host "╚══════════════════════════════════════════════╝" -ForegroundColor Green
    Write-Host ""

} catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host ""
    Write-Warn "The release process did not complete. You may need to manually clean up:"
    Write-Warn "  - Check your current branch: git rev-parse --abbrev-ref HEAD"
    Write-Warn "  - Switch back to main:       git checkout main"
    if ($releaseBranch) {
        Write-Warn "  - Delete local release branch: git branch -D $releaseBranch"
    }
    if ($bumpBranch) {
        Write-Warn "  - Delete local bump branch:    git branch -D $bumpBranch"
    }
    exit 1
} finally {
    Pop-Location
}
