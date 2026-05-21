// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake debug output service that records calls without actually attaching a debugger.
/// </summary>
internal class FakeDebugOutputService : IDebugOutputService
{
    public List<uint> AttachCalls { get; } = [];
    public int FakeExitCode { get; set; }

    public Task<int> RunDebugLoopAsync(uint processId, CancellationToken cancellationToken, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        AttachCalls.Add(processId);
        return Task.FromResult(FakeExitCode);
    }
}
