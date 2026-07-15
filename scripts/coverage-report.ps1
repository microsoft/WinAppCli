#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Measure and report *meaningful* code coverage for the winapp CLI test suite.

.DESCRIPTION
    Runs the WinApp.Cli.Tests suite with Microsoft code coverage, applying
    src/winapp-CLI/coverage.runsettings so that auto-generated interop code
    (CsWin32 P/Invoke thunks, ComInterfaceGenerator COM shims, RegexGenerator
    state machines under obj\**) is excluded from the denominator.

    Without that exclusion the raw number is dominated by generated code and
    reports ~18% instead of the real ~49% over hand-written source. See issue #630.

    The script parses the produced Cobertura report, de-duplicates line hits across
    partial classes / build configs, and prints:
      * overall line coverage over hand-written product source,
      * a per-directory breakdown,
      * the top uncovered files (biggest gaps),
      * optionally a per-area (single directory) view.

    With -Threshold it fails (exit 1) when overall coverage is below the target,
    so it can be used as a CI gate.

.PARAMETER Configuration
    Build configuration to test. Default: Release.

.PARAMETER Filter
    Optional MTP test filter (e.g. "FullyQualifiedName~MsixService"). Note: a filtered
    run only instruments the code that actually loads, so the denominator will be smaller
    than a full run. Use a full run for the authoritative number.

.PARAMETER Area
    Optional product sub-directory to focus the per-file report on (e.g. Services,
    Commands, Helpers). The overall/per-directory numbers still reflect the whole suite.

.PARAMETER Threshold
    Optional overall line-coverage percentage (0-100). If set and coverage is below it,
    the script exits with code 1.

.PARAMETER Top
    Number of top uncovered files to list. Default: 40.

.PARAMETER SkipBuild
    Skip building the test project (assumes it is already built for -Configuration).

.PARAMETER CoberturaPath
    Use an existing Cobertura XML report instead of running the tests.

.PARAMETER CsvOut
    Optional path to write the full per-file report as CSV.

.EXAMPLE
    ./scripts/coverage-report.ps1

.EXAMPLE
    ./scripts/coverage-report.ps1 -Area Services -Top 60

.EXAMPLE
    ./scripts/coverage-report.ps1 -Filter "FullyQualifiedName~MsixService" -SkipBuild

.EXAMPLE
    ./scripts/coverage-report.ps1 -Threshold 95
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Filter,
    [string]$Area,
    [ValidateRange(0, 100)][double]$Threshold = -1,
    [int]$Top = 40,
    [switch]$SkipBuild,
    [string]$CoberturaPath,
    [string]$CsvOut
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CliSolutionDir = Join-Path $RepoRoot "src\winapp-CLI"
$TestsProject = Join-Path $CliSolutionDir "WinApp.Cli.Tests\WinApp.Cli.Tests.csproj"
$Settings = Join-Path $CliSolutionDir "coverage.runsettings"
$ResultsDir = Join-Path $CliSolutionDir "TestResults\coverage-report"
$TestExitCode = 0

function Write-Section($text) {
    Write-Host ""
    Write-Host $text -ForegroundColor Cyan
    Write-Host ("-" * $text.Length) -ForegroundColor DarkGray
}

if (-not $CoberturaPath) {
    if (-not (Test-Path $Settings)) {
        throw "Coverage settings not found: $Settings"
    }

    if (-not $SkipBuild) {
        Write-Section "Building test project ($Configuration)"
        dotnet build $TestsProject -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Test project build failed." }
    }

    if (Test-Path $ResultsDir) { Remove-Item $ResultsDir -Recurse -Force }
    New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

    Write-Section "Running tests with coverage"
    $runArgs = @(
        "run", "--project", $TestsProject, "-c", $Configuration, "--no-build",
        "--results-directory", $ResultsDir,
        "--coverage", "--coverage-settings", $Settings,
        "--coverage-output-format", "cobertura", "--coverage-output", "coverage.cobertura.xml"
    )
    if ($Filter) { $runArgs += @("--filter", $Filter) }
    dotnet @runArgs
    $TestExitCode = $LASTEXITCODE
    # MTP returns non-zero when tests fail. We still parse and print coverage below (a failed
    # run usually still emits a report), then propagate the failure via the exit code at the end
    # so this script can't green-light a run whose tests actually failed.
    if ($TestExitCode -ne 0) {
        Write-Host "WARNING: test run exited with code $TestExitCode (test failures). Coverage is still reported below." -ForegroundColor Yellow
    }

    $CoberturaPath = Get-ChildItem -Path $ResultsDir -Filter "*.cobertura.xml" -Recurse -File |
        Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    if (-not $CoberturaPath) { throw "No Cobertura report was produced under $ResultsDir." }
}

