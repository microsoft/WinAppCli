#!/usr/bin/env pwsh
# Copyright (c) Microsoft Corporation. Licensed under the MIT License.
#
# Verifies that every C# source file under protocol/ carries the MIT license header
# on its first line. Runs cross-platform (Windows / hosted Linux CI) under pwsh.
# Exit 0 = all good; exit 1 = one or more files missing the header.
[CmdletBinding()]
param(
    # Defaults to the protocol/ folder (the parent of this scripts/ directory).
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$expected = '// Copyright (c) Microsoft Corporation. Licensed under the MIT License.'

$missing = @()
Get-ChildItem -Path $Root -Recurse -File -Filter *.cs |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    ForEach-Object {
        $first = Get-Content -LiteralPath $_.FullName -TotalCount 1
        if ($first -ne $expected) { $missing += $_.FullName }
    }

if ($missing.Count -gt 0) {
    Write-Host "License-header check FAILED - $($missing.Count) file(s) missing the MIT header:"
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host "Expected first line: $expected"
    exit 1
}

Write-Host "License-header check passed (all protocol C# files carry the MIT header)."
exit 0
