// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

internal interface INugetService
{
    /// <summary>
    /// Resolves the latest listed version of <paramref name="packageName"/> for the given install
    /// channel from the sources configured in the user's nuget.config (honoring
    /// <c>&lt;packageSourceMapping&gt;</c>). Throws if an eligible source cannot be queried, so the
    /// result is never a partial "latest".
    /// </summary>
    Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and extracts <paramref name="package"/> (and its transitive dependencies) into the NuGet
    /// global packages folder, resolving sources and credentials from the user's nuget.config hierarchy.
    /// Returns a map of every installed package ID to its normalized on-disk version.
    /// </summary>
    Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the dependencies of a specific package version by reading the package's .nuspec from the
    /// sources configured in the user's nuget.config (honoring <c>&lt;packageSourceMapping&gt;</c>).
    /// Returns a dictionary mapping each dependency package ID to its normalized minimum version.
    /// </summary>
    Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the NuGet global packages directory, resolved from the user's nuget.config — the
    /// NUGET_PACKAGES environment variable or the <c>globalPackagesFolder</c> setting — falling back to
    /// ~/.nuget/packages/.
    /// </summary>
    DirectoryInfo GetNuGetGlobalPackagesDir();

    /// <summary>
    /// Returns the directory for a specific package version in the NuGet global packages cache, using
    /// NuGet's normalized on-disk layout: {cache}/{lowercase-id}/{normalized-version}/.
    /// </summary>
    DirectoryInfo GetNuGetPackageDir(string packageName, string version);
}
