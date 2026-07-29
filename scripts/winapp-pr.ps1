#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Install a winapp CLI MSIX built by CI, from a PR, a branch, or a specific run.

.DESCRIPTION
    Dev-only selfhosting helper. Run it with no arguments to pick from a list of open pull
    requests. It resolves a "Build and Package" workflow run, downloads its msix-packages
    artifact, trusts the run's signing certificate, removes any previously installed winapp
    package (they share an app execution alias), and installs the new one.

    CI mints a fresh self-signed certificate on every build, so trusting it needs admin. Only
    that one step elevates -- the download runs unelevated so your `gh` credentials still apply.

    Run with -AddToPath once to install this script to a directory on your user PATH so
    `winapp-pr` works from anywhere.

.PARAMETER Target
    A PR number (690) or a branch name (main, zt/new-command). Omit it to choose interactively.

.PARAMETER Repo
    Repository to pull builds from, as owner/name. Defaults to $env:WINAPP_PR_REPO, then
    microsoft/winappCli. Use this to install from a private fork.

.PARAMETER Run
    Install from an explicit workflow run ID, bypassing PR/branch resolution.

.PARAMETER Arch
    Package architecture: x64 or arm64. Defaults to this machine's architecture.

.PARAMETER List
    Show recent candidate runs for the target instead of installing.

.PARAMETER Status
    Show the currently installed winapp package and exit.

.PARAMETER PruneCerts
    Remove trusted CI dev certificates left behind by previous installs, keeping the one the
    installed package needs. Requires admin.

.PARAMETER AddToPath
    Copy this script to %LOCALAPPDATA%\Programs\winapp-dev, add it to the user PATH, and exit.

.PARAMETER NonInteractive
    Skip the picker when no target is given and use the current git branch instead.

.PARAMETER Force
    Re-download the artifact even if a cached copy exists.

.EXAMPLE
    winapp-pr
    Pick an open PR (or the default branch) from a list and install it.

.EXAMPLE
    winapp-pr 690
    Install the latest build for PR 690.

.EXAMPLE
    winapp-pr main
    Install the latest build from main.

.EXAMPLE
    winapp-pr 42 -Repo contoso/winappCli-private
    Install PR 42 from a private fork.

.EXAMPLE
    winapp-pr -List
    Show recent runs for the current branch without installing.
#>

[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Position = 0)]
    [string]$Target,

    [string]$Repo,

    [long]$Run,

    [ValidateSet('x64', 'arm64')]
    [string]$Arch,

    [switch]$List,

    [switch]$Status,

    [switch]$PruneCerts,

    [switch]$AddToPath,

    [switch]$NonInteractive,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$WorkflowName = 'Build and Package'
$ArtifactName = 'msix-packages'
$PackageNames = @('winapp', 'winapp-dev')
$ToolHome     = Join-Path $env:LOCALAPPDATA 'winapp-dev'
$CacheRoot    = Join-Path $ToolHome 'cache'
$StateFile    = Join-Path $ToolHome 'current.json'
$InstallDir   = Join-Path $env:LOCALAPPDATA 'Programs\winapp-dev'

function Write-Step   { param([string]$m) Write-Host "`n>> $m" -ForegroundColor Cyan }
function Write-Ok     { param([string]$m) Write-Host "   [OK] $m" -ForegroundColor Green }
function Write-Detail { param([string]$m) Write-Host "   $m" -ForegroundColor Gray }
function Write-Warn   { param([string]$m) Write-Host "   [WARN] $m" -ForegroundColor Yellow }

function Fail {
    param([string]$Message)
    Write-Host "`n[ERROR] $Message" -ForegroundColor Red
    exit 1
}

function Get-HostArch {
    switch ($env:PROCESSOR_ARCHITECTURE) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { 'x64' }
    }
}

