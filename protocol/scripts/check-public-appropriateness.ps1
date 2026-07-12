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
[CmdletBinding()]
param(
    # Defaults to the protocol/ folder (the parent of this scripts/ directory).
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
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

$hits = Get-ChildItem -Path $Root -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
        $_.FullName -notmatch '[\\/]gen[\\/]out[\\/]'
    } |
    Select-String -Pattern $rx

if ($hits) {
    Write-Host "Public-appropriateness scan FAILED - $($hits.Count) forbidden reference(s):"
    foreach ($h in $hits) {
        $rel = $h.Path.Substring($Root.Length).TrimStart('\', '/')
        Write-Host ("  {0}:{1}: {2}" -f $rel, $h.LineNumber, $h.Line.Trim())
    }
    exit 1
}

Write-Host "Public-appropriateness scan passed (zero forbidden references under protocol/)."
exit 0
