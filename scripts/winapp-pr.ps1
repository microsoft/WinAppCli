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

    Install it on any machine, no clone required (needs the GitHub CLI):

        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/microsoft/winappCli/main/scripts/winapp-pr.ps1))) -AddToPath

    From a clone, run with -AddToPath instead. Either way the script is copied to a directory
    on your user PATH so `winapp-pr` works from anywhere; -UpdateTool refreshes it later.

    Run `winapp-pr -Help` for the full list of commands and options.

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

    [switch]$Uninstall,

    [switch]$All,

    [switch]$Update,

    [switch]$AddToPath,

    [switch]$UpdateTool,

    [switch]$NonInteractive,

    [switch]$Help,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Captured at script scope so -AddToPath works when the script is run straight from the web,
# where there is no file on disk to copy from.
$SelfSource = $MyInvocation.MyCommand.ScriptBlock.ToString()

$WorkflowName = 'Build and Package'
$ArtifactName = 'msix-packages'
$PackageNames = @('winapp', 'winapp-dev')
$SourceUrl    = 'https://raw.githubusercontent.com/microsoft/winappCli/main/scripts/winapp-pr.ps1'

# Control flow uses exceptions rather than `exit`: when this script is run straight from the
# web as a scriptblock, `exit` terminates the caller's whole session instead of just the script.
$FailSentinel   = 'winapp-pr:failed'
$CancelSentinel = 'winapp-pr:cancelled'

# Resolved by Assert-GhReady; may be a full path when gh was just installed and is not yet on PATH.
$GhExe = 'gh'

$ToolHome     = Join-Path $env:LOCALAPPDATA 'winapp-dev'
$CacheRoot    = Join-Path $ToolHome 'cache'
$StateFile    = Join-Path $ToolHome 'current.json'
$TrustedFile  = Join-Path $ToolHome 'trusted-certs.json'
$InstallDir   = Join-Path $env:LOCALAPPDATA 'Programs\winapp-dev'

function Write-Step   { param([string]$m) Write-Host "`n>> $m" -ForegroundColor Cyan }
function Write-Ok     { param([string]$m) Write-Host "   [OK] $m" -ForegroundColor Green }
function Write-Detail { param([string]$m) Write-Host "   $m" -ForegroundColor Gray }
function Write-Warn   { param([string]$m) Write-Host "   [WARN] $m" -ForegroundColor Yellow }

function Fail {
    param([string]$Message)
    Write-Host "`n[ERROR] $Message" -ForegroundColor Red
    throw $FailSentinel
}

function Get-HostArch {
    switch ($env:PROCESSOR_ARCHITECTURE) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { 'x64' }
    }
}

function Confirm-Action {
    param([string]$Question)

    if ([Console]::IsInputRedirected) { return $false }
    try { $answer = Read-Host "$Question [Y/n]" } catch { return $false }
    return ($answer -eq '' -or $answer -match '^[Yy]')
}

function Update-PathFromRegistry {
    <# winget installs gh machine-wide; this process won't see it without a refresh. #>
    $parts = @(
        [Environment]::GetEnvironmentVariable('Path', 'Machine'),
        [Environment]::GetEnvironmentVariable('Path', 'User')
    ) | Where-Object { $_ }
    $env:Path = $parts -join ';'
}

function Find-Gh {
    $command = Get-Command gh -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

function Install-GhCli {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Fail @'
The GitHub CLI is required to download build artifacts, and winget is not
available to install it automatically.

Install it from https://cli.github.com, then run:
  gh auth login
'@
    }

    Write-Detail 'Running: winget install --id GitHub.cli -e'
    & winget install --id GitHub.cli -e --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Fail "winget exited with code $LASTEXITCODE. Install the GitHub CLI from https://cli.github.com and re-run."
    }

    Update-PathFromRegistry
    $ghPath = Find-Gh
    if (-not $ghPath) {
        Fail 'The GitHub CLI was installed but could not be located. Open a new terminal and re-run.'
    }
    Write-Ok "GitHub CLI installed: $ghPath"
    return $ghPath
}