function Invoke-Gh {
    <# Runs gh and returns parsed JSON. stderr is kept out of the result so notices can't corrupt it. #>
    param([string[]]$Arguments, [switch]$Raw)

    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $output = & gh @Arguments 2>$errFile
        if ($LASTEXITCODE -ne 0) {
            $stderr = Get-Content $errFile -Raw -ErrorAction SilentlyContinue
            Fail "gh $($Arguments -join ' ') failed:`n$stderr"
        }
    }
    finally {
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
    }

    $text = ($output -join "`n").Trim()
    if ($Raw) { return $text }
    if (-not $text) { return $null }
    return $text | ConvertFrom-Json
}

function Get-InstalledWinapp {
    Get-AppxPackage | Where-Object { $PackageNames -contains $_.Name }
}

function Get-InstallState {
    <#
        Records what the last install came from. The installed package's own certificate can't be
        read back -- WindowsApps is ACL-locked -- so the thumbprint is tracked here instead.
    #>
    if (-not (Test-Path $StateFile)) { return $null }
    try { return Get-Content $StateFile -Raw | ConvertFrom-Json } catch { return $null }
}

function Set-InstallState {
    param([hashtable]$State)

    New-Item -ItemType Directory -Path $ToolHome -Force | Out-Null
    $State | ConvertTo-Json | Set-Content -Path $StateFile -Encoding UTF8
}

# ── Self-install ─────────────────────────────────────────────────────────────

function Install-ToPath {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

    $targetPs1 = Join-Path $InstallDir 'winapp-pr.ps1'
    if ($PSCommandPath -ne $targetPs1) {
        Copy-Item $PSCommandPath $targetPs1 -Force
    }

    # .cmd shim so the tool also works from cmd.exe and from Explorer's Run box
    $shim = @"
@echo off
where pwsh >nul 2>nul && (pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0winapp-pr.ps1" %*) || (powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0winapp-pr.ps1" %*)
"@
    Set-Content -Path (Join-Path $InstallDir 'winapp-pr.cmd') -Value $shim -Encoding ASCII

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if ($entries -notcontains $InstallDir) {
        [Environment]::SetEnvironmentVariable('Path', (@($entries + $InstallDir) -join ';'), 'User')
        Write-Ok "Added to user PATH: $InstallDir"
        Write-Detail 'Open a new terminal for the PATH change to take effect.'
    }
    else {
        Write-Ok "Already on user PATH: $InstallDir"
    }

    Write-Detail "Installed: $targetPs1"
    Write-Host "`nUsage: winapp-pr 690   |   winapp-pr main   |   winapp-pr -List" -ForegroundColor Cyan
}

# ── Target resolution ────────────────────────────────────────────────────────

function Resolve-Repo {
    if ($Repo) { return $Repo }
    if ($env:WINAPP_PR_REPO) { return $env:WINAPP_PR_REPO }
    return 'microsoft/winappCli'
}

function Resolve-CurrentBranch {
    param([string]$RepoName)

    $branch = & git rev-parse --abbrev-ref HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $branch -and $branch -ne 'HEAD') {
        return $branch.Trim()
    }
    $repoInfo = Invoke-Gh @('api', "repos/$RepoName", '--jq', '.default_branch') -Raw
    return $repoInfo.Trim()
}

function Get-LocalBranch {
    $branch = & git rev-parse --abbrev-ref HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $branch -and $branch -ne 'HEAD') { return $branch.Trim() }
    return ''
}

# ── Interactive picker ───────────────────────────────────────────────────────

function Get-RelativeAge {
    param([datetime]$When)

    $span = (Get-Date) - $When.ToLocalTime()
    if ($span.TotalMinutes -lt 60) { return "$([int]$span.TotalMinutes)m" }
    if ($span.TotalHours -lt 24)   { return "$([int]$span.TotalHours)h" }
    return "$([int]$span.TotalDays)d"
}

