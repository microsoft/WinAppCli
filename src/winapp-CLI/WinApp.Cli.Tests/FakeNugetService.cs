// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake NuGet service that returns predictable versions without network calls.
/// Tracks which packages were queried for test assertions.
/// </summary>
internal class FakeNugetService : INugetService
{
    public string DefaultVersion { get; set; } = "1.6.0";
    public List<string> QueriedPackages { get; } = [];
    public List<(string Package, string Version, DirectoryInfo OutputDir)> InstalledPackages { get; } = [];

    public Task EnsureNugetExeAsync(DirectoryInfo winappDir, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        QueriedPackages.Add(packageName);
        return Task.FromResult(DefaultVersion);
    }

    public Task<Dictionary<string, string>> InstallPackageAsync(DirectoryInfo globalWinappDir, string package, string version, DirectoryInfo outputDir, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        InstalledPackages.Add((package, version, outputDir));
        return Task.FromResult(new Dictionary<string, string> { [package] = version });
    }

    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }
}
