// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Locates, installs, and gates the framework-dependent Windows App Runtime (Framework + DDLM) MSIX
/// packages an unpackaged WinUI app needs at startup. Extracted from <see cref="WorkspaceSetupService"/>
/// so this runtime-install responsibility has its own home (pr-review M9).
/// </summary>
internal class WindowsAppRuntimeService(
    IPackageRegistrationService packageRegistrationService,
    INugetService nugetService) : IWindowsAppRuntimeService
{
    /// <summary>
    /// Package entry information from MSIX inventory
    /// </summary>
    public class MsixPackageEntry
    {
        public required string FileName { get; set; }
        public required string PackageIdentity { get; set; }
    }

    /// <summary>
    /// Parses the MSIX inventory file and returns package entries (shared implementation)
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="architecture">
    /// Target architecture whose <c>win10-{arch}</c> inventory is read. When <c>null</c>, defaults to the
    /// CLI's process architecture (folder mode / legacy callers) — preserving the original behavior.
    /// Project mode passes the app's resolved arch so a cross-arch inventory is read correctly.
    /// </param>
    /// <returns>List of package entries, or null if not found</returns>
    public static async Task<List<MsixPackageEntry>?> ParseMsixInventoryAsync(TaskContext taskContext, DirectoryInfo msixDir, CancellationToken cancellationToken, string? architecture = null)
    {
        architecture = RunArchHelper.NormalizeArchitecture(architecture) ?? RunArchHelper.DefaultArchitecture();

        taskContext.AddDebugMessage($"{UiSymbols.Note} Using architecture for MSIX inventory: {architecture}");

        // Look for MSIX packages for the current architecture
        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");
        if (!Directory.Exists(msixArchDir))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No MSIX packages found for architecture {architecture}");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available directories: {string.Join(", ", msixDir.GetDirectories().Select(d => d.Name))}");
            return null;
        }

        // Read the MSIX inventory file
        var inventoryPath = Path.Combine(msixArchDir, "msix.inventory");
        if (!File.Exists(inventoryPath))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No msix.inventory file found in {msixArchDir}");
            return null;
        }

        var inventoryLines = await File.ReadAllLinesAsync(inventoryPath, cancellationToken);
        var packageEntries = inventoryLines
            .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => new MsixPackageEntry { FileName = parts[0].Trim(), PackageIdentity = parts[1].Trim() })
            .ToList();

        if (packageEntries.Count == 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No valid package entries found in msix.inventory");
            return null;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Found {packageEntries.Count} MSIX packages in inventory");

        return packageEntries;
    }

    /// <summary>
    /// Reads the actual package Name and Version from the AppxManifest.xml inside an MSIX file.
    /// The MSIX inventory file can have incorrect package names (e.g., the DDLM), so we read
    /// the real identity directly from the package to ensure correct installation checks.
    /// </summary>
    private static (string? Name, string? Version) ReadMsixIdentity(string msixFilePath, TaskContext taskContext)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(msixFilePath);
            var manifestEntry = zip.GetEntry("AppxManifest.xml");
            if (manifestEntry == null)
            {
                return (null, null);
            }

            using var stream = manifestEntry.Open();
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var identityElement = doc.Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Identity");

            var name = identityElement?.Attribute("Name")?.Value;
            var version = identityElement?.Attribute("Version")?.Value;
            return (name, version);
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not read identity from {Path.GetFileName(msixFilePath)}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Installs Windows App SDK runtime MSIX packages for the current system architecture
    /// </summary>
    /// <param name="msixDir">Directory containing the MSIX packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null)
    {
        // Directory/inventory arch: needs a concrete value to locate win10-{arch}. Default to the CLI's
        // process arch (folder mode / legacy callers — byte-identical to the previous behavior). Project
        // mode passes the app's resolved --arch so the correct-arch inventory/packages are used.
        var dirArch = RunArchHelper.NormalizeArchitecture(architecture) ?? RunArchHelper.DefaultArchitecture();

        // Install-skip filter arch: preserved as-is (nullable). In folder mode this stays null so the
        // "already installed?" check is arch-agnostic exactly as before (spec L2 — folder mode is
        // byte-for-byte identical). Only project mode (explicit arch) filters by target arch so a
        // cross-arch runtime isn't wrongly skipped because a same-name host-arch package is present.
        var filterArch = RunArchHelper.NormalizeArchitecture(architecture);

        // Get package entries from MSIX inventory
        var packageEntries = await ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken, dirArch);
        if (packageEntries == null || packageEntries.Count == 0)
        {
            return (0, 0, Array.Empty<(string, string)>());
        }

        var msixArchDir = Path.Join(msixDir.FullName, $"win10-{dirArch}");

        // Build list of packages to evaluate
        var packagesToCheck = new List<(string FilePath, string PackageName, string NewVersion, string FileName)>();
        foreach (var entry in packageEntries)
        {
            var msixFilePath = Path.Combine(msixArchDir, entry.FileName);
            if (!File.Exists(msixFilePath))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSIX file not found: {msixFilePath}");
                continue;
            }

            // Read the actual package identity from the MSIX's AppxManifest.xml.
            // The inventory file's PackageIdentity can differ from the real installed name.
            var (packageName, newVersionString) = ReadMsixIdentity(msixFilePath, taskContext);
            if (packageName == null)
            {
                // Fallback: parse from inventory identity string
                var identityParts = entry.PackageIdentity.Split('_');
                packageName = identityParts[0];
                newVersionString = identityParts.Length >= 2 ? identityParts[1] : "";
            }

            packagesToCheck.Add((msixFilePath, packageName, newVersionString ?? "", entry.FileName));
        }

        if (packagesToCheck.Count == 0)
        {
            return (0, 0, Array.Empty<(string, string)>());
        }

        taskContext.AddDebugMessage($"{UiSymbols.Info} Checking and installing {packagesToCheck.Count} MSIX packages");

        var installedCount = 0;
        var errorCount = 0;

        foreach (var (filePath, packageName, newVersion, fileName) in packagesToCheck)
        {
            // Check if already installed with same or newer version. The arch filter is applied only
            // in project mode (filterArch non-null); in folder mode it's null → arch-agnostic match,
            // byte-identical to the previous behavior (spec L2).
            var installedVersion = packageRegistrationService.GetInstalledVersion(packageName, filterArch);
            if (installedVersion != null)
            {
                if (Version.TryParse(installedVersion, out var existing) &&
                    Version.TryParse(newVersion, out var incoming) &&
                    existing >= incoming)
                {
                    taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: Already installed or newer version exists");
                    continue;
                }
            }

            taskContext.AddDebugMessage($"{UiSymbols.Info} {fileName}: Will install");

            try
            {
                await packageRegistrationService.InstallPackageAsync(filePath, cancellationToken);
                installedCount++;
                taskContext.AddDebugMessage($"{UiSymbols.Check} {fileName}: Installation successful");
            }
            catch (Exception ex)
            {
                errorCount++;
                taskContext.AddDebugMessage($"{UiSymbols.Note} {fileName}: {ex.Message}");
            }
        }

        // Provide summary feedback
        if (installedCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Check} Installed {installedCount} MSIX packages");
        }
        if (errorCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} {errorCount} packages failed to install");
        }

        // Surface the versioned Framework + DDLM identities from this inventory so the caller can gate
        // on the SPECIFIC runtime the app was built against (spec R2-M1), rather than accepting any
        // registered WinAppSDK version for the arch. The version is carried alongside the name so the
        // gate can reject a stale OLDER patch of the same Framework family (spec R2-M1 residual).
        var runtimePackages = packagesToCheck
            .Where(p => IsRuntimeGatePackageName(p.PackageName))
            .Select(p => (Name: p.PackageName, Version: p.NewVersion))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (installedCount, errorCount, runtimePackages);
    }

    /// <summary>
    /// Package-name prefixes that identify a framework-dependent Windows App Runtime registration.
    /// The bootstrapper an unpackaged WinUI app runs at startup resolves a versioned Framework package
    /// plus its matching-arch DDLM; both must be present for the app to boot.
    /// </summary>
    private const string WinAppRuntimeFrameworkPrefix = "Microsoft.WindowsAppRuntime.";
    private const string WinAppRuntimeDdlmPrefix = "Microsoft.WinAppRuntime.DDLM.";

    // The Component Store (CBS) package shares the Framework prefix but is a system singleton, not the
    // app-facing Framework — exclude it so its presence never masks a missing target-arch Framework.
    // Internal so a test can assert this infix actually discriminates a CBS name from a Framework name.
    internal const string WinAppRuntimeCbsInfix = ".CBS.";

    /// <summary>
    /// Classifies a package name as the app-facing versioned Framework (whose family name is
    /// <c>Microsoft.WindowsAppRuntime.{major.minor}</c>, excluding the CBS system component). This is the
    /// identity the gate exact-matches and version-compares, since the Framework's version lives in its
    /// package Version (not its name).
    /// </summary>
    private static bool IsFrameworkGatePackageName(string packageName) =>
        packageName.StartsWith(WinAppRuntimeFrameworkPrefix, StringComparison.OrdinalIgnoreCase)
        && !packageName.Contains(WinAppRuntimeCbsInfix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies a package name as one of the framework-dependent runtime identities the gate cares
    /// about: a versioned Framework (excluding the CBS system component) or a DDLM.
    /// </summary>
    private static bool IsRuntimeGatePackageName(string packageName) =>
        IsFrameworkGatePackageName(packageName)
        || packageName.StartsWith(WinAppRuntimeDdlmPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when a framework-dependent Windows App Runtime is registered for the current
    /// user for <paramref name="architecture"/>: i.e. both a versioned Framework package
    /// (<c>Microsoft.WindowsAppRuntime.{version}</c>, excluding the CBS system component) and its
    /// matching-arch DDLM (<c>Microsoft.WinAppRuntime.DDLM.*</c>) are present. Mirrors the runtime
    /// presence check an unpackaged WinUI app's bootstrapper performs, so callers can gate the launch
    /// instead of starting an app that would crash resolving its runtime.
    /// <para>
    /// When <paramref name="expectedRuntimePackages"/> is supplied (the versioned identities from
    /// the resolved runtime inventory), the app-facing <b>Framework</b> family is additionally required to
    /// be registered for the arch at a version <b>greater than or equal to</b> the required one. This
    /// closes the false-pass where a version-specific install silently failed but a DIFFERENT WinAppSDK
    /// version — or a stale OLDER patch of the same Framework family (whose family name is only
    /// <c>major.minor</c>) — is registered for the arch (common on dev boxes); without it the generic
    /// prefix check would pass and the app would still crash at bootstrap (spec R2-M1). The DDLM identities
    /// are intentionally NOT exact-matched here — their names embed the full version and they install
    /// side-by-side, so the generic DDLM presence check above suffices and demanding the app's exact DDLM
    /// would over-strictly false-fail when a newer compatible DDLM is present (spec R4-L1). When empty or
    /// null (folder mode / legacy callers), only the generic presence check runs — byte-identical to the
    /// previous behavior.
    /// </para>
    /// </summary>
    public bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<(string Name, string Version)>? expectedRuntimePackages = null)
    {
        var arch = RunArchHelper.NormalizeArchitecture(architecture) ?? RunArchHelper.DefaultArchitecture();

        var hasFramework = packageRegistrationService.IsPackageInstalled(
            WinAppRuntimeFrameworkPrefix, arch, excludeNameSubstring: WinAppRuntimeCbsInfix);
        var hasDdlm = packageRegistrationService.IsPackageInstalled(WinAppRuntimeDdlmPrefix, arch);

        if (!hasFramework || !hasDdlm)
        {
            return false;
        }

        if (expectedRuntimePackages is { Count: > 0 })
        {
            foreach (var (name, requiredVersion) in expectedRuntimePackages)
            {
                // Only the app-facing Framework family gets an exact-identity + version check. The DDLM is
                // deliberately NOT exact-matched here — it's already covered by the generic hasDdlm presence
                // check above. DDLM package names embed the FULL version (e.g.
                // Microsoft.WinAppRuntime.DDLM.8000.806.2252.0-x64) and install side-by-side, so demanding
                // the app's EXACT DDLM would over-strictly false-FAIL a launch when a newer compatible DDLM
                // is registered but that specific one failed to install (spec R4-L1). The Framework version
                // compare below is the authoritative patch-level guard; DDLMs track the Framework, so a
                // present DDLM plus the correct Framework version is sufficient.
                if (!IsFrameworkGatePackageName(name))
                {
                    continue;
                }

                // Require the SPECIFIC Framework family the app was built against to be registered for the
                // arch. GetInstalledVersion is an exact-name match, so a wrong minor (e.g. need 1.8, have
                // 1.6) returns null here and fails the gate.
                var installedVersion = packageRegistrationService.GetInstalledVersion(name, arch);
                if (installedVersion is null)
                {
                    return false;
                }

                // Patch-level guard (spec R2-M1 residual): the Framework family name is only major.minor,
                // so a stale OLDER patch of the same minor would satisfy a name-presence check even when
                // the newer patch the app needs failed to install. Reject when both versions parse and the
                // installed one is older. If either is unparseable, fall back to presence (already confirmed
                // above) rather than blocking a launch on an unexpected version string.
                if (Version.TryParse(requiredVersion, out var required) &&
                    Version.TryParse(installedVersion, out var installed) &&
                    installed < required)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the MSIX directory for Windows App SDK runtime packages
    /// </summary>
    /// <param name="usedVersions">Optional dictionary of package versions to look for specific installed packages</param>
    /// <returns>The path to the MSIX directory, or null if not found</returns>
    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null)
    {
        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
        return FindMsixDirectoryInNuGetCache(nugetCacheDir, usedVersions);
    }

    /// <summary>
    /// Searches the NuGet global packages cache (lowercase id/version folder convention).
    /// </summary>
    private static DirectoryInfo? FindMsixDirectoryInNuGetCache(DirectoryInfo nugetCacheDir, Dictionary<string, string>? usedVersions)
    {
        if (usedVersions != null)
        {
            // Try runtime package first (Windows App SDK 1.8+)
            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, out var runtimeVersion))
            {
                var msixDir = TryGetMsixDirectoryFromNuGetCache(nugetCacheDir, BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, runtimeVersion);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }

            // Fallback to main package
            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_PACKAGE, out var mainVersion))
            {
                var msixDir = TryGetMsixDirectoryFromNuGetCache(nugetCacheDir, BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        // General scan: look for any runtime package directories
        var runtimeDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE.ToLowerInvariant()));
        if (runtimeDir.Exists)
        {
            foreach (var versionDir in runtimeDir.GetDirectories().OrderByDescending(d => d.Name, new VersionStringComparer()))
            {
                var msixDir = TryGetMsixDirectoryFromPath(versionDir);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        // Fallback: main package
        var mainDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, BuildToolsService.WINAPP_SDK_PACKAGE.ToLowerInvariant()));
        if (mainDir.Exists)
        {
            foreach (var versionDir in mainDir.GetDirectories().OrderByDescending(d => d.Name, new VersionStringComparer()))
            {
                var msixDir = TryGetMsixDirectoryFromPath(versionDir);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the NuGet cache for a specific package/version (lowercase ID/version layout).
    /// </summary>
    private static DirectoryInfo? TryGetMsixDirectoryFromNuGetCache(DirectoryInfo nugetCacheDir, string packageId, string version)
    {
        // NuGet global cache uses lowercase package IDs
        var pkgVersionDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, packageId.ToLowerInvariant(), version));
        return TryGetMsixDirectoryFromPath(pkgVersionDir);
    }

    /// <summary>
    /// Helper method to check if an MSIX directory exists for a given package path
    /// </summary>
    /// <param name="packagePath">The full path to the package directory</param>
    /// <returns>The MSIX directory path if it exists, null otherwise</returns>
    private static DirectoryInfo? TryGetMsixDirectoryFromPath(DirectoryInfo packagePath)
    {
        var msixDir = new DirectoryInfo(Path.Combine(packagePath.FullName, "tools", "MSIX"));
        return msixDir.Exists ? msixDir : null;
    }

    /// <summary>
    /// Comparer for sorting version strings, including prerelease support
    /// </summary>
    private class VersionStringComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null)
            {
                return 0;
            }
            if (x == null)
            {
                return -1;
            }
            if (y == null)
            {
                return 1;
            }

            // Use the same comparison logic as NugetService.CompareVersions
            return NugetService.CompareVersions(x, y);
        }
    }
}
