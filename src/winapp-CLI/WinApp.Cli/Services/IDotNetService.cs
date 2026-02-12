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
    Task AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an arbitrary dotnet CLI command in the given working directory.
    /// </summary>
    Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs `dotnet list package --include-transitive --format json` and returns the parsed result.
    /// </summary>
    Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo csprojFile, CancellationToken cancellationToken = default);
}