function Get-MenuItems {
    <# Open PRs newest-first, plus the default branch, as selectable targets. #>
    param([string]$RepoName)

    $prs = Invoke-Gh @('api',
        "repos/$RepoName/pulls?state=open&sort=updated&direction=desc&per_page=30",
        '--jq', '[.[] | {number, title, draft, branch: .head.ref, author: .user.login, updated: .updated_at}]')

    $state = Get-InstallState
    $localBranch = Get-LocalBranch

    $items = foreach ($pr in @($prs)) {
        $meta = @($pr.author, (Get-RelativeAge ([datetime]$pr.updated)))
        if ($pr.draft) { $meta = @($pr.author, 'draft', (Get-RelativeAge ([datetime]$pr.updated))) }

        [pscustomobject]@{
            Spec   = [string]$pr.number
            Name   = "#$($pr.number)"
            Title  = $pr.title
            Meta   = ($meta -join ' - ')
            Marker = if ($state -and $state.Branch -eq $pr.branch) { '*' }
                     elseif ($localBranch -eq $pr.branch) { '.' }
                     else { ' ' }
        }
    }

    $defaultBranch = Invoke-Gh @('api', "repos/$RepoName", '--jq', '.default_branch') -Raw
    $items = @($items) + [pscustomobject]@{
        Spec   = $defaultBranch
        Name   = $defaultBranch
        Title  = 'latest build from the default branch'
        Meta   = ''
        Marker = if ($state -and $state.Branch -eq $defaultBranch) { '*' } else { ' ' }
    }

    return @($items)
}

function Get-ConsoleWidth {
    try { return [Math]::Max(60, [Console]::WindowWidth - 1) } catch { return 100 }
}

function Format-MenuLines {
    <# Pure renderer so the layout can be verified without a live console. #>
    param([object[]]$Items, [int]$SelectedIndex, [int]$Width)

    $nameWidth = ($Items | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
    $metaWidth = ($Items | ForEach-Object { $_.Meta.Length } | Measure-Object -Maximum).Maximum

    $lines = for ($i = 0; $i -lt $Items.Count; $i++) {
        $item = $Items[$i]
        $cursor = if ($i -eq $SelectedIndex) { '>' } else { ' ' }
        $prefix = "$cursor$($item.Marker) $($item.Name.PadRight($nameWidth))  "
        $suffix = if ($item.Meta) { "  $($item.Meta.PadLeft($metaWidth))" } else { '' }

        $room = $Width - $prefix.Length - $suffix.Length
        $title = $item.Title
        if ($room -lt 10) { $room = 10 }
        if ($title.Length -gt $room) { $title = $title.Substring(0, $room - 3) + '...' }

        "$prefix$($title.PadRight($room))$suffix"
    }
    return @($lines)
}

function Show-InteractiveMenu {
    param([object[]]$Items)

    $width = Get-ConsoleWidth
    $selected = 0
    # Start on the entry for the current branch, if there is one.
    for ($i = 0; $i -lt $Items.Count; $i++) {
        if ($Items[$i].Marker -eq '.') { $selected = $i; break }
    }

    $lineCount = $Items.Count
    $top = [Console]::CursorTop

    [Console]::CursorVisible = $false
    try {
        while ($true) {
            [Console]::SetCursorPosition(0, $top)
            $lines = Format-MenuLines -Items $Items -SelectedIndex $selected -Width $width
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $color = if ($i -eq $selected) { 'Cyan' } else { 'Gray' }
                Write-Host $lines[$i].PadRight($width) -ForegroundColor $color
            }
            # Recomputed each pass so the menu stays anchored if the buffer scrolled.
            $top = [Console]::CursorTop - $lineCount

            $key = [Console]::ReadKey($true)
            switch ($key.Key) {
                'UpArrow'   { $selected = ($selected - 1 + $Items.Count) % $Items.Count }
                'DownArrow' { $selected = ($selected + 1) % $Items.Count }
                'Home'      { $selected = 0 }
                'End'       { $selected = $Items.Count - 1 }
                'Enter'     { return $Items[$selected] }
                'Escape'    { return $null }
                'Q'         { return $null }
            }
        }
    }
    finally {
        [Console]::CursorVisible = $true
    }
}

