// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Management.Deployment;

namespace WinApp.Cli.Services;

/// <summary>
/// Manages MSIX package registration, unregistration, and installation using
/// the Windows <see cref="PackageManager"/> WinRT API directly, without
/// shelling out to PowerShell.
/// </summary>
internal sealed class PackageRegistrationService(ILogger<PackageRegistrationService> logger) : IPackageRegistrationService
{
    // HRESULT 0x80073CFB = ERROR_PACKAGE_NOT_REGISTERED_FOR_SIDELOAD (developer mode not enabled)
    // HRESULT 0x800704EC = ERROR_ACCESS_DISABLED_BY_POLICY (group policy blocks sideloading)
    private const int ERROR_PACKAGE_NOT_REGISTERED_FOR_SIDELOAD = unchecked((int)0x80073CFB);
    private const int ERROR_ACCESS_DISABLED_BY_POLICY = unchecked((int)0x800704EC);

    /// <inheritdoc />
    public async Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var manifestUri = new Uri(Path.GetFullPath(manifestPath));
        var pm = new PackageManager();

        try
        {
            var result = await pm.RegisterPackageAsync(
                manifestUri,
                null,
                DeploymentOptions.DevelopmentMode | DeploymentOptions.ForceApplicationShutdown
            ).AsTask(cancellationToken);

            if (!result.IsRegistered)
            {
                var errorText = result.ErrorText ?? "Unknown error";
                throw new InvalidOperationException(
                    $"Failed to register package: {errorText} (0x{result.ExtendedErrorCode?.HResult:X8})");
            }

            logger.LogDebug("Package registered from loose layout: {ManifestPath}", manifestPath);
        }
        catch (Exception ex) when (IsDeveloperModeError(ex))
        {
            throw new InvalidOperationException(
                "Windows Developer Mode is required to register MSIX packages from loose files. " +
                "Open Settings > System > For Developers and enable Developer Mode.", ex);
        }
    }

    /// <inheritdoc />
    public async Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default)
    {
        var manifestUri = new Uri(Path.GetFullPath(manifestPath));
        var externalUri = new Uri(Path.GetFullPath(externalLocation) + Path.DirectorySeparatorChar);
        var pm = new PackageManager();

        try
        {
            var options = new RegisterPackageOptions
            {
                ExternalLocationUri = externalUri,
                DeveloperMode = true,
                ForceUpdateFromAnyVersion = true,
            };

            var result = await pm.RegisterPackageByUriAsync(
                manifestUri,
                options
            ).AsTask(cancellationToken);

            if (!result.IsRegistered)
            {
                var errorText = result.ErrorText ?? "Unknown error";
                throw new InvalidOperationException(
                    $"Failed to register sparse package: {errorText} (0x{result.ExtendedErrorCode?.HResult:X8})");
            }

            logger.LogDebug("Sparse package registered: {ManifestPath} (external: {ExternalLocation})", manifestPath, externalLocation);
        }
        catch (Exception ex) when (IsDeveloperModeError(ex))
        {
            throw new InvalidOperationException(
                "Windows Developer Mode is required to register MSIX packages from loose files. " +
                "Open Settings > System > For Developers and enable Developer Mode.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterAsync(string packageName, CancellationToken cancellationToken = default)
    {
        var pm = new PackageManager();

        // FindPackagesForUser with name+publisher requires both to match.
        // Use the single-string overload to find by family name prefix, then filter by name.
        var allUserPackages = pm.FindPackagesForUser(string.Empty);
        var matchingPackages = allUserPackages
            .Where(p => string.Equals(p.Id.Name, packageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingPackages.Count == 0)
        {
            return false;
        }

        foreach (var pkg in matchingPackages)
        {
            var fullName = pkg.Id.FullName;
            logger.LogDebug("Removing package: {PackageFullName}", fullName);

            var result = await pm.RemovePackageAsync(fullName).AsTask(cancellationToken);

            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                logger.LogWarning("Warning removing package {PackageFullName}: {Error}", fullName, result.ErrorText);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var packageUri = new Uri(Path.GetFullPath(packagePath));
        var pm = new PackageManager();

        var result = await pm.AddPackageAsync(
            packageUri,
            null,
            DeploymentOptions.ForceApplicationShutdown
        ).AsTask(cancellationToken);

        if (!string.IsNullOrEmpty(result.ErrorText))
        {
            throw new InvalidOperationException(
                $"Failed to install package '{Path.GetFileName(packagePath)}': {result.ErrorText} (0x{result.ExtendedErrorCode?.HResult:X8})");
        }

        logger.LogDebug("Installed package: {PackagePath}", packagePath);
    }

    /// <inheritdoc />
    public string? GetInstalledVersion(string packageName)
    {
        var pm = new PackageManager();
        // Use the single-parameter overload and filter manually.
        // The (userId, name, publisher) overload rejects empty/null publisher
        // because string.Empty marshals as null HSTRING in WinRT interop.
        var allUserPackages = pm.FindPackagesForUser(string.Empty);

        foreach (var pkg in allUserPackages)
        {
            if (string.Equals(pkg.Id.Name, packageName, StringComparison.OrdinalIgnoreCase))
            {
                var v = pkg.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
        }

        return null;
    }

    /// <inheritdoc />
    public List<DevPackageInfo> FindDevPackages(string packageName)
    {
        var pm = new PackageManager();
        var allUserPackages = pm.FindPackagesForUser(string.Empty);
        var results = new List<DevPackageInfo>();

        foreach (var pkg in allUserPackages)
        {
            if (!string.Equals(pkg.Id.Name, packageName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? installLocation = null;
            try
            {
                installLocation = pkg.InstalledLocation?.Path;
            }
            catch
            {
                // InstalledLocation can throw if the path no longer exists
            }

            var v = pkg.Id.Version;
            results.Add(new DevPackageInfo(
                FullName: pkg.Id.FullName,
                Name: pkg.Id.Name,
                Version: $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                InstallLocation: installLocation,
                IsDevelopmentMode: pkg.IsDevelopmentMode));
        }

        return results;
    }

    private static bool IsDeveloperModeError(Exception ex)
    {
        return ex.HResult == ERROR_PACKAGE_NOT_REGISTERED_FOR_SIDELOAD
            || ex.HResult == ERROR_ACCESS_DISABLED_BY_POLICY;
    }
}
