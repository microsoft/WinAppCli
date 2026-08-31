#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Discard doc changes whose only difference is the ms.date stamp.
.DESCRIPTION
    port-mslearn-docs.ps1 stamps every generated page with today's date, so a
    release rewrites all of them even though most are byte-identical otherwise.
    That buries the handful of real edits in a wall of date-only diffs for the
    docs-repo reviewer.

    Run this inside a checkout of the docs repo after copying the ported docs in
    and before committing. Any tracked file whose diff against HEAD consists
    solely of the ms.date line is restored to HEAD, so it drops out of the commit
    entirely and keeps the date of the last release that actually changed it.

    Files that are new, deleted, or have any other change are left alone.
.PARAMETER RepoPath
    Path to the docs repo working tree. Defaults to the current directory.
.PARAMETER PathSpec
    Optional git pathspec limiting which files are considered, e.g.
    "hub/apps/dev-tools/winapp-cli". Defaults to the whole repo.
.PARAMETER WhatIf
    Report what would be discarded without touching anything.
.EXAMPLE
    .\scripts\prune-date-only-doc-changes.ps1 -RepoPath C:\src\windows-dev-docs-pr -PathSpec hub/apps/dev-tools/winapp-cli
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$RepoPath = ".",
    [string]$PathSpec = ""
)

$ErrorActionPreference = "Stop"

function Write-Step { param([string]$msg) Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Info { param([string]$msg) Write-Host "    $msg" -ForegroundColor Gray }
function Write-Ok   { param([string]$msg) Write-Host "    $msg" -ForegroundColor Green }

if (-not (Test-Path $RepoPath)) {
    throw "RepoPath does not exist: $RepoPath"
}
$repoFull = (Resolve-Path $RepoPath).Path

Push-Location $repoFull
try {
    # Compare against HEAD so the result is the same whether or not the caller
    # has already staged the copied files.
    $diffArgs = @('diff', 'HEAD', '--name-only', '--diff-filter=M')
    if ($PathSpec) { $diffArgs += @('--', $PathSpec) }

    $changed = @(& git @diffArgs 2>$null | Where-Object { $_ })
    if ($LASTEXITCODE -ne 0) { throw "git diff failed with exit code $LASTEXITCODE" }

    Write-Step "Scanning $($changed.Count) modified file(s) for date-only changes"

    $pruned = @()
    $kept = @()

    foreach ($file in $changed) {
        # Only markdown pages carry ms.date front matter; leave toc.yml and
        # anything else to be judged on its own content.
        if ($file -notmatch '\.md$') { $kept += $file; continue }

        $patch = & git diff HEAD -- $file 2>$null
        if ($LASTEXITCODE -ne 0) { throw "git diff failed for $file" }

        # Added/removed content lines, ignoring the +++/--- file headers.
        $changedLines = @($patch | Where-Object {
            ($_ -match '^\+(?!\+\+)') -or ($_ -match '^-(?!--)')
        })

        if ($changedLines.Count -eq 0) { $kept += $file; continue }

        $nonDate = @($changedLines | Where-Object { $_ -notmatch '^[+-]ms\.date:' })
        if ($nonDate.Count -eq 0) {
            $pruned += $file
        } else {
            $kept += $file
        }
    }

    if ($pruned.Count -gt 0) {
        if ($PSCmdlet.ShouldProcess("$($pruned.Count) file(s)", "restore to HEAD")) {
            # Restores index and working tree together, so the file drops out of
            # the commit whether or not it was already staged.
            foreach ($batch in ($pruned | ForEach-Object { $_ })) {
                & git checkout HEAD -- $batch
                if ($LASTEXITCODE -ne 0) { throw "git checkout failed for $batch" }
            }
        }
        foreach ($f in $pruned) { Write-Info "  date-only, discarded: $f" }
    }

    Write-Step "Done"
    Write-Ok "Discarded $($pruned.Count) date-only change(s); kept $($kept.Count) real change(s)."
    foreach ($f in $kept) { Write-Info "  kept: $f" }
}
finally {
    Pop-Location
}