function Show-NumberedMenu {
    <# Used when the console can't do raw key input (redirected stdin, CI, some hosts). #>
    param([object[]]$Items)

    $width = Get-ConsoleWidth
    $lines = Format-MenuLines -Items $Items -SelectedIndex -1 -Width $width
    for ($i = 0; $i -lt $lines.Count; $i++) {
        Write-Host ("{0,3}. {1}" -f ($i + 1), $lines[$i].Substring(1))
    }

    $answer = try {
        Read-Host "`nSelect a build to install (1-$($Items.Count), blank to cancel)"
    }
    catch {
        Fail "Console input is not available here. Pass a target instead, e.g. winapp-pr 690"
    }
    if (-not $answer) { return $null }

    $index = 0
    if (-not [int]::TryParse($answer, [ref]$index) -or $index -lt 1 -or $index -gt $Items.Count) {
        Fail "'$answer' is not a valid selection."
    }
    return $Items[$index - 1]
}

function Select-InstallTarget {
    param([string]$RepoName)

    Write-Step "Open pull requests in $RepoName"
    $items = Get-MenuItems -RepoName $RepoName
    if (-not $items) { Fail "No open pull requests or branches found in $RepoName." }

    Write-Host "   * installed   . current branch   (Up/Down, Enter to install, Esc to cancel)`n" -ForegroundColor DarkGray

    $canDrawMenu = -not ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected)
    $choice = if ($canDrawMenu) {
        # Cursor control is unavailable in some hosts; fall back rather than fail.
        try { Show-InteractiveMenu -Items $items }
        catch { Show-NumberedMenu -Items $items }
    }
    else {
        Show-NumberedMenu -Items $items
    }

    if (-not $choice) {
        Write-Host "`nCancelled." -ForegroundColor Yellow
        exit 0
    }
    Write-Host ''
    return $choice.Spec
}

function Get-BranchRuns {
    param([string]$RepoName, [string]$Branch)

    $encoded = [uri]::EscapeDataString($Branch)
    $runs = Invoke-Gh @('api', "repos/$RepoName/actions/runs?branch=$encoded&per_page=50",
        '--jq', '.workflow_runs')
    return @($runs | Where-Object { $_.name -eq $WorkflowName } |
        Sort-Object { [datetime]$_.created_at } -Descending)
}

function Get-CandidateRuns {
    <#
        Returns Build and Package runs for the target, best first. For a PR, builds of the exact
        head commit come first, then older builds of the same branch so an in-progress or
        artifact-less newest run degrades to the previous good build instead of failing.
    #>
    param([string]$RepoName, [string]$TargetSpec)

    if ($TargetSpec -notmatch '^\d+$') {
        Write-Detail "Branch: $TargetSpec"
        return Get-BranchRuns -RepoName $RepoName -Branch $TargetSpec
    }

    $pr = Invoke-Gh @('api', "repos/$RepoName/pulls/$TargetSpec",
        '--jq', '{sha: .head.sha, branch: .head.ref, title: .title}')
    Write-Detail "PR #$TargetSpec  $($pr.title)"
    Write-Detail "Branch: $($pr.branch)  @ $($pr.sha.Substring(0,8))"

    $script:TargetHeadSha = $pr.sha

    $headRuns = Invoke-Gh @('api', "repos/$RepoName/actions/runs?head_sha=$($pr.sha)&per_page=100",
        '--jq', '.workflow_runs')
    $headRuns = @($headRuns | Where-Object { $_.name -eq $WorkflowName } |
        Sort-Object { [datetime]$_.created_at } -Descending)

    $branchRuns = Get-BranchRuns -RepoName $RepoName -Branch $pr.branch

    $seen = @{}
    return @($headRuns + $branchRuns | Where-Object {
        if ($seen.ContainsKey($_.id)) { return $false }
        $seen[$_.id] = $true
        return $true
    })
}

