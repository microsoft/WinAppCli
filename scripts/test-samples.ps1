<#
.SYNOPSIS
Local orchestrator to run sample & guide tests.

.DESCRIPTION
Discovers and runs test.ps1 for each sample (or a specified subset).
Each test validates the corresponding guide workflow from scratch and
verifies the existing sample code still builds.
Reports a pass/fail summary at the end.

.PARAMETER Samples
One or more sample names to test. Defaults to all samples that have a test.ps1.

.PARAMETER WinappPath
Path to the winapp npm package (.tgz or directory) passed to each test.

.PARAMETER SkipCleanup
Passed through to each test — keep build artifacts for debugging.

.PARAMETER Verbose
Enable verbose output for all tests.

.EXAMPLE
.\scripts\test-samples.ps1
Run all sample tests.

.EXAMPLE
.\scripts\test-samples.ps1 -Samples dotnet-app,rust-app -Verbose
Run only the dotnet-app and rust-app tests with verbose output.
#>

[CmdletBinding()]
param(
    [string[]]$Samples,
    [string]$WinappPath,
    [switch]$SkipCleanup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$samplesRoot = Join-Path $PSScriptRoot "..\samples"

# Discover samples with test.ps1
$allTests = @(Get-ChildItem -Path $samplesRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "test.ps1") } |
    Select-Object -ExpandProperty Name)

if ($Samples) {
    # Validate requested samples exist
    foreach ($s in $Samples) {
        if ($s -notin $allTests) {
            Write-Warning "Sample '$s' does not have a test.ps1 — skipping"
        }
    }
    $testList = @($Samples | Where-Object { $_ -in $allTests })
} else {
    $testList = $allTests
}

if (-not $testList) {
    Write-Host "No sample tests to run." -ForegroundColor Yellow
    exit 0
}

Write-Host "`n$('='*80)" -ForegroundColor Cyan
Write-Host "SAMPLE & GUIDE TEST RUNNER — $($testList.Count) test(s)" -ForegroundColor Cyan
Write-Host "$('='*80)`n" -ForegroundColor Cyan

$results = @()

foreach ($sample in $testList) {
    $testScript = Join-Path $samplesRoot $sample "test.ps1"
    Write-Host "`nRunning: $sample" -ForegroundColor Yellow
    Write-Host ("-" * 40) -ForegroundColor DarkGray

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $params = @{}
        if ($WinappPath)  { $params['WinappPath']  = $WinappPath }
        if ($SkipCleanup) { $params['SkipCleanup'] = $true }
        if ($VerbosePreference -eq 'Continue') { $params['Verbose'] = $true }

        & $testScript @params
        $sw.Stop()
        $results += [PSCustomObject]@{ Sample = $sample; Status = 'PASS'; Duration = $sw.Elapsed; Error = $null }
    } catch {
        $sw.Stop()
        Write-Host "  ✗ $sample FAILED: $_" -ForegroundColor Red
        $results += [PSCustomObject]@{ Sample = $sample; Status = 'FAIL'; Duration = $sw.Elapsed; Error = $_.ToString() }
    }
}

# Summary
Write-Host "`n$('='*80)" -ForegroundColor Cyan
Write-Host "RESULTS SUMMARY" -ForegroundColor Cyan
Write-Host "$('='*80)" -ForegroundColor Cyan

$passed = @($results | Where-Object Status -eq 'PASS').Count
$failed = @($results | Where-Object Status -eq 'FAIL').Count

foreach ($r in $results) {
    $color = if ($r.Status -eq 'PASS') { 'Green' } else { 'Red' }
    $dur = "{0:mm\:ss}" -f $r.Duration
    $line = "  [{0}] {1} ({2})" -f $r.Status, $r.Sample, $dur
    Write-Host $line -ForegroundColor $color
    if ($r.Error) {
        Write-Host "        $($r.Error)" -ForegroundColor DarkRed
    }
}

Write-Host "`n  $passed passed, $failed failed out of $($results.Count) test(s)`n" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($failed -gt 0) {
    exit 1
}
