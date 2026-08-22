// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Services;

/// <summary>
/// Locates, installs, and gates the framework-dependent Windows App Runtime (Framework + DDLM) MSIX
/// packages an unpackaged WinUI app needs at startup. Extracted from <see cref="WorkspaceSetupService"/>
/// so this runtime-install responsibility has its own home (pr-review M9).
/// </summary>
internal interface IWindowsAppRuntimeService
{
    /// <summary>
    /// Finds the MSIX directory for Windows App SDK runtime packages in the NuGet global cache.
    /// </summary>
    /// <param name="usedVersions">Optional dictionary of package versions to look for specific installed packages.</param>
    /// <param name="requireExactVersion">
    /// When <c>true</c>, only the exact package/version directories in <paramref name="usedVersions"/> are
    /// accepted; the general "any cached runtime" scan is skipped so an unrelated cached runtime is never
    /// returned. Project-mode unpackaged launches pass <c>true</c> so the gate identities describe the
    /// runtime the app was actually built against (returns <c>null</c> when the exact version is absent).
    /// Legacy/packaged callers keep the default (<c>false</c>) tolerant scan.
    /// </param>
    /// <returns>The path to the MSIX directory, or null if not found.</returns>
    DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null, bool requireExactVersion = false);

    /// <summary>
    /// Reads the versioned runtime package identities from an exact runtime MSIX payload without
    /// installing anything. Used by preflight callers that must compare the requested runtime with
    /// the current user's registrations before deciding whether installation is necessary.
    /// </summary>
    Task<IReadOnlyList<(string Name, string Version)>> GetWindowsAppRuntimePackagesAsync(
        DirectoryInfo msixDir,
        TaskContext taskContext,
        CancellationToken cancellationToken,
        string? architecture = null);

    /// <summary>
    /// Installs the Windows App Runtime framework MSIX packages (Framework / DDLM / Singleton / Main)
    /// from the given runtime MSIX directory.
    /// </summary>
    /// <param name="msixDir">The runtime MSIX directory (from the NuGet cache).</param>
    /// <param name="taskContext">Status/debug sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="architecture">
    /// Optional target architecture (<c>x64</c> / <c>arm64</c> / <c>x86</c>). When <c>null</c> (folder mode
    /// and legacy callers) the current process architecture is used, preserving existing behavior.
    /// Project mode passes the app's resolved <c>--arch</c> so an unpackaged app of a different arch gets
    /// the matching Framework/DDLM.
    /// </param>
    /// <returns>
    /// Install/error counts plus the versioned Framework/DDLM package identities (name + version)
    /// discovered in the inventory, so the caller can gate on the SPECIFIC runtime the app needs at the
    /// required version.
    /// </returns>
    Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null);

    /// <summary>
    /// Returns <c>true</c> when a framework-dependent Windows App Runtime (a versioned Framework package
    /// plus its matching-arch DDLM) is registered for the current user for <paramref name="architecture"/>.
    /// Mirrors the presence check an unpackaged WinUI app's bootstrapper performs so callers can gate the
    /// launch rather than starting an app that would fail to resolve its runtime.
    /// </summary>
    /// <param name="architecture">Target architecture (<c>x64</c> / <c>arm64</c> / <c>x86</c>); <c>null</c> uses the current process architecture.</param>
    /// <param name="expectedRuntimePackages">
    /// Optional versioned Framework/DDLM identities (name + version, from <see cref="InstallWindowsAppRuntimeAsync"/>)
    /// the app was built against. When supplied, the app-facing Framework family must be registered for the
    /// arch at a version &gt;= the required one — closing the false-pass where a different WinAppSDK version,
    /// or a stale older patch of the same Framework family, is registered but the required version silently
    /// failed to install. DDLM identities are not exact-matched (their names embed the full
    /// version and install side-by-side); the generic DDLM presence check covers them. When
    /// null/empty (folder mode / legacy callers) only the generic presence check runs.
    /// </param>
    bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<(string Name, string Version)>? expectedRuntimePackages = null);
}
