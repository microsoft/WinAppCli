#!/usr/bin/env pwsh
# Copyright (c) Microsoft Corporation. Licensed under the MIT License.
#
# Public-appropriateness scan for the ported protocol assets. This repo is public-bound, so the
# ported contract must not reference internal-only artifacts. Fails (exit 1) on any hit for:
#   * the dropped agent tool-manifest facade,
#   * internal probe labels,
#   * origin/provenance disclaimer language,
#   * internal repository names,
#   * the injection/platform runtime component name,
#   * internal transport source-path references.
# The search terms are assembled from fragments so this scanner never matches itself.
#
# Scope: all of protocol/ (minus build/generated output) PLUS the cross-cutting docs this port also
# touches — the devtools specs (specs/winapp-devtools-*.md) and the repo AGENTS.md. The devtools glob
# deliberately excludes specs/winapp-run-csproj.md (a different workstream owns it).
[CmdletBinding()]
param(
    # Repo root; defaults to two levels up from this scripts/ directory (protocol/scripts -> protocol -> repo root).
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'

$terms = @(
    'M' + 'CP'
    'SM' + '-D'
    'clean' + '-room'
    'nikol' + 'ame'
    'win' + '-devex'
    'Framework' + 'Udk'
    'Initialize' + 'Xaml'
    'src[\\/]' + 'Transport'
)
$rx = ($terms -join '|')

$targets = [System.Collections.Generic.List[System.IO.FileInfo]]::new()

# 1. The whole protocol/ folder, excluding build output and generated facades.
$protocolDir = Join-Path $RepoRoot 'protocol'
Get-ChildItem -Path $protocolDir -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
        $_.FullName -notmatch '[\\/]gen[\\/]out[\\/]'
    } | ForEach-Object { $targets.Add($_) }

# 2. The devtools specs this port edits (overview + protocol), and their siblings for good measure.
$specsDir = Join-Path $RepoRoot 'specs'
if (Test-Path $specsDir) {
    Get-ChildItem -Path $specsDir -Filter 'winapp-devtools-*.md' -File | ForEach-Object { $targets.Add($_) }
}

# 3. The repo AGENTS.md (this port adds a protocol/ pointer to it).
$agents = Join-Path $RepoRoot 'AGENTS.md'
if (Test-Path $agents) { $targets.Add((Get-Item $agents)) }

$hits = $targets | Select-String -Pattern $rx

if ($hits) {
    Write-Host "Public-appropriateness scan FAILED - $($hits.Count) forbidden reference(s):"
    foreach ($h in $hits) {
        $rel = $h.Path.Substring($RepoRoot.Length).TrimStart('\', '/')
        Write-Host ("  {0}:{1}: {2}" -f $rel, $h.LineNumber, $h.Line.Trim())
    }
    exit 1
}

Write-Host "Public-appropriateness scan passed (zero forbidden references under protocol/, specs/winapp-devtools-*.md, AGENTS.md)."
exit 0
