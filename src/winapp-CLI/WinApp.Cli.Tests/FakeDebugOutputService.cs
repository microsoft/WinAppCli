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

    /// <summary>
    /// When set, the debug loop cancels this token source *during* the loop (before returning),
    /// standing in for a Ctrl+C that arrives while the debugger is attached. The loop still
    /// returns <see cref="FakeExitCode"/> normally so the command exercises its post-loop
    /// cancellation-cleanup branch (terminate the package's processes).
    /// </summary>
    public CancellationTokenSource? CancelTokenDuringLoop { get; set; }

    public Task<int> RunDebugLoopAsync(uint processId, CancellationToken cancellationToken, bool useSymbols = false, IReadOnlyList<string>? symbolSearchPaths = null)
    {
        AttachCalls.Add(processId);
        CancelTokenDuringLoop?.Cancel();
        return Task.FromResult(FakeExitCode);
    }
}
