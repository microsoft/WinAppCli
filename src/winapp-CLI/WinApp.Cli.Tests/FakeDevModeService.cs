// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

internal class FakeDevModeService : IDevModeService
{
    /// <summary>
    /// Controls the value returned by <see cref="IsEnabled"/>. Defaults to true
    /// so existing callers keep the enabled-developer-mode behavior; set to false
    /// to exercise the "Developer Mode not enabled" guard paths.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public Task<int> EnsureWin11DevModeAsync(TaskContext taskContext, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }

    public bool IsEnabled()
    {
        return Enabled;
    }
}
