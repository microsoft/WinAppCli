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
    public List<(string Package, string Version)> InstalledPackages { get; } = [];

    /// <summary>
    /// Set this to the test cache directory to enable NuGet cache path resolution in tests.
    /// </summary>
    public DirectoryInfo? CacheDirectory { get; set; }

    /// <summary>
    /// Packages listed here will cause <see cref="GetLatestVersionAsync"/> to throw an exception,
    /// simulating a transient NuGet failure for that specific package.
    /// </summary>
    public HashSet<string> PackagesToThrow { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When set alongside <see cref="CancelOnQuery"/>, querying this package cancels that token source and
    /// throws, simulating a user Ctrl+C landing during a latest-version lookup for that package.
    /// </summary>
    public string? CancelOnQueryPackage { get; set; }

    /// <summary>
    /// The token source cancelled when <see cref="CancelOnQueryPackage"/> is queried. Wire this to the same
    /// token passed into the command so the handler observes a genuine cancellation.
    /// </summary>
    public CancellationTokenSource? CancelOnQuery { get; set; }

    public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        QueriedPackages.Add(packageName);
        if (CancelOnQueryPackage is not null && string.Equals(packageName, CancelOnQueryPackage, StringComparison.OrdinalIgnoreCase))
        {
            CancelOnQuery?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (PackagesToThrow.Contains(packageName))
        {
            throw new InvalidOperationException($"Simulated NuGet failure for {packageName}");
        }
        return Task.FromResult(DefaultVersion);
    }

    public Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        InstalledPackages.Add((package, version));
        return Task.FromResult(new Dictionary<string, string> { [package] = version });
    }

    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Dictionary<string, string>());
    }

    public DirectoryInfo GetNuGetGlobalPackagesDir()
    {
        if (CacheDirectory == null)
        {
            throw new InvalidOperationException("FakeNugetService.CacheDirectory must be set before calling GetNuGetGlobalPackagesDir");
        }
        var dir = new DirectoryInfo(Path.Combine(CacheDirectory.FullName, "packages"));
        if (!dir.Exists)
        {
            dir.Create();
        }
        return dir;
    }

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
    {
        var cache = GetNuGetGlobalPackagesDir();
        return new DirectoryInfo(Path.Combine(cache.FullName, packageName.ToLowerInvariant(), version));
    }
}
