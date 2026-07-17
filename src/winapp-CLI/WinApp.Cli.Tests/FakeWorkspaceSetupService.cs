// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake workspace-setup service that records calls and returns configurable results
/// without touching NuGet, the filesystem, or build tools.
/// </summary>
internal sealed class FakeWorkspaceSetupService : IWorkspaceSetupService
{
    public List<WorkspaceSetupOptions> SetupWorkspaceCalls { get; } = [];
    public List<DirectoryInfo> InstallRuntimeCalls { get; } = [];

    /// <summary>Exit code returned by <see cref="SetupWorkspaceAsync"/>.</summary>
    public int SetupWorkspaceResult { get; set; }

    /// <summary>Directory returned by <see cref="FindWindowsAppSdkMsixDirectory"/> (null = not found).</summary>
    public DirectoryInfo? MsixDirectory { get; set; }

    /// <summary>Result returned by <see cref="InstallWindowsAppRuntimeAsync"/>.</summary>
    public (int InstalledCount, int ErrorCount) InstallRuntimeResult { get; set; } = (1, 0);

    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null)
    {
        return MsixDirectory;
    }

    public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
    {
        SetupWorkspaceCalls.Add(options);
        return Task.FromResult(SetupWorkspaceResult);
    }

    /// <summary>Value returned by <see cref="IsWindowsAppRuntimeRegistered"/>.</summary>
    public bool IsRuntimeRegisteredResult { get; set; } = true;

    public Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null)
    {
        InstallRuntimeCalls.Add(msixDir);
        return Task.FromResult((InstallRuntimeResult.InstalledCount, InstallRuntimeResult.ErrorCount, (IReadOnlyList<(string Name, string Version)>)[]));
    }

    public bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<(string Name, string Version)>? expectedRuntimePackages = null)
        => IsRuntimeRegisteredResult;
}
