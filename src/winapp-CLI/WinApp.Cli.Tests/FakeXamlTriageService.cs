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

    public XamlTriageResult FakeResult { get; set; } = XamlTriageResult.None;

    /// <summary>When set, <see cref="TryAnalyzeAsync"/> throws this instead of returning a result.</summary>
    public Exception? ThrowOnAnalyze { get; set; }

    public Task<XamlTriageResult> TryAnalyzeAsync(string dumpPath, bool useSymbols, CancellationToken cancellationToken = default)
    {
        AnalyzeCalls.Add((dumpPath, useSymbols));
        if (ThrowOnAnalyze != null)
        {
            throw ThrowOnAnalyze;
        }

        return Task.FromResult(FakeResult);
    }
}
