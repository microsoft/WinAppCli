<#
.SYNOPSIS
  Source-resolution census (Gate 1) harness. Orchestrates the census across a build-config matrix and
  hands the collected per-element data to the tested analysis layer, which grades every element,
  aggregates by SourceKind, and returns a GO / CONDITIONAL / KILL / INCONCLUSIVE verdict.

.DESCRIPTION
  Productization deliberately splits "collect" from "analyze":

    * ANALYZE (this harness drives it; pure, CI-runnable) — the winapp-provenance-census tool reads a
      directory of census TSVs (handle, type, name, file, line, col), applies the source-provenance
      grader (spec section 4) and the Gate-1 kill-criteria (spec section 5), and writes
      results/census-latest.md + results/census-latest.json.

    * COLLECT (heavy gate; NOT in this repo) — producing a fresh TSV means launching a real app and
      reading its live UI element tree with the required runtime component. That collector is owned by
      the "reading the UI" workstream, needs an interactive desktop, and is supplied to this harness
      through the -Collector seam. Until it lands, run the harness in -AnalyzeOnly mode over an
      existing corpus of TSVs (e.g. the committed reference corpus under results/).

  This keeps the honesty model and the verdict in one tested place (the analyzer) instead of a script.

.PARAMETER Pages
  Fixture page labels that make up one census sweep (used only when collecting).

.PARAMETER Configs
  Build-config labels to sweep. The analyzer judges Gate-1 on the non-stripping Release config;
  'release-nolineinfo' is the diagnostic line-info-stripped probe (never the arbiter). 'packaged' and
  'trimmed' are defined for the full matrix but need desktop/packaged collection.

.PARAMETER TsvDir
  Directory the census TSVs are read from (defaults to ResultsDir).

.PARAMETER ResultsDir
  Directory the published report (census-latest.md/json) is written to.

.PARAMETER Collector
  Heavy-gate seam: a script block invoked as & $Collector $configLabel $page $outTsv to collect one
  TSV from a running app via the required runtime component. Omit to run analyze-only.

.PARAMETER FixtureProject
  Optional path to an app project to build per config before collecting (owned by the reading-the-UI
  workstream; not required for analyze-only runs).

.PARAMETER AnalyzeOnly
  Skip collection entirely and (re)analyze the TSVs already in TsvDir. This is the CI / no-desktop path.

.EXAMPLE
  # No-desktop: re-analyze the committed reference corpus and refresh the published rates.
  pwsh src/winapp-devtools/census/Run-Census.ps1 -AnalyzeOnly `
      -TsvDir src/winapp-devtools/provenance/WinApp.DevTools.Provenance.Tests/Fixtures/census

.EXAMPLE
  # Heavy gate (desktop): collect fresh TSVs with an operator-supplied collector, then analyze.
  pwsh src/winapp-devtools/census/Run-Census.ps1 -Collector $myCollector -FixtureProject path/to/app
#>
[CmdletBinding()]
param(
    [string[]]$Pages   = @('SmokePage', 'Repeater', 'Items', 'UcHost', 'XBindFn'),
    [string[]]$Configs = @('debug', 'release', 'release-nolineinfo'),
    [string]$TsvDir,
    [string]$ResultsDir,
    [scriptblock]$Collector,
    [string]$FixtureProject,
    [switch]$AnalyzeOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$ResultsDir = if ($ResultsDir) { $ResultsDir } else { Join-Path $root 'results' }
$TsvDir = if ($TsvDir) { $TsvDir } else { $ResultsDir }
$analyzerProj = Join-Path $root '..\provenance\WinApp.DevTools.Provenance.Census\WinApp.DevTools.Provenance.Census.csproj'

New-Item -ItemType Directory -Force $ResultsDir | Out-Null

# Build-config matrix. release-nolineinfo forces XAML line-info off (DisableXbfLineInfo is a public,
# supported switch) so we can measure the worst-case "line-info stripped" behaviour.
$configMap = @{
    'debug'              = @{ Configuration = 'Debug';   Props = @() }
    'release'            = @{ Configuration = 'Release'; Props = @() }
    'release-nolineinfo' = @{ Configuration = 'Release'; Props = @('-p:DisableXbfLineInfo=true') }
    'packaged'           = @{ Configuration = 'Release'; Props = @() }
    'trimmed'            = @{ Configuration = 'Release'; Props = @('-p:PublishTrimmed=true') }
}

function Build-Fixture([string]$label) {
    if (-not $FixtureProject) { return }
    $c = $configMap[$label]
    if (-not $c) { throw "unknown config label '$label'" }
    Write-Host "==> building fixture [$label] ($($c.Configuration)) $($c.Props -join ' ')" -ForegroundColor Cyan
    $buildArgs = @('build', $FixtureProject, '-c', $c.Configuration, '--nologo') + $c.Props
    & dotnet @buildArgs 2>&1 | Select-Object -Last 3 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    if ($LASTEXITCODE -ne 0) { throw "fixture build failed for $label" }
}

# ---------------- collect (heavy gate; only when a collector is supplied) ----------------
if (-not $AnalyzeOnly -and $Collector) {
    foreach ($label in $Configs) {
        Build-Fixture $label
        foreach ($page in $Pages) {
            $outTsv = Join-Path $TsvDir "$label-$page.tsv"
            Write-Host "==> collecting [$label/$page] -> $(Split-Path $outTsv -Leaf)" -ForegroundColor Cyan
            & $Collector $label $page $outTsv
            if ($LASTEXITCODE -ne 0) { Write-Host "    collector failed for $label/$page" -ForegroundColor Red }
        }
    }
}
elseif (-not $AnalyzeOnly) {
    Write-Host "==> no -Collector supplied; running analyze-only over existing TSVs in $TsvDir" -ForegroundColor Yellow
    Write-Host "    (fresh collection is a heavy gate: it needs a desktop + the reading-the-UI collector)" -ForegroundColor DarkGray
}

# ---------------- analyze (pure; delegates grading + verdict to the tested library) ----------------
if (-not (Get-ChildItem -Path $TsvDir -Filter *.tsv -ErrorAction SilentlyContinue)) {
    throw "no census TSVs found in $TsvDir - collect some (heavy gate) or point -TsvDir at a corpus (e.g. the reference corpus)."
}

Write-Host "==> analyzing census TSVs in $TsvDir" -ForegroundColor Cyan
& dotnet run --project $analyzerProj -c Release -- analyze $TsvDir --out $ResultsDir
$verdictExit = $LASTEXITCODE

Write-Host ""
Write-Host "report: $(Join-Path $ResultsDir 'census-latest.md')" -ForegroundColor Cyan
exit $verdictExit
