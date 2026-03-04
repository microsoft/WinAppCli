// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Represents a WinRT component discovered in a NuGet package:
/// a .winmd metadata file paired with its native implementation DLL.
/// </summary>
internal sealed record WinRTComponent(FileInfo WinmdPath, string ImplementationDll);

/// <summary>
/// Service for reading .winmd (Windows Metadata) files and discovering
/// WinRT activatable classes for manifest generation.
/// </summary>
internal interface IWinmdService
{
    /// <summary>
    /// Reads a .winmd file and returns the fully-qualified names of all
    /// activatable runtime classes declared within it.
    /// </summary>
    IReadOnlyList<string> GetActivatableClasses(FileInfo winmdPath);

    /// <summary>
    /// Discovers WinRT components in NuGet packages by finding .winmd files
    /// that have a matching native implementation DLL in runtimes/win-{arch}/native/.
    /// </summary>
    /// <param name="nugetCacheDir">The NuGet global packages directory.</param>
    /// <param name="packages">Dictionary of package name → version to scan.</param>
    /// <param name="architecture">Target architecture (e.g., "x64", "arm64").</param>
    /// <param name="excludePackageNames">Package names to skip (already handled by other mechanisms).</param>
    IReadOnlyList<WinRTComponent> DiscoverWinRTComponents(
        DirectoryInfo nugetCacheDir,
        Dictionary<string, string> packages,
        string architecture,
        IReadOnlySet<string>? excludePackageNames = null);
}
