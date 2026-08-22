// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Locates, installs, and gates the framework-dependent Windows App Runtime (Framework + DDLM) MSIX
/// packages an unpackaged WinUI app needs at startup.
/// </summary>
internal class WindowsAppRuntimeService(
    IPackageRegistrationService packageRegistrationService,
    INugetService nugetService) : IWindowsAppRuntimeService
{
    private sealed record RuntimePackageCandidate(string FilePath, string PackageName, string Version, string FileName);

    /// <summary>Package entry information from MSIX inventory.</summary>
    public class MsixPackageEntry
    {
        public required string FileName { get; set; }
        public required string PackageIdentity { get; set; }
    }

    /// <summary>
    /// Parses the MSIX inventory file and returns package entries.
    /// </summary>
    /// <param name="architecture">
    /// Target architecture whose <c>win10-{arch}</c> inventory is read. When <c>null</c>, defaults to the
    /// CLI's process architecture (folder mode / legacy callers). Project mode passes the app's resolved arch.
    /// </param>
    public static async Task<List<MsixPackageEntry>?> ParseMsixInventoryAsync(TaskContext taskContext, DirectoryInfo msixDir, CancellationToken cancellationToken, string? architecture = null)
    {
        architecture = RunArchHelper.NormalizeArchitecture(architecture) ?? RunArchHelper.DefaultArchitecture();

        taskContext.AddDebugMessage($"{UiSymbols.Note} Using architecture for MSIX inventory: {architecture}");

        var msixArchDir = Path.Combine(msixDir.FullName, $"win10-{architecture}");
        if (!Directory.Exists(msixArchDir))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No MSIX packages found for architecture {architecture}");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available directories: {string.Join(", ", msixDir.GetDirectories().Select(d => d.Name))}");
            return null;
        }

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
    /// Reads the actual package Name and Version from the AppxManifest.xml inside an MSIX file, since the
    /// inventory file's names can be wrong (e.g. the DDLM).
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
            var doc = AppxManifestDocument.Load(stream);
            return (doc.IdentityName, doc.IdentityVersion);
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Could not read identity from {Path.GetFileName(msixFilePath)}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Installs Windows App SDK runtime MSIX packages for the target architecture.
    /// </summary>
    public async Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null)
    {
        // Directory/inventory arch: needs a concrete value to locate win10-{arch}. Default to the CLI's
        // process arch (folder mode / legacy callers); project mode passes the app's resolved --arch.
        var dirArch = RunArchHelper.NormalizeArchitecture(architecture) ?? RunArchHelper.DefaultArchitecture();

        // Install-skip filter arch: nullable. Folder mode keeps it null (arch-agnostic "already installed?"
        // check); project mode filters by target arch so a cross-arch runtime isn't wrongly skipped because
        // a same-name host-arch package is present.
        var filterArch = RunArchHelper.NormalizeArchitecture(architecture);

        var packagesToCheck = await GetRuntimePackageCandidatesAsync(msixDir, taskContext, dirArch, cancellationToken);

        if (packagesToCheck.Count == 0)
        {
            return (0, 0, Array.Empty<(string, string)>());
        }

        taskContext.AddDebugMessage($"{UiSymbols.Info} Checking and installing {packagesToCheck.Count} MSIX packages");

        var installedCount = 0;
        var errorCount = 0;

        foreach (var candidate in packagesToCheck)
        {
            var (filePath, packageName, newVersion, fileName) =
                (candidate.FilePath, candidate.PackageName, candidate.Version, candidate.FileName);
            // Skip if already installed with same or newer version. The arch filter applies only in project
            // mode (filterArch non-null); folder mode is null → arch-agnostic match.
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ctrl+C — stop provisioning immediately instead of counting cancellation as an install
                // failure and hammering the remaining packages with an already-cancelled token.
                throw;
            }
            catch (Exception ex)
            {
                errorCount++;
                taskContext.AddDebugMessage($"{UiSymbols.Note} {fileName}: {ex.Message}");
            }
        }

        if (installedCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Check} Installed {installedCount} MSIX packages");
        }
        if (errorCount > 0)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} {errorCount} packages failed to install");
        }

        // Surface the versioned Framework + DDLM identities so the caller can gate on the SPECIFIC runtime
        // the app was built against (not any registered version) and reject a stale older patch.
        var runtimePackages = packagesToCheck
            .Where(p => IsRuntimeGatePackageName(p.PackageName))
            .Select(p => (Name: p.PackageName, Version: p.Version))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (installedCount, errorCount, runtimePackages);
    }

    public async Task<IReadOnlyList<(string Name, string Version)>> GetWindowsAppRuntimePackagesAsync(
        DirectoryInfo msixDir,
        TaskContext taskContext,
        CancellationToken cancellationToken,
        string? architecture = null)
    {
        var arch = RunArchHelper.NormalizeArchitecture(architecture) ?? RunArchHelper.DefaultArchitecture();
        var candidates = await GetRuntimePackageCandidatesAsync(msixDir, taskContext, arch, cancellationToken);
        return candidates
            .Where(p => IsRuntimeGatePackageName(p.PackageName))
            .Select(p => (Name: p.PackageName, Version: p.Version))
            .DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<RuntimePackageCandidate>> GetRuntimePackageCandidatesAsync(
        DirectoryInfo msixDir,
        TaskContext taskContext,
        string architecture,
        CancellationToken cancellationToken)
    {
        var packageEntries = await ParseMsixInventoryAsync(
            taskContext,
            msixDir,
            cancellationToken,
            architecture);
        if (packageEntries == null || packageEntries.Count == 0)
        {
            return [];
        }

        var msixArchDir = Path.Join(msixDir.FullName, $"win10-{architecture}");
        var candidates = new List<RuntimePackageCandidate>();
        foreach (var entry in packageEntries)
        {
            var msixFilePath = Path.Combine(msixArchDir, entry.FileName);
            if (!File.Exists(msixFilePath))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} MSIX file not found: {msixFilePath}");
                continue;
            }

            // Read the real identity from the MSIX's AppxManifest.xml (the inventory's PackageIdentity can
            // differ from the installed name).
            var (packageName, version) = ReadMsixIdentity(msixFilePath, taskContext);
            if (packageName == null)
            {
                // Fallback: parse from inventory identity string.
                var identityParts = entry.PackageIdentity.Split('_');
                packageName = identityParts[0];
                version = identityParts.Length >= 2 ? identityParts[1] : "";
            }

            candidates.Add(new RuntimePackageCandidate(
                msixFilePath,
                packageName,
                version ?? "",
                entry.FileName));
        }

        return candidates;
    }

    /// <summary>
    /// Package-name prefixes that identify a framework-dependent Windows App Runtime registration (a
    /// versioned Framework plus its matching-arch DDLM; both must be present for an unpackaged app to boot).
    /// </summary>
    private const string WinAppRuntimeFrameworkPrefix = "Microsoft.WindowsAppRuntime.";
    private const string WinAppRuntimeDdlmPrefix = "Microsoft.WinAppRuntime.DDLM.";

    // The Component Store (CBS) package shares the Framework prefix but is a system singleton — exclude it
    // so its presence never masks a missing target-arch Framework. (Internal for a discriminator test.)
    internal const string WinAppRuntimeCbsInfix = ".CBS.";

    /// <summary>
    /// Classifies a package name as the app-facing versioned Framework (family
    /// <c>Microsoft.WindowsAppRuntime.{major.minor}</c>, excluding CBS). Its version lives in the package
    /// Version, not the name, so the gate version-compares separately.
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
    /// Returns <c>true</c> when a framework-dependent Windows App Runtime is registered for
    /// <paramref name="architecture"/>: both a versioned Framework package (excluding CBS) and its
    /// matching-arch DDLM. Mirrors the bootstrapper's presence check so callers can gate the launch.
    /// <para>
    /// When <paramref name="expectedRuntimePackages"/> is supplied, the app-facing <b>Framework</b> family
    /// is additionally required at a version <b>&gt;=</b> the required one — closing the false-pass where a
    /// version-specific install silently failed but a different/older WinAppSDK version is registered. The
    /// <b>DDLM</b> is release-gated the same way (highest registered DDLM must be <b>&gt;=</b> the required
    /// release; DDLMs install side-by-side, so a <c>&gt;=</c> compare accepts a newer compatible DDLM while
    /// rejecting only-older). An unparseable required/installed version falls back to the generic presence
    /// check above. Empty/null (folder mode / legacy callers) runs only the generic presence check.
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
                // Only the app-facing Framework family gets an exact-identity + version check; the DDLM is
                // release-gated separately below (see IsDdlmReleaseSatisfied).
                if (!IsFrameworkGatePackageName(name))
                {
                    continue;
                }

                // Require the SPECIFIC Framework family the app was built against. GetInstalledVersion is an
                // exact-name match, so a wrong minor (need 1.8, have 1.6) returns null and fails the gate.
                var installedVersion = packageRegistrationService.GetInstalledVersion(name, arch);
                if (installedVersion is null)
                {
                    return false;
                }

                // Patch-level guard: the family name is only major.minor, so a stale older patch would
                // satisfy a name-presence check. Reject when both versions parse and the installed one is
                // older; fall back to presence when either is unparseable.
                if (Version.TryParse(requiredVersion, out var required) &&
                    Version.TryParse(installedVersion, out var installed) &&
                    installed < required)
                {
                    return false;
                }
            }

            if (!IsDdlmReleaseSatisfied(arch, expectedRuntimePackages))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies the highest DDLM registered for <paramref name="arch"/> is at least the required release
    /// from <paramref name="expectedRuntimePackages"/>. Returns <c>true</c> (accept) when no required/installed
    /// version parses — falling back to the generic DDLM presence the caller already confirmed. A <c>&gt;=</c>
    /// compare against the newest installed DDLM keeps a newer-than-required DDLM compatible while rejecting
    /// only-older.
    /// </summary>
    private bool IsDdlmReleaseSatisfied(string arch, IReadOnlyList<(string Name, string Version)> expectedRuntimePackages)
    {
        Version? required = null;
        foreach (var (name, version) in expectedRuntimePackages)
        {
            if (!name.StartsWith(WinAppRuntimeDdlmPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Version.TryParse(version, out var parsed) && (required is null || parsed > required))
            {
                required = parsed;
            }
        }

        if (required is null)
        {
            // No parseable required DDLM release — nothing to gate beyond the caller's presence check.
            return true;
        }

        var installedRaw = packageRegistrationService.GetHighestInstalledVersion(WinAppRuntimeDdlmPrefix, arch);
        if (!Version.TryParse(installedRaw, out var installed))
        {
            // A DDLM is present but its version string is unexpected — don't block on an unparseable version.
            return true;
        }

        return installed >= required;
    }

    /// <summary>
    /// Finds the MSIX directory for Windows App SDK runtime packages, or null if not found.
    /// </summary>
    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null, bool requireExactVersion = false)
    {
        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
        return FindMsixDirectoryInNuGetCache(nugetCacheDir, usedVersions, requireExactVersion);
    }

    /// <summary>
    /// Searches the NuGet global packages cache (lowercase id/version folder convention).
    /// </summary>
    private static DirectoryInfo? FindMsixDirectoryInNuGetCache(DirectoryInfo nugetCacheDir, Dictionary<string, string>? usedVersions, bool requireExactVersion)
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

            // Fallback to main package.
            if (usedVersions.TryGetValue(BuildToolsService.WINAPP_SDK_PACKAGE, out var mainVersion))
            {
                var msixDir = TryGetMsixDirectoryFromNuGetCache(nugetCacheDir, BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion);
                if (msixDir != null)
                {
                    return msixDir;
                }
            }
        }

        if (requireExactVersion)
        {
            // Exact-version callers (project-mode unpackaged) must NOT accept an unrelated cached runtime:
            // the highest-version scans below would return a different WinAppSDK version and the derived
            // presence gate would falsely pass. Stop here so the caller sees "exact version unavailable".
            return null;
        }

        // General scan: any runtime package directories.
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

        // Fallback: main package.
        var mainDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, BuildToolsService.WINAPP_SDK_PACKAGE.ToLowerInvariant()));
        if (mainDir.Exists)
        {
            return mainDir.GetDirectories()
                .OrderByDescending(d => d.Name, new VersionStringComparer())
                .Select(TryGetMsixDirectoryFromPath)
                .FirstOrDefault(msixDir => msixDir != null);
        }

        return null;
    }

    /// <summary>Checks the NuGet cache for a specific package/version (lowercase ID/version layout).</summary>
    private static DirectoryInfo? TryGetMsixDirectoryFromNuGetCache(DirectoryInfo nugetCacheDir, string packageId, string version)
    {
        var pkgVersionDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, packageId.ToLowerInvariant(), version));
        return TryGetMsixDirectoryFromPath(pkgVersionDir);
    }

    /// <summary>Returns the <c>tools/MSIX</c> directory under a package path if it exists, else null.</summary>
    private static DirectoryInfo? TryGetMsixDirectoryFromPath(DirectoryInfo packagePath)
    {
        var msixDir = new DirectoryInfo(Path.Combine(packagePath.FullName, "tools", "MSIX"));
        return msixDir.Exists ? msixDir : null;
    }

    /// <summary>Comparer for sorting version strings, including prerelease support.</summary>
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

            return NugetService.CompareVersions(x, y);
        }
    }
}
