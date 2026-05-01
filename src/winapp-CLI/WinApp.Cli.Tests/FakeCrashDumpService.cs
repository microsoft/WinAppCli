// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake crash dump service that records calls without writing actual dumps.
/// </summary>
internal class FakeCrashDumpService : ICrashDumpService
{
    public List<(uint ProcessId, uint ThreadId)> WriteCalls { get; } = [];
    public string? FakeDumpPath { get; set; }
    public List<string> AnalyzeCalls { get; } = [];

    public string? WriteMiniDump(uint processId,
        byte[]? savedContext, uint savedThreadId,
        int savedExceptionCode, nuint savedExceptionAddress)
    {
        WriteCalls.Add((processId, savedThreadId));
        return FakeDumpPath;
    }

    public Task AnalyzeDumpAsync(string dumpPath, string logPath, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        AnalyzeCalls.Add(dumpPath);
        return Task.CompletedTask;
    }
}
