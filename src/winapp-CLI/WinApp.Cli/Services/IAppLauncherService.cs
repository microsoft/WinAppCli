// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Provides methods for launching packaged Windows applications and computing
/// MSIX package identity values.
/// </summary>
internal interface IAppLauncherService
{
    /// <summary>
    /// Launches a packaged application by its Application User Model ID (AUMID).
    /// </summary>
    /// <param name="aumid">The Application User Model ID (e.g. <c>PackageFamilyName!App</c>).</param>
    /// <param name="arguments">Optional command-line arguments to pass to the application.</param>
    /// <returns>The process ID of the launched application.</returns>
    uint LaunchByAumid(string aumid, string? arguments = null);

    /// <summary>
    /// Terminates all processes belonging to a packaged application using
    /// <c>IPackageDebugSettings.TerminateAllProcesses</c>. Falls back to killing a
    /// single process by PID when the package-level termination fails.
    /// </summary>
    /// <param name="packageFullName">The full name of the package whose processes should be terminated, or <c>null</c> to skip package-level termination.</param>
    /// <param name="processId">Fallback process ID to kill if package-level termination fails or <paramref name="packageFullName"/> is <c>null</c>.</param>
    void TerminatePackageProcesses(string? packageFullName, uint processId);

    /// <summary>
    /// Computes the package family name from a package name and publisher distinguished name.
    /// The result follows the Windows format: <c>{packageName}_{publisherId}</c>, where the
    /// publisher ID is a 13-character Crockford Base32 encoding derived from the publisher's SHA256 hash.
    /// </summary>
    /// <param name="packageName">The MSIX package name (e.g. <c>MyCompany.MyApp</c>).</param>
    /// <param name="publisher">The publisher distinguished name (e.g. <c>CN=MyCompany</c>).</param>
    /// <returns>The computed package family name.</returns>
    string ComputePackageFamilyName(string packageName, string publisher);

    /// <summary>
    /// Resolves the package full name from a package family name by querying
    /// the system's package inventory.
    /// </summary>
    /// <param name="packageFamilyName">The package family name (e.g. <c>MyApp_abc123def</c>).</param>
    /// <returns>The package full name, or <c>null</c> if the package is not installed.</returns>
    string? GetPackageFullName(string packageFamilyName);
}
