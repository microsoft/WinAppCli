// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface INugetService
{
    Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the NuGet v3 flat container API for the dependencies of a specific package version.
    /// Returns a dictionary mapping dependency package ID to its version (or version range).
    /// </summary>
    Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the NuGet global packages directory (typically ~/.nuget/packages/).
    /// Respects the NUGET_PACKAGES environment variable.
    /// </summary>
    DirectoryInfo GetNuGetGlobalPackagesDir();

    /// <summary>
    /// Returns the directory for a specific package version in the NuGet global packages cache.
    /// Uses the standard NuGet layout: {cache}/{lowercase-id}/{version}/
    /// </summary>
    DirectoryInfo GetNuGetPackageDir(string packageName, string version);
}