function Assert-GhReady {
    <#
        A token is unavoidable: GitHub serves run and artifact metadata anonymously but returns
        401 for the artifact zip itself, so there is no tokenless path to a build.
    #>
    $ghPath = Find-Gh
    if (-not $ghPath) {
        Write-Warn 'The GitHub CLI (gh) is required to download build artifacts, and is not installed.'
        if (-not (Confirm-Action '   Install it now with winget?')) {
            Fail @'
Install the GitHub CLI, then re-run:
  winget install --id GitHub.cli -e
  gh auth login
'@
        }
        $ghPath = Install-GhCli
    }
    $script:GhExe = $ghPath

    # Scoped to the active github.com account: a bare `gh auth status` fails when ANY configured
    # account on any host is stale, which would send people with a second account into sign-in.
    & $script:GhExe auth status --active --hostname github.com *> $null
    if ($LASTEXITCODE -eq 0) { return }

    # gh refuses to store credentials while an env token is set, so offering login would dead-end.
    $envTokenName = @('GH_TOKEN', 'GITHUB_TOKEN') |
        Where-Object { [Environment]::GetEnvironmentVariable($_) } |
        Select-Object -First 1
    if ($envTokenName) {
        Fail @"
GitHub rejected the token in `$env:$envTokenName.

Update it, or clear it and sign in interactively:
  Remove-Item Env:\$envTokenName
  gh auth login
"@
    }

    Write-Warn 'The GitHub CLI is installed but not signed in.'
    if (-not (Confirm-Action '   Sign in now?')) {
        Fail @'
Sign in, then re-run:
  gh auth login
'@
    }

    # gh drives its own prompts here, so let it own the console.
    & $script:GhExe auth login
    & $script:GhExe auth status --active --hostname github.com *> $null
    if ($LASTEXITCODE -ne 0) {
        Fail 'Still not signed in. Run gh auth login manually, then re-run.'
    }
    Write-Ok 'Signed in to GitHub'
}

function Get-GhAccounts {
    <# Logins gh has configured, so a permissions error can suggest a real alternative. #>
    $status = & $script:GhExe auth status 2>&1 | Out-String
    $logins = [regex]::Matches($status, 'account (\S+)') | ForEach-Object { $_.Groups[1].Value }
    return @($logins | Select-Object -Unique)
}

function Get-AccessHint {
    param([string]$Url)

    $repo = if ($Url -match 'repos/([^/?]+/[^/?]+)') { $Matches[1] } else { 'that repository' }

    $active = & $script:GhExe api user --jq '.login' 2>$null
    $active = if ($LASTEXITCODE -eq 0 -and $active) { $active.Trim() } else { 'unknown' }

    $others = @(Get-GhAccounts | Where-Object { $_ -ne $active })
    $lines = @(
        "Cannot see $repo as GitHub user '$active'.",
        '',
        'The repository may not exist, or this account may not have access to it.'
    )
    if ($others) {
        $lines += "Other accounts gh has configured: $($others -join ', ')"
        $lines += "Switch with:  gh auth switch --user $($others[0])"
    }
    $lines += 'For a private org repo the token must also be SSO-authorized for that organization.'
    return ($lines -join "`n")
}

function Invoke-Gh {
    <# Runs gh and returns parsed JSON. stderr is kept out of the result so notices can't corrupt it. #>
    param([string[]]$Arguments, [switch]$Raw)

    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $output = & $script:GhExe @Arguments 2>$errFile
        if ($LASTEXITCODE -ne 0) {
            $stderr = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
            if ($stderr -match 'HTTP 404') {
                Fail (Get-AccessHint -Url ($Arguments -join ' '))
            }
            if ($stderr -match 'HTTP 401') {
                Fail "GitHub rejected the stored credentials. Run: gh auth login"
            }
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

function Test-RunningInOwnProcess {
    <#
        True when PowerShell was launched specifically to run this script (pwsh -File ...), so
        our environment dies with it. When we are running inside the caller's session instead,
        changes to $env:Path reach them directly.
    #>
    $cmdArgs = [Environment]::GetCommandLineArgs()
    for ($i = 0; $i -lt $cmdArgs.Count - 1; $i++) {
        if ($cmdArgs[$i] -in '-File', '-f') { return $true }
    }
    return $false
}

function Install-ToPath {
    param([string]$Source)

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $targetPs1 = Join-Path $InstallDir 'winapp-pr.ps1'

    if ($Source) {
        Set-Content -Path $targetPs1 -Value $Source -Encoding UTF8
    }
    elseif (-not $PSCommandPath) {
        # Running straight from the web: our own source is the freshest copy there is.
        Set-Content -Path $targetPs1 -Value $SelfSource -Encoding UTF8
    }
    elseif ((Test-Path $PSCommandPath) -and $PSCommandPath -ne $targetPs1) {
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
    }
    else {
        Write-Ok "Already on user PATH: $InstallDir"
    }

    # The registry change only reaches new processes, so patch this session's PATH as well.
    # That reaches the caller whenever we are running inside their session, which covers both
    # the web one-liner and `& .\winapp-pr.ps1 -AddToPath` from a clone.
    if (@($env:Path -split ';') -notcontains $InstallDir) {
        $env:Path = "$env:Path;$InstallDir"
    }

    Write-Detail "Installed: $targetPs1"
    if (Test-RunningInOwnProcess) {
        Write-Detail 'Open a new terminal to use winapp-pr in this session.'
    }
    else {
        Write-Ok 'winapp-pr is ready to use in this session.'
    }
    Write-Host "`nUsage: winapp-pr   |   winapp-pr 690   |   winapp-pr main   |   winapp-pr -Status" -ForegroundColor Cyan
}

function Update-Self {
    Write-Detail "Downloading $SourceUrl"
    try {
        $latest = Invoke-RestMethod -Uri $SourceUrl -UseBasicParsing
    }
    catch {
        Fail "Could not download the latest winapp-pr: $_"
    }
    if (-not $latest -or $latest -notmatch 'winapp-pr') {
        Fail 'Downloaded content does not look like winapp-pr.ps1.'
    }
    Install-ToPath -Source $latest
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

    # Only claim a build is installed when the record still describes the installed package and
    # came from the repo being listed; branch names collide across forks.
    $installedBranch = ''
    $installedIsCurrent = $true
    $liveState = Test-StateMatchesInstall -State $state -Package (Get-InstalledWinapp | Select-Object -First 1)
    if ($liveState -and $state.Branch -and $state.Repo -eq $RepoName) {
        $installedBranch = $state.Branch
        $newest = Get-MsixArtifact -RepoName $RepoName -Quiet `
            -Runs (Get-BranchRuns -RepoName $RepoName -Branch $installedBranch -HeadRepo $state.HeadRepo)
        if ($newest -and $newest.Run.id -ne $state.RunId) { $installedIsCurrent = $false }
    }

    function Get-Marker {
        param([string]$Branch)
        if ($installedBranch -and $installedBranch -eq $Branch) {
            return $(if ($installedIsCurrent) { '*' } else { '^' })
        }
        if ($localBranch -and $localBranch -eq $Branch) { return '.' }
        return ' '
    }

    $items = foreach ($pr in @($prs)) {
        $meta = @($pr.author, (Get-RelativeAge ([datetime]$pr.updated)))
        if ($pr.draft) { $meta = @($pr.author, 'draft', (Get-RelativeAge ([datetime]$pr.updated))) }

        [pscustomobject]@{
            Spec   = [string]$pr.number
            Name   = "PR #$($pr.number)"
            Title  = $pr.title
            Meta   = ($meta -join ' - ')
            Marker = Get-Marker -Branch $pr.branch
        }
    }

    $defaultBranch = Invoke-Gh @('api', "repos/$RepoName", '--jq', '.default_branch') -Raw
    $items = @($items) + [pscustomobject]@{
        Spec   = $defaultBranch
        Name   = $defaultBranch
        Title  = 'latest build from the default branch'
        Meta   = ''
        Marker = Get-Marker -Branch $defaultBranch
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

    Write-Host "   * installed   ^ newer build available   . current branch   (Up/Down, Enter to install, Esc to cancel)`n" -ForegroundColor DarkGray

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
        throw $CancelSentinel
    }
    Write-Host ''
    return $choice.Spec
}

function Get-BranchRuns {
    param([string]$RepoName, [string]$Branch, [string]$HeadRepo)

    $encoded = [uri]::EscapeDataString($Branch)
    $runs = Invoke-Gh @('api', "repos/$RepoName/actions/runs?branch=$encoded&per_page=50",
        '--jq', '.workflow_runs')
    $runs = @($runs | Where-Object { $_.name -eq $WorkflowName })

    # Fork PRs run in the base repo and can share a branch name with another fork -- "main"
    # especially -- so pin to the head repository when the caller knows which one it wants.
    if ($HeadRepo) {
        $runs = @($runs | Where-Object { $_.head_repository.full_name -eq $HeadRepo })
    }

    return @($runs | Sort-Object { [datetime]$_.created_at } -Descending)
}

function Get-CandidateRuns {
    <#
        Returns Build and Package runs for the target, best first. For a PR, builds of the exact
        head commit come first, then older builds of the same branch so an in-progress or
        artifact-less newest run degrades to the previous good build instead of failing.
    #>
    param([string]$RepoName, [string]$TargetSpec, [string]$HeadRepo)

    if ($TargetSpec -notmatch '^\d+$') {
        Write-Detail "Branch: $TargetSpec"
        return Get-BranchRuns -RepoName $RepoName -Branch $TargetSpec -HeadRepo $HeadRepo
    }

    $pr = Invoke-Gh @('api', "repos/$RepoName/pulls/$TargetSpec",
        '--jq', '{sha: .head.sha, branch: .head.ref, title: .title}')
    Write-Detail "PR #$TargetSpec  $($pr.title)"
    Write-Detail "Branch: $($pr.branch)  @ $($pr.sha.Substring(0,8))"

    $script:TargetHeadSha = $pr.sha
    $script:TargetPr = $TargetSpec

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
    param([string]$RepoName, [object[]]$Runs, [switch]$Quiet)

    foreach ($run in $Runs) {
        # --paginate is load-bearing: the artifacts endpoint returns 30 per page, and a run that
        # uploads more than that pushes msix-packages onto page 2, where an unpaginated query
        # cannot see it. The symptom is indistinguishable from a run that genuinely never built a
        # package -- the walk-back to an older run looks like it is working, and silently hands
        # back a stale build. Do not remove this without checking .total_count on a busy run.
        $artifacts = Invoke-Gh @('api', "repos/$RepoName/actions/runs/$($run.id)/artifacts",
            '--paginate', '--jq', '.artifacts')
        $msix = $artifacts | Where-Object { $_.name -eq $ArtifactName -and -not $_.expired } |
            Select-Object -First 1
        if ($msix) {
            return [pscustomobject]@{ Run = $run; Artifact = $msix }
        }
        if (-not $Quiet) {
            Write-Detail "Run $($run.id) has no usable $ArtifactName artifact among its $(($artifacts | Measure-Object).Count), trying older run..."
        }
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

    & $script:GhExe run download $Run.id -R $RepoName -n $ArtifactName -D $runCache 2>&1 | Out-Null
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

function Test-Thumbprint {
    <# Thumbprints get interpolated into an elevated command, so treat them as untrusted input. #>
    param([string]$Value)
    return ($Value -match '^[0-9A-Fa-f]{40}$')
}

function Read-P7xCertificate {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return $null }
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        # AppxSignature.p7x is a PKCS#7 blob behind a 4-byte 'PKCX' magic; SignedCms rejects it.
        if ($bytes.Length -gt 4 -and
            $bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B -and $bytes[2] -eq 0x43 -and $bytes[3] -eq 0x58) {
            $bytes = $bytes[4..($bytes.Length - 1)]
        }
        $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms
        $cms.Decode($bytes)
        return $cms.Certificates[0]
    }
    catch {
        return $null
    }
}

function Get-InstalledCertThumbprint {
    <# The signing certificate of the installed package. CI mints one per run, so it is unique. #>
    param([object]$Package)

    if (-not $Package -or -not $Package.InstallLocation) { return '' }
    $cert = Read-P7xCertificate -Path (Join-Path $Package.InstallLocation 'AppxSignature.p7x')
    if ($cert) { return $cert.Thumbprint }
    return ''
}

function Get-TrackedCerts {
    <#
        Thumbprints this tool has trusted. TrustedPeople is a shared store holding unrelated
        anchors, so cleanup is driven by what we added rather than by matching on subject.
    #>
    if (-not (Test-Path $TrustedFile)) { return @() }
    try {
        $values = @(Get-Content $TrustedFile -Raw | ConvertFrom-Json)
    }
    catch {
        return @()
    }
    # This file is user-writable and its values are interpolated into an elevated command.
    return @($values | Where-Object { Test-Thumbprint -Value $_ })
}

function Set-TrackedCerts {
    param([string[]]$Thumbprints)

    New-Item -ItemType Directory -Path $ToolHome -Force | Out-Null
    $clean = @($Thumbprints | Where-Object { Test-Thumbprint -Value $_ })
    ,$clean | ConvertTo-Json | Set-Content -Path $TrustedFile -Encoding UTF8
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

    $tracked = Get-TrackedCerts

    $already = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint }
    if ($already) {
        Write-Ok "Certificate already trusted ($($Certificate.Thumbprint.Substring(0,12))...)"
        if ($tracked -notcontains $Certificate.Thumbprint) {
            Set-TrackedCerts -Thumbprints (@($tracked) + $Certificate.Thumbprint)
        }
        return
    }

    $cerPath = Join-Path $env:TEMP "winapp-pr-$($Certificate.Thumbprint).cer"
    [System.IO.File]::WriteAllBytes($cerPath, $Certificate.Export('Cert'))

    Write-Detail "Trusting $($Certificate.Subject) ($($Certificate.Thumbprint.Substring(0,12))...)"
    Write-Detail 'Approve the elevation prompt to add it to LocalMachine\TrustedPeople.'

    # Retire the certs we trusted for earlier builds in the same elevated step, so cleanup
    # never costs an extra prompt. Removing one does not affect an already-installed package.
    $retire = @($tracked | Where-Object { $_ -and $_ -ne $Certificate.Thumbprint -and (Test-Thumbprint -Value $_) })
    if ($retire) {
        Write-Detail "Also retiring $($retire.Count) certificate(s) from earlier installs."
    }

    if (-not (Test-Thumbprint -Value $Certificate.Thumbprint)) {
        Fail "Refusing to trust a certificate with an unexpected thumbprint format."
    }

    $statements = @(
        "Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    ) + @($retire | ForEach-Object {
        "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$_' -Force -ErrorAction SilentlyContinue"
    })

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($statements -join '; '))
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
    Set-TrackedCerts -Thumbprints @($Certificate.Thumbprint)
    Write-Ok 'Certificate trusted'
}

