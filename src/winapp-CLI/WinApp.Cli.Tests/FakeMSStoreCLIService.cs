// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake Microsoft Store Developer CLI service. Records whether the availability check ran and
/// returns a configurable executable path so the command can launch a harmless real process
/// (e.g. cmd.exe) instead of downloading and running the real msstore CLI.
/// </summary>
internal sealed class FakeMSStoreCLIService : IMSStoreCLIService
{
    public int EnsureAvailableCallCount { get; private set; }

    /// <summary>When set, <see cref="EnsureMSStoreCLIAvailableAsync"/> throws this exception.</summary>
    public Exception? EnsureException { get; set; }

    /// <summary>Path returned by <see cref="GetMSStoreCLIPath"/>. Defaults to cmd.exe for tests.</summary>
    public string CliPath { get; set; } = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public Task EnsureMSStoreCLIAvailableAsync(CancellationToken cancellationToken = default)
    {
        EnsureAvailableCallCount++;
        if (EnsureException != null)
        {
            throw EnsureException;
        }
        return Task.CompletedTask;
    }

    public string GetMSStoreCLIPath() => CliPath;
}
