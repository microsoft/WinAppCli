// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Provides methods for registering, unregistering, and querying MSIX packages
/// using the Windows PackageManager API.
/// </summary>
internal interface IPackageRegistrationService
{
    /// <summary>
    /// Registers a loose-layout MSIX package from an AppxManifest.xml path.
    /// Uses DevelopmentMode to allow registration without signing.
    /// </summary>
    /// <param name="manifestPath">Path to the AppxManifest.xml in the loose layout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a sparse MSIX package with an external location.
    /// The package references files at the external location rather than containing them.
    /// </summary>
    /// <param name="manifestPath">Path to the AppxManifest.xml file.</param>
    /// <param name="externalLocation">External directory containing the app files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unregisters an installed package by name. Returns true if a package was found and removed.
    /// </summary>
    /// <param name="packageName">The package identity name (e.g. <c>MyCompany.MyApp</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if a package was unregistered, false if no matching package was found.</returns>
    Task<bool> UnregisterAsync(string packageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs an MSIX/APPX package file, optionally forcing application shutdown.
    /// Used for installing framework dependencies like Windows App Runtime.
    /// </summary>
    /// <param name="packagePath">Path to the .msix or .appx file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a package with the given name is installed and returns its version,
    /// or null if not found.
    /// </summary>
    /// <param name="packageName">The package identity name.</param>
    /// <returns>The installed version, or null if not found.</returns>
    string? GetInstalledVersion(string packageName);

    /// <summary>
    /// Finds all installed packages matching the given name that were registered in
    /// development mode (sideloaded). Returns package metadata including the full name
    /// and install location for safety checks.
    /// </summary>
    /// <param name="packageName">The package identity name to search for.</param>
    /// <returns>A list of matching dev-mode packages.</returns>
    List<DevPackageInfo> FindDevPackages(string packageName);
}

/// <summary>
/// Information about a development-mode registered package.
/// </summary>
internal sealed record DevPackageInfo(
    string FullName,
    string Name,
    string Version,
    string? InstallLocation,
    bool IsDevelopmentMode);
