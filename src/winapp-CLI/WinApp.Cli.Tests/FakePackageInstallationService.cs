// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake package-installation service that records calls and returns predictable version
/// dictionaries without touching NuGet or the filesystem.
/// </summary>
internal sealed class FakePackageInstallationService : IPackageInstallationService
{
    public List<DirectoryInfo> InitializeWorkspaceCalls { get; } = [];
    public List<(DirectoryInfo Root, string[] Packages, bool IgnoreConfig)> InstallPackagesCalls { get; } = [];
    public List<(DirectoryInfo Root, string PackageName, string? Version)> EnsurePackageCalls { get; } = [];

    /// <summary>Flattened names of every package passed to <see cref="InstallPackagesAsync"/>, for convenient assertions.</summary>
    public List<string> InstalledPackages => InstallPackagesCalls.SelectMany(c => c.Packages).ToList();

    /// <summary>Version map returned by <see cref="InstallPackagesAsync"/>. When null, echoes the requested packages.</summary>
    public Dictionary<string, string>? InstalledVersions { get; set; }

    /// <summary>Result returned by <see cref="EnsurePackageAsync"/>.</summary>
    public bool EnsurePackageResult { get; set; } = true;

    public void InitializeWorkspace(DirectoryInfo rootDirectory)
    {
        InitializeWorkspaceCalls.Add(rootDirectory);
    }

    public Task<Dictionary<string, string>> InstallPackagesAsync(
        DirectoryInfo rootDirectory,
        IEnumerable<string> packages,
        TaskContext taskContext,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        bool ignoreConfig = false,
        CancellationToken cancellationToken = default)
    {
        var packageArray = packages.ToArray();
        InstallPackagesCalls.Add((rootDirectory, packageArray, ignoreConfig));
        var result = InstalledVersions ?? packageArray.ToDictionary(p => p, _ => "1.0.0");
        return Task.FromResult(result);
    }

    public Task<bool> EnsurePackageAsync(
        DirectoryInfo rootDirectory,
        string packageName,
        TaskContext taskContext,
        string? version = null,
        SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
        CancellationToken cancellationToken = default)
    {
        EnsurePackageCalls.Add((rootDirectory, packageName, version));
        return Task.FromResult(EnsurePackageResult);
    }
}
