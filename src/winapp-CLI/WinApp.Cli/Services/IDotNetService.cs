// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for detecting and working with .NET projects
/// </summary>
internal interface IDotNetService
{
    /// <summary>
    /// Finds all .csproj files in the specified directory (non-recursive)
    /// </summary>
    /// <returns>A list of .csproj files found, empty if none</returns>
    IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory);

    /// <summary>
    /// Gets the TargetFramework value from a .csproj file.
    /// If the project uses <TargetFrameworks> (plural/multi-targeting), returns the first TFM.
    /// </summary>
    string? GetTargetFramework(FileInfo csprojPath);

    /// <summary>
    /// Checks whether a .csproj file uses <TargetFrameworks> (plural) for multi-targeting.
    /// </summary>
    bool IsMultiTargeted(FileInfo csprojPath);

    /// <summary>
    /// Checks whether the TargetFramework includes a Windows TFM that supports WinAppSDK
    /// (e.g. net8.0-windows10.0.19041.0 or later)
    /// </summary>
    bool IsTargetFrameworkSupported(string targetFramework);

    /// <summary>
    /// Returns the recommended TargetFramework for WinAppSDK projects.
    /// If <paramref name="currentTargetFramework"/> is provided and has a supported .NET version,
    /// it preserves that version and only adds/updates the Windows SDK version.
    /// </summary>
    /// <param name="currentTargetFramework">The current TargetFramework from the project, or null if not set.</param>
    string GetRecommendedTargetFramework(string? currentTargetFramework = null);

    /// <summary>
    /// Updates the TargetFramework in a .csproj file
    /// </summary>
    void SetTargetFramework(FileInfo csprojPath, string newTargetFramework);

    /// <summary>
    /// Adds or updates a NuGet PackageReference using the dotnet CLI.
    /// </summary>
    /// <param name="csprojPath">The project file in which to add or update the package reference.</param>
    /// <param name="packageName">The name of the NuGet package to add or update.</param>
    /// <param name="version">
    /// The specific package version to install. When <see langword="null"/>, the dotnet CLI is invoked
    /// with the <c>--prerelease</c> flag, allowing the latest prerelease version to be selected.
    /// </param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The version that was added or updated.</returns>
    Task<string> AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an arbitrary dotnet CLI command in the given working directory.
    /// </summary>
    Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the .csproj has a RuntimeIdentifier element with a default that auto-detects
    /// the current platform architecture. Only adds the element if no RuntimeIdentifier or
    /// RuntimeIdentifiers element already exists in the project.
    /// </summary>
    /// <returns>True if the .csproj was modified, false if it already had a RuntimeIdentifier.</returns>
    Task<bool> EnsureRuntimeIdentifierAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the PublishProfile element in a .csproj to include a Condition that checks
    /// whether the publish profile file actually exists, preventing build errors when it doesn't.
    /// Transforms: &ltPublishProfile&gt;win-$(Platform).pubxml&lt;/PublishProfile&gt;
    /// To: &lt;PublishProfile Condition="Exists('Properties\PublishProfiles\win-$(Platform).pubxml')"&gt;win-$(Platform).pubxml&lt;/PublishProfile&gt;
    /// </summary>
    /// <returns>True if the .csproj was modified, false if no matching PublishProfile element was found.</returns>
    Task<bool> UpdatePublishProfileAsync(FileInfo csprojPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a .csproj file contains a PackageReference for the specified package
    /// by querying the dotnet CLI package list.
    /// </summary>
    Task<bool> HasPackageReferenceAsync(FileInfo csprojPath, string packageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs `dotnet list package --format json` and returns the parsed result.
    /// </summary>
    /// <param name="csprojFile">The .csproj file to query.</param>
    /// <param name="includeTransitive">When true, includes transitive package references in the output.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo csprojFile, bool includeTransitive = true, CancellationToken cancellationToken = default);
}
