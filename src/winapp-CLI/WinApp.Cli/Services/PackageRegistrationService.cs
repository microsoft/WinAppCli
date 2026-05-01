// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using Windows.Management.Deployment;

namespace WinApp.Cli.Services;

/// <summary>
/// Manages MSIX package registration, unregistration, and installation using
/// the Windows <see cref="PackageManager"/> WinRT API directly, without
/// shelling out to PowerShell.
/// </summary>
internal sealed class PackageRegistrationService(ILogger<PackageRegistrationService> logger) : IPackageRegistrationService
{
    // HRESULT 0x800704EC = ERROR_ACCESS_DISABLED_BY_POLICY (group policy blocks sideloading)
    // Note: 0x80073CFB ("Reinstallation of the package was blocked") is intentionally NOT
    // treated as a developer-mode error — it more commonly indicates a conflicting installed
    // package (e.g., a previously installed signed MSIX with the same identity).
    private const int ERROR_ACCESS_DISABLED_BY_POLICY = unchecked((int)0x800704EC);

    /// <inheritdoc />
    public async Task RegisterLooseLayoutAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        LongPathHelper.ValidatePathLength(fullPath);

        // WinRT PackageManager doesn't support paths exceeding MAX_PATH or symlinks.
        // Convert to 8.3 short path as a workaround; throw if shortening fails.
        var manifestUri = new Uri(LongPathHelper.GetShortPathOrThrow(fullPath));
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
                throw BuildRegistrationException(
                    "Failed to register package",
                    result.ErrorText,
                    result.ExtendedErrorCode?.HResult,
                    packageIdentityName: TryReadIdentityName(fullPath));
            }

            logger.LogDebug("Package registered from loose layout: {ManifestPath}", manifestPath);
        }
        catch (Exception ex) when (IsSideloadPolicyError(ex))
        {
            throw new InvalidOperationException(
                "Sideloading is blocked by Group Policy on this machine. " +
                "Contact your IT administrator to allow trusted app sideloading.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw BuildRegistrationException(
                "Failed to register package",
                ex.Message,
                ex.HResult,
                packageIdentityName: TryReadIdentityName(fullPath),
                inner: ex);
        }
    }

    /// <inheritdoc />
    public async Task RegisterSparseAsync(string manifestPath, string externalLocation, CancellationToken cancellationToken = default)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        LongPathHelper.ValidatePathLength(fullManifestPath);
        var fullExternalPath = Path.GetFullPath(externalLocation);
        LongPathHelper.ValidatePathLength(fullExternalPath);

        // WinRT PackageManager doesn't support paths exceeding MAX_PATH or symlinks.
        // Convert to 8.3 short paths as a workaround; throw if shortening fails.
        var manifestUri = new Uri(LongPathHelper.GetShortPathOrThrow(fullManifestPath));
        var shortExternalPath = LongPathHelper.GetShortPathOrThrow(fullExternalPath + Path.DirectorySeparatorChar);
        if (!Path.EndsInDirectorySeparator(shortExternalPath))
        {
            shortExternalPath += Path.DirectorySeparatorChar;
        }

        var externalUri = new Uri(shortExternalPath);
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
                throw BuildRegistrationException(
                    "Failed to register sparse package",
                    result.ErrorText,
                    result.ExtendedErrorCode?.HResult,
                    packageIdentityName: TryReadIdentityName(fullManifestPath));
            }

            logger.LogDebug("Sparse package registered: {ManifestPath} (external: {ExternalLocation})", manifestPath, externalLocation);
        }
        catch (Exception ex) when (IsSideloadPolicyError(ex))
        {
            throw new InvalidOperationException(
                "Sideloading is blocked by Group Policy on this machine. " +
                "Contact your IT administrator to allow trusted app sideloading.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw BuildRegistrationException(
                "Failed to register sparse package",
                ex.Message,
                ex.HResult,
                packageIdentityName: TryReadIdentityName(fullManifestPath),
                inner: ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnregisterAsync(string packageName, bool preserveAppData = true, CancellationToken cancellationToken = default)
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
            logger.LogDebug("Removing package: {PackageFullName} (preserveAppData={PreserveAppData})", fullName, preserveAppData);

            var removalOptions = preserveAppData
                ? RemovalOptions.PreserveApplicationData
                : RemovalOptions.None;

            var result = await pm.RemovePackageAsync(fullName, removalOptions).AsTask(cancellationToken);

            if (!string.IsNullOrEmpty(result.ErrorText))
            {
                logger.LogWarning("Warning removing package {PackageFullName}: {Error}", fullName, result.ErrorText);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task UnregisterByFullNameAsync(string packageFullName, bool preserveAppData = true, CancellationToken cancellationToken = default)
    {
        var pm = new PackageManager();

        logger.LogDebug("Removing package: {PackageFullName} (preserveAppData={PreserveAppData})", packageFullName, preserveAppData);

        var removalOptions = preserveAppData
            ? RemovalOptions.PreserveApplicationData
            : RemovalOptions.None;

        var result = await pm.RemovePackageAsync(packageFullName, removalOptions).AsTask(cancellationToken);

        if (!string.IsNullOrEmpty(result.ErrorText))
        {
            logger.LogWarning("Warning removing package {PackageFullName}: {Error}", packageFullName, result.ErrorText);
        }
    }

    /// <inheritdoc />
    public async Task InstallPackageAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(packagePath);
        LongPathHelper.ValidatePathLength(fullPath);

        // WinRT PackageManager doesn't support paths exceeding MAX_PATH or symlinks.
        // Convert to 8.3 short path as a workaround; throw if shortening fails.
        var packageUri = new Uri(LongPathHelper.GetShortPathOrThrow(fullPath));
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

    internal static bool IsSideloadPolicyError(Exception ex)
    {
        return ex.HResult == ERROR_ACCESS_DISABLED_BY_POLICY;
    }

    // HRESULT 0x80073CFB — most commonly raised when a package with the same identity is
    // already installed (e.g., via a signed MSIX) and cannot be re-registered as dev-mode
    // loose files. Officially: "Reinstallation of the package was blocked."
    internal const int ERROR_INSTALL_PACKAGE_ALREADY_EXISTS = unchecked((int)0x80073CFB);

    /// <summary>
    /// Builds a user-facing exception describing a failed package registration.
    /// When the HRESULT indicates a duplicate-identity conflict (0x80073CFB) and a
    /// <paramref name="packageIdentityName"/> is supplied, the hint embeds the actual
    /// identity so the user can copy/paste the remediation command directly.
    /// </summary>
    internal static InvalidOperationException BuildRegistrationException(
        string prefix,
        string? errorText,
        int? hresult,
        string? packageIdentityName = null,
        Exception? inner = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(prefix).Append(": ");
        sb.Append(string.IsNullOrEmpty(errorText) ? "Unknown error" : errorText);
        if (hresult.HasValue)
        {
            sb.Append(" (0x").Append(hresult.Value.ToString("X8")).Append(')');
        }

        if (hresult == ERROR_INSTALL_PACKAGE_ALREADY_EXISTS)
        {
            var identityToken = string.IsNullOrWhiteSpace(packageIdentityName)
                ? "<PackageName>"
                : packageIdentityName;

            sb.AppendLine();
            sb.Append(
                "Hint: a package with the same identity may already be installed " +
                "(e.g., from a signed MSIX). Try removing it first:");
            sb.AppendLine();
            sb.Append("  Get-AppxPackage ").Append(identityToken).Append(" | Remove-AppxPackage");
        }

        return inner is null
            ? new InvalidOperationException(sb.ToString())
            : new InvalidOperationException(sb.ToString(), inner);
    }

    /// <summary>
    /// Best-effort read of the AppxManifest Identity/@Name attribute. Returns null on any
    /// I/O or parse failure; callers should treat null as "identity unknown" and fall back
    /// to a generic placeholder.
    /// </summary>
    private static string? TryReadIdentityName(string manifestPath)
    {
        try
        {
            return AppxManifestDocument.Load(manifestPath).IdentityName;
        }
        catch
        {
            return null;
        }
    }
}
