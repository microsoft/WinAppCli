// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake WinUI triage service that records calls and returns a configurable result
/// without hosting DbgEng or downloading any debugging binaries.
/// </summary>
internal class FakeXamlTriageService : IXamlTriageService
{
    public List<(string DumpPath, bool UseSymbols)> AnalyzeCalls { get; } = [];

    public string? FakeResult { get; set; }

    public Task<string?> TryAnalyzeAsync(string dumpPath, bool useSymbols, CancellationToken cancellationToken = default)
    {
        AnalyzeCalls.Add((dumpPath, useSymbols));
        return Task.FromResult(FakeResult);
    }
}
