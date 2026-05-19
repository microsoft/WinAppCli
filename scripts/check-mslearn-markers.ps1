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

    $lost = @()
    foreach ($f in $baseMarked) {
        if ($headMarkedSet.ContainsKey($f)) { continue }
        if ($renameMap.ContainsKey($f) -and $headMarkedSet.ContainsKey($renameMap[$f])) {
            Write-Host "OK: '$f' was renamed to '$($renameMap[$f])' with marker preserved."
            continue
        }
        $lost += $f
    }

    if ($lost.Count -gt 0) {
        Write-Host "::error::The following docs were marked '<!-- mslearn: true -->' on $BaseRef but no longer are on HEAD:"
        foreach ($f in $lost) {
            Write-Host "::error file=${f}::Missing or moved '<!-- mslearn: true -->' marker"
        }
        Write-Host "::error::"
        Write-Host "::error::If the file was renamed, re-add the marker at its new path."
        Write-Host "::error::If you intentionally unpublished it from MS Learn, remove the"
        Write-Host "::error::marker on $BaseRef first (separate PR) so the diff is explicit."
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