Write-Section "Parsing coverage report"
Write-Host $CoberturaPath -ForegroundColor DarkGray
[xml]$xml = Get-Content $CoberturaPath

# De-duplicate line hits per source file (partial classes and multiple build configs
# produce several <class> entries for the same file); keep the max hit count per line.
$byFile = @{}
foreach ($cls in $xml.coverage.packages.package.classes.class) {
    $fn = $cls.filename
    if (-not $fn) { continue }
    if ($fn -match '\\obj\\') { continue }              # defensive: settings already exclude these
    if ($fn -notmatch '\\WinApp\.Cli\\') { continue }   # product source only
    if (-not $cls.lines.line) { continue }
    if (-not $byFile.ContainsKey($fn)) { $byFile[$fn] = @{} }
    foreach ($ln in @($cls.lines.line)) {
        $num = [int]$ln.number
        $hits = [int]$ln.hits
        if (-not $byFile[$fn].ContainsKey($num) -or $byFile[$fn][$num] -lt $hits) {
            $byFile[$fn][$num] = $hits
        }
    }
}

if ($byFile.Count -eq 0) {
    throw "No hand-written product source found in the coverage report. Was the suite run against WinApp.Cli?"
}

$rows = foreach ($fn in $byFile.Keys) {
    $valid = $byFile[$fn].Count
    $covered = @($byFile[$fn].Values | Where-Object { $_ -gt 0 }).Count
    $rel = $fn -replace '.*\\WinApp\.Cli\\', ''
    [pscustomobject]@{
        File      = $rel
        Dir       = ($rel -split '\\')[0]
        Valid     = $valid
        Covered   = $covered
        Uncovered = $valid - $covered
        Pct       = [math]::Round($covered / $valid * 100, 1)
    }
}

$totalValid = ($rows | Measure-Object Valid -Sum).Sum
$totalCovered = ($rows | Measure-Object Covered -Sum).Sum
$overall = [math]::Round($totalCovered / $totalValid * 100, 2)

if ($CsvOut) {
    $rows | Sort-Object Uncovered -Descending | Export-Csv $CsvOut -NoTypeInformation
    Write-Host "Full per-file report written to $CsvOut" -ForegroundColor DarkGray
}

Write-Section "Coverage by directory"
$rows | Group-Object Dir | ForEach-Object {
    $v = ($_.Group | Measure-Object Valid -Sum).Sum
    $c = ($_.Group | Measure-Object Covered -Sum).Sum
    [pscustomobject]@{
        Dir       = $_.Name
        Files     = $_.Count
        Valid     = $v
        Covered   = $c
        Pct       = [math]::Round($c / $v * 100, 1)
        Uncovered = $v - $c
    }
} | Sort-Object Uncovered -Descending | Format-Table -AutoSize | Out-String | Write-Host

$reportRows = $rows
if ($Area) {
    $reportRows = $rows | Where-Object { $_.Dir -ieq $Area -or $_.File -ilike "$Area\*" }
    Write-Section "Top $Top uncovered files in '$Area'"
} else {
    Write-Section "Top $Top uncovered files"
}
$reportRows | Sort-Object Uncovered -Descending | Select-Object -First $Top |
    Format-Table @{n = 'File'; e = { $_.File }; w = 60 }, Valid, Covered, Uncovered, Pct -AutoSize |
    Out-String | Write-Host

Write-Section "Overall (hand-written product source)"
Write-Host ("  Files:     {0}" -f $rows.Count)
Write-Host ("  Lines:     {0} covered / {1} valid" -f $totalCovered, $totalValid)
$color = if ($overall -ge 95) { "Green" } elseif ($overall -ge 75) { "Yellow" } else { "Red" }
Write-Host ("  Coverage:  {0}%" -f $overall) -ForegroundColor $color

if ($Threshold -ge 0) {
    if ($overall -lt $Threshold) {
        Write-Host ("FAIL: coverage {0}% is below threshold {1}%." -f $overall, $Threshold) -ForegroundColor Red
        exit 1
    }
    Write-Host ("PASS: coverage {0}% meets threshold {1}%." -f $overall, $Threshold) -ForegroundColor Green
}

if ($TestExitCode -ne 0) {
    Write-Host ("FAIL: the test run reported failures (exit code {0}); failing despite the coverage report above." -f $TestExitCode) -ForegroundColor Red
    exit $TestExitCode
}
