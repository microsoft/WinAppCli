// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake package installation service for exercising the native/C++ SDK-install path of
/// WorkspaceSetupService without touching NuGet or the filesystem package layout.
/// </summary>
internal sealed class FakePackageInstallationService : IPackageInstallationService
{
    /// <summary>Versions returned from <see cref="InstallPackagesAsync"/>.</summary>
    public Dictionary<string, string> InstallResult { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, <see cref="InstallPackagesAsync"/> returns null to exercise the error branch.</summary>
    public bool ReturnNull { get; set; }

    /// <summary>
    /// When set, <see cref="InstallPackagesAsync"/> throws this exception to exercise the
    /// unexpected-failure catch in <see cref="WorkspaceSetupService"/>.
    /// </summary>
    public Exception? ThrowOnInstall { get; set; }

    public List<DirectoryInfo> InitializedWorkspaces { get; } = [];

    /// <summary>Package names passed to the most recent <see cref="InstallPackagesAsync"/> call.</summary>
    public string[] LastRequestedPackages { get; private set; } = [];

    public void InitializeWorkspace(DirectoryInfo rootDirectory) => InitializedWorkspaces.Add(rootDirectory);

    public Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
    {
        LastRequestedPackages = packages.ToArray();
        if (ThrowOnInstall != null)
        {
            throw ThrowOnInstall;
        }
        return Task.FromResult(ReturnNull ? null! : InstallResult);
    }

    public Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>Fake C++/WinRT service: reports a (dummy) cppwinrt.exe and no-ops projection generation.</summary>
internal sealed class FakeCppWinrtService : ICppWinrtService
{
    /// <summary>When true, <see cref="FindCppWinrtExe"/> returns null to exercise the not-found branch.</summary>
    public bool ReturnNullExe { get; set; }

    public int RunWithRspCallCount { get; private set; }

    public FileInfo? FindCppWinrtExe(DirectoryInfo packagesDir, IDictionary<string, string> usedVersions)
        => ReturnNullExe ? null : new FileInfo(Path.Combine(packagesDir.FullName, "cppwinrt.exe"));

    public Task RunWithRspAsync(FileInfo cppwinrtExe, IEnumerable<FileInfo> winmdInputs, DirectoryInfo outputDir, DirectoryInfo workingDirectory, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        RunWithRspCallCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Fake package layout service: no-ops the copy operations and returns a configurable winmd list.</summary>
internal sealed class FakePackageLayoutService : IPackageLayoutService
{
    /// <summary>Winmds returned from <see cref="FindWinmds"/>. Empty exercises the "no winmd" branch.</summary>
    public List<FileInfo> Winmds { get; set; } = [];

    public void CopyIncludesFromPackages(DirectoryInfo nugetCacheDir, DirectoryInfo includeOut, Dictionary<string, string> usedVersions) { }
    public void CopyLibsAllArch(DirectoryInfo nugetCacheDir, DirectoryInfo libRoot, Dictionary<string, string> usedVersions) { }
    public void CopyRuntimesAllArch(DirectoryInfo nugetCacheDir, DirectoryInfo binRoot, Dictionary<string, string> usedVersions) { }
    public IEnumerable<FileInfo> FindWinmds(DirectoryInfo nugetCacheDir, Dictionary<string, string> usedVersions) => Winmds;
}

/// <summary>Fake winmds lockfile service: records writes and never reads from disk.</summary>
internal sealed class FakeWinmdsLockfileService : IWinmdsLockfileService
{
    public int WriteCallCount { get; private set; }

    public FileInfo GetLockfilePath(DirectoryInfo winappDir)
        => new(Path.Combine(winappDir.FullName, "winmds.lock.json"));

    public Task WriteAsync(
        DirectoryInfo winappDir,
        IReadOnlyDictionary<string, string> usedVersions,
        IReadOnlyList<FileInfo> discoveredWinmds,
        DirectoryInfo nugetCacheDir,
        string? yamlPackagesHash = null,
        CancellationToken cancellationToken = default)
    {
        WriteCallCount++;
        return Task.CompletedTask;
    }

    public Task<WinmdsLockfile?> TryReadAsync(DirectoryInfo winappDir, CancellationToken cancellationToken = default)
        => Task.FromResult<WinmdsLockfile?>(null);
}