function Get-MsixArtifact {
    <# First run in the list that has a downloadable msix-packages artifact. #>
    param([string]$RepoName, [object[]]$Runs)

    foreach ($run in $Runs) {
        $artifacts = Invoke-Gh @('api', "repos/$RepoName/actions/runs/$($run.id)/artifacts",
            '--jq', '.artifacts')
        $msix = $artifacts | Where-Object { $_.name -eq $ArtifactName -and -not $_.expired } |
            Select-Object -First 1
        if ($msix) {
            return [pscustomobject]@{ Run = $run; Artifact = $msix }
        }
        Write-Detail "Run $($run.id) has no usable $ArtifactName artifact, trying older run..."
    }
    return $null
}

# ── Download ─────────────────────────────────────────────────────────────────

function Get-CachedMsixPackage {
    param([string]$RepoName, [object]$Run, [string]$Architecture)

    $runCache = Join-Path $CacheRoot "$($RepoName -replace '[\\/]', '_')-$($Run.id)"

    if ($Force -and (Test-Path $runCache)) {
        Remove-Item $runCache -Recurse -Force
    }

    $cached = if (Test-Path $runCache) {
        Get-ChildItem $runCache -Filter "*_$Architecture.msix" -ErrorAction SilentlyContinue |
            Select-Object -First 1
    }
    if ($cached) {
        Write-Ok "Using cached package: $($cached.Name)"
        return $cached
    }

    New-Item -ItemType Directory -Path $runCache -Force | Out-Null
    Write-Detail "Downloading $ArtifactName from run $($Run.id) (~33 MB)..."

    & gh run download $Run.id -R $RepoName -n $ArtifactName -D $runCache 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Remove-Item $runCache -Recurse -Force -ErrorAction SilentlyContinue
        Fail "Failed to download artifact from run $($Run.id)."
    }

    # Drop the other architecture and the bundled installer assets; we only need one package.
    Get-ChildItem $runCache -File |
        Where-Object { $_.Name -notlike "*_$Architecture.msix" } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $package = Get-ChildItem $runCache -Filter "*_$Architecture.msix" | Select-Object -First 1
    if (-not $package) {
        Fail "Artifact contained no $Architecture package."
    }

    Get-ChildItem $runCache -File | ForEach-Object { Unblock-File $_.FullName -ErrorAction SilentlyContinue }
    Write-Ok "Downloaded $($package.Name)"
    return $package
}

# ── Certificate trust ────────────────────────────────────────────────────────

function Read-P7xCertificate {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return $null }
    try {
        $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
        $cms.Decode([System.IO.File]::ReadAllBytes($Path))
        return $cms.Certificates[0]
    }
    catch {
        return $null
    }
}