function Remove-TrustedCerts {
    <# Removes the given thumbprints from TrustedPeople, elevating once if needed. #>
    param([string[]]$Thumbprints)

    $targets = @($Thumbprints | Where-Object { Test-Thumbprint -Value $_ })
    if (-not $targets) { return $true }

    $isAdmin = (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($isAdmin) {
        foreach ($thumb in $targets) {
            Remove-Item "Cert:\LocalMachine\TrustedPeople\$thumb" -Force -ErrorAction SilentlyContinue
        }
        return $true
    }

    $removals = ($targets | ForEach-Object {
        "Remove-Item 'Cert:\LocalMachine\TrustedPeople\$_' -Force -ErrorAction SilentlyContinue"
    }) -join '; '
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($removals))
    $shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

    try {
        $proc = Start-Process $shell -Verb RunAs -Wait -PassThru `
            -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-EncodedCommand', $encoded)
    }
    catch {
        return $false
    }
    return ($proc.ExitCode -eq 0)
}

function Remove-StaleCertificates {
    param([string]$KeepThumbprint)

    # These certs all share the CI runner's subject, which is what makes them safe to bulk-remove.
    $stale = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object { $_.Subject -eq 'CN=runneradmin' -and $_.Thumbprint -ne $KeepThumbprint }

    if (-not $stale) {
        Write-Ok 'No stale CI certificates to remove'
        return
    }

    Write-Detail "Found $($stale.Count) stale CI certificate(s)"

    if (-not (Remove-TrustedCerts -Thumbprints @($stale.Thumbprint))) {
        Fail 'Certificate cleanup failed or was cancelled.'
    }
    Set-TrackedCerts -Thumbprints @(Get-TrackedCerts | Where-Object { $_ -eq $KeepThumbprint })
    Write-Ok "Removed $($stale.Count) certificate(s)"
}

function Get-CacheSize {
    if (-not (Test-Path $CacheRoot)) { return 0 }
    $bytes = (Get-ChildItem $CacheRoot -Recurse -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if (-not $bytes) { return 0 }
    return [math]::Round($bytes / 1MB)
}

function Invoke-Uninstall {
    <#
        Undo what this tool did: the installed build, the certificates trusted for it, and the
        downloads and records kept for it. -All also removes winapp-pr itself.
    #>
    $package = Get-InstalledWinapp | Select-Object -First 1
    $tracked = @(Get-TrackedCerts)
    $cacheMb = Get-CacheSize

    if (-not $package -and -not $tracked -and -not $cacheMb -and -not $All) {
        Write-Ok 'Nothing to remove.'
        return
    }

    Write-Detail 'This will remove:'
    if ($package) { Write-Detail "  - the installed package ($($package.Name) $($package.Version))" }
    if ($tracked) { Write-Detail "  - $($tracked.Count) CI certificate(s) trusted for it" }
    if ($cacheMb) { Write-Detail "  - $cacheMb MB of cached downloads" }
    if ($All)     { Write-Detail "  - winapp-pr itself, and its entry on your PATH" }

    if (-not $Force -and -not (Confirm-Action '   Continue?')) {
        Write-Host "`nCancelled." -ForegroundColor Yellow
        return
    }

    if ($package) {
        foreach ($pkg in Get-InstalledWinapp) {
            Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
            Write-Ok "Removed $($pkg.Name) $($pkg.Version)"
        }
    }

    if ($tracked) {
        if (Remove-TrustedCerts -Thumbprints $tracked) {
            Write-Ok "Removed $($tracked.Count) trusted certificate(s)"
        }
        else {
            Write-Warn 'Certificates were not removed; run winapp-pr -PruneCerts later.'
        }
    }

    $hadState = (Test-Path $ToolHome)
    foreach ($path in @($CacheRoot, $StateFile, $TrustedFile)) {
        Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $ToolHome -Recurse -Force -ErrorAction SilentlyContinue
    if ($hadState) { Write-Ok 'Removed cached downloads and install records' }

    if (-not $All) {
        Write-Host "`nDone. winapp-pr is still installed; run it again to install another build.`n" -ForegroundColor Green
        return
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = @($userPath -split ';' | Where-Object { $_ -and $_ -ne $InstallDir })
    [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
    $env:Path = (@($env:Path -split ';' | Where-Object { $_ -and $_ -ne $InstallDir }) -join ';')

    # A running .ps1 is read into memory rather than locked, so it can delete its own directory.
    Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $InstallDir) {
        Write-Warn "Could not remove $InstallDir; delete it manually."
    }
    else {
        Write-Ok 'Removed winapp-pr and its PATH entry'
    }
    Write-Host "`nDone. winapp-pr is fully removed.`n" -ForegroundColor Green
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

function Show-ScriptHelp {
    # Hand-written rather than Get-Help: it stays readable, and it works identically when the
    # script is run from the web as a scriptblock, where Get-Help has no file to read.
    # Written to the pipeline a line at a time so it can be piped, paged, or grepped.
    @'
winapp-pr - install winapp CLI MSIX builds produced by CI

USAGE
  winapp-pr [<pr>|<branch>] [options]

PICKING A BUILD
  winapp-pr                 choose from a list of open pull requests
  winapp-pr 690             a pull request
  winapp-pr main            a branch
  winapp-pr -Run <id>       an exact workflow run

COMMANDS
  -Update                   install the newest build of whatever you have
  -Status                   show the installed build and where it came from
  -List                     list recent builds for the target
  -Uninstall                remove the installed build and everything kept for it
                            (add -All to remove winapp-pr itself too)
  -PruneCerts               remove trusted CI certificates from past installs
                            (installs retire their own automatically)
  -AddToPath                install winapp-pr itself onto your PATH
  -UpdateTool               update winapp-pr itself
  -Help                     show this

OPTIONS
  -Repo <owner/name>        install from another repo, such as a private fork
  -Arch <x64|arm64>         override the detected architecture
  -Force                    reinstall even if that build is already installed,
                            or skip the -Uninstall confirmation
  -NonInteractive           skip the picker and use the current git branch

Installing a build replaces any winapp package already installed, and needs the
GitHub CLI; winapp-pr offers to install it and sign you in if it is missing.
Set $env:WINAPP_PR_REPO to change the default repo.
'@ -split "`r?`n"
}

function Get-InstalledArch {
    <# Maps the installed package's architecture onto the names used for artifacts. #>
    param([object]$Package)

    if (-not $Package) { return '' }
    switch ("$($Package.Architecture)".ToLower()) {
        'x64'   { 'x64' }
        'arm64' { 'arm64' }
        default { "$($Package.Architecture)".ToLower() }
    }
}

function Test-StateMatchesInstall {
    <#
        True when the install record still describes the package that is actually installed.
        A package installed by other means -- double-clicking an MSIX, setup-winapprun.ps1 --
        leaves the record describing a build that is no longer there.
    #>
    param([object]$State, [object]$Package)

    if (-not $State -or -not $Package) { return $false }

    # Prefer the signing certificate: dev versions are a commit count, so unrelated branches at
    # the same depth produce identical name/version/architecture and would compare equal.
    $installedThumb = Get-InstalledCertThumbprint -Package $Package
    if ($installedThumb) {
        return ($installedThumb -eq $State.Thumbprint)
    }

    # Signature unreadable: fall back to the weaker check rather than refusing to work.
    if ($State.Version -ne $Package.Version) { return $false }
    if ($State.Arch -and $State.Arch -ne (Get-InstalledArch -Package $Package)) { return $false }
    return $true
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
    if (Test-StateMatchesInstall -State $state -Package ($installed | Select-Object -First 1)) {
        # Name the PR as well as the branch: the picker offers builds by PR number, so that is
        # the identifier you chose it by.
        $source = @($state.Repo)
        if ($state.Pr) { $source += "PR #$($state.Pr)" }
        $source += $state.Branch
        Write-Detail "Source: $($source -join '  ')  @ $($state.HeadSha)"
        Write-Detail "Run   : $($state.RunId)  installed $($state.InstalledAt)"
    }
    elseif ($state) {
        Write-Warn 'This package was not installed by winapp-pr; its source is unknown.'
    }
}

# ── Main ─────────────────────────────────────────────────────────────────────

function Invoke-Main {
    if ($Help) {
        Show-ScriptHelp
        return
    }

    if ($UpdateTool) {
        Write-Step 'Updating winapp-pr from main'
        Update-Self
        return
    }

    if ($AddToPath) {
        Write-Step 'Installing winapp-pr to your user PATH'
        Install-ToPath
        return
    }

    if ($Status) {
        Write-Step 'Installed winapp package'
        Show-Status
        return
    }

    if ($Uninstall) {
        Write-Step 'Uninstalling'
        Invoke-Uninstall
        return
    }

    Assert-GhReady


    $repoName = Resolve-Repo
    $installState = Get-InstallState
    $installedPackage = Get-InstalledWinapp | Select-Object -First 1
    $stateIsLive = Test-StateMatchesInstall -State $installState -Package $installedPackage

    if ($Update) {
        if (-not $installState -or -not ($installState.Pr -or $installState.Branch)) {
            Fail 'No record of a previous install to update. Pass a PR number or branch instead.'
        }
        if ($installedPackage -and -not $stateIsLive) {
            Fail @"
The installed package ($($installedPackage.Name) $($installedPackage.Version)) was not installed
by winapp-pr, so there is no way to tell which build it came from.

Name what you want instead, for example:
  winapp-pr $($installState.Branch)
"@
        }
        # The record describes where this build came from, so only an explicit -Repo may
        # override it; WINAPP_PR_REPO is a default for new installs, not a redirect for this one.
        if (-not $Repo -and $installState.Repo) {
            $repoName = $installState.Repo
        }
    }

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
        return
    }

    if ($Run) {
        Write-Step "Resolving build from $repoName"
        $runDetail = Invoke-Gh @('api', "repos/$repoName/actions/runs/$Run")
        $runs = @($runDetail)
        Write-Detail "Run $Run  ($($runDetail.head_branch))"
    }
    else {
        $spec = $Target
        $updateHeadRepo = ''
        if (-not $spec -and $Update) {
            # Prefer the PR the build came from: a branch name alone can match another fork's
            # runs in this same base repo. Fall back to the branch, pinned to its head repo.
            if ($installState.Pr) {
                $spec = [string]$installState.Pr
                Write-Detail "Updating the installed build: PR #$($installState.Pr)"
            }
            else {
                $spec = $installState.Branch
                $updateHeadRepo = $installState.HeadRepo
                Write-Detail "Updating the installed build: $($installState.Branch)"
            }
        }
        if (-not $spec) {
            $spec = if ($NonInteractive -or $List) {
                Resolve-CurrentBranch -RepoName $repoName
            }
            else {
                Select-InstallTarget -RepoName $repoName
            }
        }
        Write-Step "Resolving build from $repoName"
        $runs = Get-CandidateRuns -RepoName $repoName -TargetSpec $spec -HeadRepo $updateHeadRepo
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
        return
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

    # Re-resolving often lands on the build already installed; don't churn the package for nothing.
    # Requires the record to still describe the installed package, and the same architecture,
    # so asking for a different -Arch is never mistaken for a no-op.
    if (-not $Force -and $stateIsLive -and
        $installState.RunId -eq $selected.Run.id -and
        (Get-InstalledArch -Package $installedPackage) -eq $architecture) {
        Write-Ok "Already on this build -- nothing to do."
        Write-Detail 'Use -Force to reinstall it anyway.'
        return
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
            Pr          = $script:TargetPr
            Branch      = $selected.Run.head_branch
            HeadRepo    = $selected.Run.head_repository.full_name
            HeadSha     = $selected.Run.head_sha.Substring(0, 8)
            Version     = $installed.Version
            Arch        = $architecture
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

    Write-Host "`nDone.`n" -ForegroundColor Green
}

$exitCode = 0
try {
    Invoke-Main
}
catch {
    switch ($_.Exception.Message) {
        $CancelSentinel { $exitCode = 0 }
        $FailSentinel   { $exitCode = 1 }
        default {
            Write-Host "`n[ERROR] $($_.Exception.Message)" -ForegroundColor Red
            $exitCode = 1
        }
    }
}

# Only a real script file may call exit: from a scriptblock it would close the caller's session.
if ($PSCommandPath) { exit $exitCode }
$global:LASTEXITCODE = $exitCode
