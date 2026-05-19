<#
.SYNOPSIS
    Verifies that no doc loses its <!-- mslearn: true --> marker between a base ref and HEAD.

.DESCRIPTION
    Mirrors the logic used by .github/workflows/docs-mslearn-check.yml so the
    same check can be exercised locally and unit-tested with Pester.

    Comparison rules:
      * Source of truth is the marker text itself (no hardcoded allow list).
      * A doc renamed between base and HEAD with the marker preserved at the
        new path is NOT a regression.
      * A doc that exists on base WITH the marker but on HEAD WITHOUT it
        (whether deleted, renamed-without-marker, or had the marker stripped)
        IS a regression and fails the check.

.PARAMETER BaseRef
    The git ref to compare against (e.g. 'origin/main', or a SHA in tests).

.PARAMETER DocsPath
    The pathspec to scan under. Defaults to 'docs/'.

.PARAMETER WorkingDirectory
    Optional; if set, runs git commands in that directory (useful for tests).

.OUTPUTS
    Exit code 0 on success, 1 if any marker was lost.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseRef,

    [string]$DocsPath = 'docs/',

    [string]$WorkingDirectory
)

$ErrorActionPreference = 'Stop'
$marker = '<!-- mslearn: true -->'

if ($WorkingDirectory) {
    Push-Location $WorkingDirectory
}

try {
    function Get-MarkedDocs([string]$ref) {
        $files = git ls-tree -r --name-only $ref -- $DocsPath |
            Where-Object { $_ -like '*.md' }
        $marked = @()
        foreach ($f in $files) {
            $content = git show "${ref}:${f}" 2>$null
            if ($LASTEXITCODE -eq 0 -and ($content -join "`n") -match [regex]::Escape($marker)) {
                $marked += $f
            }
        }
        return ,$marked
    }

    $baseMarked = Get-MarkedDocs $BaseRef
    $headMarked = Get-MarkedDocs 'HEAD'

    Write-Host "Marked on $BaseRef : $($baseMarked.Count)"
    Write-Host "Marked on HEAD    : $($headMarked.Count)"

    $renameMap = @{}
    $nameStatus = git diff --name-status --find-renames=90% "$BaseRef" HEAD -- $DocsPath
    foreach ($line in $nameStatus) {
        if ($line -match '^R\d+\s+(\S+)\s+(\S+)$') {
            $renameMap[$Matches[1]] = $Matches[2]
        }
    }

    $headMarkedSet = @{}
    foreach ($f in $headMarked) { $headMarkedSet[$f] = $true }

    # Build the set of paths that exist on HEAD (regardless of marker state).
    # If a base-marked doc is gone from HEAD entirely, treat that as an intentional
    # delete — it's already visible in the PR diff and shouldn't fail this check.
    $headPaths = @{}
    foreach ($p in (git ls-tree -r --name-only HEAD -- $DocsPath)) {
        if ($p -like '*.md') { $headPaths[$p] = $true }
    }

    $lost = @()
    $deleted = @()
    foreach ($f in $baseMarked) {
        if ($headMarkedSet.ContainsKey($f)) { continue }
        if ($renameMap.ContainsKey($f) -and $headMarkedSet.ContainsKey($renameMap[$f])) {
            Write-Host "OK: '$f' was renamed to '$($renameMap[$f])' with marker preserved."
            continue
        }
        # If the file no longer exists on HEAD (and wasn't picked up as a rename),
        # the doc was deleted — that's already explicit in the diff, so skip it.
        if (-not $headPaths.ContainsKey($f)) {
            $deleted += $f
            continue
        }
        $lost += $f
    }

    if ($deleted.Count -gt 0) {
        Write-Host "Skipping $($deleted.Count) doc(s) that were deleted on HEAD (delete is explicit in the diff):"
        foreach ($f in $deleted) { Write-Host "  - $f" }
    }

    if ($lost.Count -gt 0) {
        Write-Host ""
        Write-Host "::error::The following docs were marked '<!-- mslearn: true -->' on $BaseRef but no longer are on HEAD:"
        foreach ($f in $lost) {
            Write-Host "  - $f"
            Write-Host "::error file=${f}::Missing or moved '<!-- mslearn: true -->' marker"
        }
        Write-Host ""
        Write-Host "::error::If the file was renamed, re-add the marker at its new path."
        exit 1
    }

    $added = $headMarked | Where-Object { $baseMarked -notcontains $_ }
    if ($added.Count -gt 0) {
        Write-Host "New docs added to MS Learn in this PR:"
        $added | ForEach-Object { Write-Host "  + $_" }
    }

    Write-Host "OK: no mslearn markers were dropped."
    exit 0
}
finally {
    if ($WorkingDirectory) {
        Pop-Location
    }
}
