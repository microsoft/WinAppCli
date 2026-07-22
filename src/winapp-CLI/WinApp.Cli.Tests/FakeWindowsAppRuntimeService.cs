// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake Windows App Runtime service that records calls and returns configurable results
/// without touching NuGet, the filesystem, or the machine's package registrations.
/// </summary>
internal sealed class FakeWindowsAppRuntimeService : IWindowsAppRuntimeService
{
    public List<DirectoryInfo> InstallRuntimeCalls { get; } = [];

    /// <summary>Directory returned by <see cref="FindWindowsAppSdkMsixDirectory"/> (null = not found).</summary>
    public DirectoryInfo? MsixDirectory { get; set; }

    /// <summary>Result returned by <see cref="InstallWindowsAppRuntimeAsync"/>.</summary>
    public (int InstalledCount, int ErrorCount) InstallRuntimeResult { get; set; } = (1, 0);

    /// <summary>Value returned by <see cref="IsWindowsAppRuntimeRegistered"/>.</summary>
    public bool IsRuntimeRegisteredResult { get; set; } = true;

    /// <summary>Records the <c>requireExactVersion</c> argument of the last <see cref="FindWindowsAppSdkMsixDirectory"/> call.</summary>
    public bool? LastRequireExactVersion { get; private set; }

    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null, bool requireExactVersion = false)
    {
        LastRequireExactVersion = requireExactVersion;
        return MsixDirectory;
    }

    public Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null)
    {
        InstallRuntimeCalls.Add(msixDir);
        return Task.FromResult((InstallRuntimeResult.InstalledCount, InstallRuntimeResult.ErrorCount, (IReadOnlyList<(string Name, string Version)>)[]));
    }

    public bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<(string Name, string Version)>? expectedRuntimePackages = null)
        => IsRuntimeRegisteredResult;
}
