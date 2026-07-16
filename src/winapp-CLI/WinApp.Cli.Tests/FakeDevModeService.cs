// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Configurable dev mode fake. Defaults preserve the simple "already enabled, enable succeeds"
/// behavior (<see cref="IsEnabled"/> returns <c>true</c>, <see cref="EnsureWin11DevModeAsync"/>
/// returns <c>0</c>) that most consumers rely on, while exposing knobs so tests can drive the
/// "Configuring developer mode" sub-task in <see cref="WorkspaceSetupService"/> (enable succeeds /
/// returns -1 / throws) and simulate Developer Mode becoming enabled mid-setup.
/// </summary>
internal class FakeDevModeService : IDevModeService
{
    public bool IsEnabledResult { get; set; } = true;
    public int EnsureResult { get; set; }
    public Exception? EnsureThrows { get; set; }
    public int EnsureCallCount { get; private set; }

    /// <summary>
    /// When set, each call to <see cref="IsEnabled"/> dequeues one value (falling back to
    /// <see cref="IsEnabledResult"/> once exhausted). Lets a test simulate Developer Mode becoming
    /// enabled between the initial prompt check and the "Configuring developer mode" sub-task.
    /// </summary>
    public Queue<bool>? IsEnabledSequence { get; set; }

    public bool IsEnabled()
    {
        if (IsEnabledSequence is { Count: > 0 })
        {
            return IsEnabledSequence.Dequeue();
        }

        return IsEnabledResult;
    }

    public Task<int> EnsureWin11DevModeAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        EnsureCallCount++;
        if (EnsureThrows != null)
        {
            throw EnsureThrows;
        }

        return Task.FromResult(EnsureResult);
    }
}