function Get-PackageCertificate {
    param([string]$PackagePath)

    $signature = Get-AuthenticodeSignature -FilePath $PackagePath
    if ($signature -and $signature.SignerCertificate) {
        return $signature.SignerCertificate
    }

    # Unsigned-looking result usually just means the cert isn't trusted yet; read it out of the package.
    $temp = Join-Path $env:TEMP "winapp-pr-cert-$(Get-Random)"
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    try {
        $zip = Join-Path $temp 'package.zip'
        Copy-Item $PackagePath $zip -Force
        Expand-Archive -Path $zip -DestinationPath $temp -Force
        $p7x = Get-ChildItem $temp -Filter 'AppxSignature.p7x' -Recurse | Select-Object -First 1
        if (-not $p7x) { return $null }
        return Read-P7xCertificate -Path $p7x.FullName
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Grant-CertificateTrust {
    <# Imports the cert into LocalMachine\TrustedPeople, elevating only for that step. #>
    param([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $already = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint }
    if ($already) {
        Write-Ok "Certificate already trusted ($($Certificate.Thumbprint.Substring(0,12))...)"
        return
    }

    $cerPath = Join-Path $env:TEMP "winapp-pr-$($Certificate.Thumbprint).cer"
    [System.IO.File]::WriteAllBytes($cerPath, $Certificate.Export('Cert'))

    Write-Detail "Trusting $($Certificate.Subject) ($($Certificate.Thumbprint.Substring(0,12))...)"
    Write-Detail 'Approve the elevation prompt to add it to LocalMachine\TrustedPeople.'

    $command = "Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

    try {
        $proc = Start-Process $shell -Verb RunAs -Wait -PassThru `
            -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', $encoded)
    }
    catch {
        Fail "Elevation was cancelled or failed: $_"
    }
    finally {
        Remove-Item $cerPath -Force -ErrorAction SilentlyContinue
    }

    if ($proc.ExitCode -ne 0) {
        Fail "Certificate import failed with exit code $($proc.ExitCode)."
    }

    $verify = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint }
    if (-not $verify) {
        Fail 'Certificate import reported success but the certificate is not in the store.'
    }
    Write-Ok 'Certificate trusted'
}

function Remove-StaleCertificates {
    param([string]$KeepThumbprint)

    $isAdmin = (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    # These certs all share the CI runner's subject, which is what makes them safe to bulk-remove.
    $stale = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Subject -eq 'CN=runneradmin' -and $_.Thumbprint -ne $KeepThumbprint }

    if (-not $stale) {
        Write-Ok 'No stale CI certificates to remove'
        return
    }

    Write-Detail "Found $($stale.Count) stale CI certificate(s)"

    if ($isAdmin) {
        foreach ($cert in $stale) {
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)" -Force
        }
        Write-Ok "Removed $($stale.Count) certificate(s)"
        return
    }

    $removals = ($stale | ForEach-Object {
        "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$($_.Thumbprint)' -Force -ErrorAction SilentlyContinue"
    }) -join '; '
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($removals))
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

    $proc = Start-Process $shell -Verb RunAs -Wait -PassThru `
        -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', $encoded)
    if ($proc.ExitCode -ne 0) {
        Fail "Certificate cleanup failed with exit code $($proc.ExitCode)."
    }
    Write-Ok "Removed $($stale.Count) certificate(s)"
}

# ── Install ──────────────────────────────────────────────────────────────────

function Install-WinappPackage {
    param([string]$PackagePath)

    # Every build shares the winapp execution alias, so the old package has to go first.
    $existing = Get-InstalledWinapp
    foreach ($pkg in $existing) {
        Write-Detail "Removing $($pkg.Name) $($pkg.Version)"
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
    }

    Add-AppxPackage -Path $PackagePath -ForceApplicationShutdown -ErrorAction Stop
    Write-Ok 'Package installed'
}

function Show-Status {
    $installed = Get-InstalledWinapp
    if (-not $installed) {
        Write-Detail 'No winapp package is currently installed.'
        return
    }
    foreach ($pkg in $installed) {
        Write-Detail "$($pkg.Name) $($pkg.Version)  [$($pkg.Publisher)]"
    }

    $state = Get-InstallState
    if ($state -and $state.Version -eq ($installed | Select-Object -First 1).Version) {
        Write-Detail "Source: $($state.Repo)  $($state.Branch)  @ $($state.HeadSha)"
        Write-Detail "Run   : $($state.RunId)  installed $($state.InstalledAt)"
    }
}

# ── Main ─────────────────────────────────────────────────────────────────────

if ($AddToPath) {
    Write-Step 'Installing winapp-pr to your user PATH'
    Install-ToPath
    exit 0
}

if ($Status) {
    Write-Step 'Installed winapp package'
    Show-Status
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail 'The GitHub CLI (gh) is required. Install it from https://cli.github.com and run: gh auth login'
}

$repoName = Resolve-Repo
if ($repoName -notmatch '^[^/]+/[^/]+$') {
    Fail "Repo must be in owner/name form, got '$repoName'."
}

$architecture = if ($Arch) { $Arch } else { Get-HostArch }

if ($PruneCerts) {
    Write-Step 'Pruning stale CI certificates'
    $state = Get-InstallState
    $keep = ''
    if ($state -and $state.Thumbprint) {
        $keep = $state.Thumbprint
        Write-Detail "Keeping $($keep.Substring(0,12))... (used by the installed build)"
    }
    else {
        Write-Warn 'No install record found; reinstalling the current build will need re-trusting.'
    }
    Remove-StaleCertificates -KeepThumbprint $keep
    exit 0
}

if ($Run) {
    Write-Step "Resolving build from $repoName"
    $runDetail = Invoke-Gh @('api', "repos/$repoName/actions/runs/$Run")
    $runs = @($runDetail)
    Write-Detail "Run $Run  ($($runDetail.head_branch))"
}
else {
    $spec = $Target
    if (-not $spec) {
        $spec = if ($NonInteractive -or $List) {
            Resolve-CurrentBranch -RepoName $repoName
        }
        else {
            Select-InstallTarget -RepoName $repoName
        }
    }
    Write-Step "Resolving build from $repoName"
    $runs = Get-CandidateRuns -RepoName $repoName -TargetSpec $spec
    if (-not $runs) {
        Fail "No '$WorkflowName' runs found for '$spec' in $repoName."
    }
}

if ($List) {
    Write-Step 'Recent runs'
    $runs | Select-Object -First 10 | ForEach-Object {
        $when = ([datetime]$_.created_at).ToLocalTime().ToString('yyyy-MM-dd HH:mm')
        $state = if ($_.conclusion) { $_.conclusion } else { $_.status }
        Write-Detail ("{0,-12} {1}  {2,-10} {3}" -f $_.id, $when, $state, $_.head_sha.Substring(0, 8))
    }
    Write-Host "`nInstall a specific one with: winapp-pr -Run <id>" -ForegroundColor Cyan
    exit 0
}

$selected = Get-MsixArtifact -RepoName $repoName -Runs $runs
if (-not $selected) {
    Fail "No run for this target has an unexpired '$ArtifactName' artifact (they expire after 90 days)."
}

$runState = if ($selected.Run.conclusion) { $selected.Run.conclusion } else { $selected.Run.status }
Write-Ok "Run $($selected.Run.id)  [$runState]  $($selected.Run.head_sha.Substring(0,8))"
if ($runState -ne 'success') {
    Write-Warn "That run did not succeed; the package may be from a failing build."
}
if ($script:TargetHeadSha -and $selected.Run.head_sha -ne $script:TargetHeadSha) {
    Write-Warn "No package for the PR's head commit ($($script:TargetHeadSha.Substring(0,8))) yet -- using an earlier build."
}

Write-Step "Fetching $architecture package"
$package = Get-CachedMsixPackage -RepoName $repoName -Run $selected.Run -Architecture $architecture

Write-Step 'Trusting signing certificate'
$certificate = Get-PackageCertificate -PackagePath $package.FullName
if (-not $certificate) {
    Fail "Could not read a signing certificate from $($package.Name)."
}
Grant-CertificateTrust -Certificate $certificate

Write-Step 'Installing package'
Install-WinappPackage -PackagePath $package.FullName

Write-Step 'Verifying'
$installed = Get-InstalledWinapp | Select-Object -First 1
if ($installed) {
    Write-Ok "$($installed.Name) $($installed.Version)"
    Set-InstallState @{
        Repo        = $repoName
        RunId       = $selected.Run.id
        Branch      = $selected.Run.head_branch
        HeadSha     = $selected.Run.head_sha.Substring(0, 8)
        Version     = $installed.Version
        Thumbprint  = $certificate.Thumbprint
        InstalledAt = (Get-Date).ToString('s')
    }
}
$version = & winapp --version 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Detail "winapp --version -> $version"
}
else {
    Write-Warn "'winapp --version' failed; open a new terminal and try again."
}

Write-Host "`nDone. Run 'winapp-pr -PruneCerts' occasionally to clear old CI certificates.`n" -ForegroundColor Green
