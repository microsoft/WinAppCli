// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Wraps the official NuGet client libraries (NuGet.Protocol / NuGet.Packaging /
/// NuGet.Configuration). Package sources, credentials and the global packages folder are resolved
/// from the user's <c>nuget.config</c> hierarchy (rooted at the current working directory), so
/// private/custom feeds and mirrors are honored when restoring SDK packages. Source resolution /
/// credentials / eligibility are delegated to <see cref="NugetSourceProvider"/> and package
/// download/extraction to <see cref="NugetPackageDownloader"/>; this service owns version selection,
/// dependency traversal and the on-disk cache layout.
/// </summary>
internal partial class NugetService : INugetService
{
    private static readonly ILogger Logger = NullLogger.Instance;
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> DependencyCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly IWinappDirectoryService _winappDirectoryService;
    private readonly NugetSourceProvider _sourceProvider;
    private readonly NugetPackageDownloader _downloader;

    public NugetService(
        IWinappDirectoryService winappDirectoryService,
        NugetSourceProvider sourceProvider,
        NugetPackageDownloader downloader)
    {
        _winappDirectoryService = winappDirectoryService;
        _sourceProvider = sourceProvider;
        _downloader = downloader;
    }

    private static readonly string[] IgnoredDependencyPrefixes =
    [
        "NETStandard.",
        "runtime.",
        "System.",
        "Microsoft.Bcl.",
        "Microsoft.NETCore.",
    ];

    public static readonly string[] SDK_PACKAGES =
    [
        "Microsoft.Windows.CppWinRT",
        BuildToolsService.WINAPP_SDK_PACKAGE,
        "Microsoft.Windows.ImplementationLibrary",
        BuildToolsService.CPP_SDK_PACKAGE,
        $"{BuildToolsService.CPP_SDK_PACKAGE}.x64",
        $"{BuildToolsService.CPP_SDK_PACKAGE}.arm64"
    ];

    public DirectoryInfo GetNuGetGlobalPackagesDir()
    {
        // In test mode (cache override set), use a "packages" subdir of the override directory
        var globalDir = _winappDirectoryService.GetGlobalWinappDirectory();
        if (IsTestOverride(globalDir))
        {
            var overrideDir = new DirectoryInfo(Path.Combine(globalDir.FullName, "packages"));
            if (!overrideDir.Exists)
            {
                overrideDir.Create();
            }
            return overrideDir;
        }

        // Resolve the global packages folder from the user's NuGet configuration. This honors the
        // NUGET_PACKAGES environment variable and the `globalPackagesFolder` setting in nuget.config,
        // falling back to %USERPROFILE%/.nuget/packages.
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(_sourceProvider.Settings);
        var nugetDir = new DirectoryInfo(globalPackagesFolder);
        if (!nugetDir.Exists)
        {
            nugetDir.Create();
        }
        return nugetDir;
    }

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
    {
        // Validate the identity BEFORE it is ever turned into a filesystem path. A malformed id or a value
        // that is not a real NuGet version (e.g. "..") must never be concatenated into the cache path: it
        // could otherwise resolve to a directory outside the package folder (path traversal) that a later
        // DirectoryInfo.Exists() check would treat as an already-installed package. There is deliberately no
        // raw string fallback — an unparseable version is an error, not a literal folder name.
        if (string.IsNullOrWhiteSpace(packageName) || !PackageIdValidator.IsValidPackageId(packageName))
        {
            throw new InvalidOperationException(
                $"'{packageName}' is not a valid NuGet package id.");
        }

        var parsed = ParseVersion(packageName, version);
        var cache = GetNuGetGlobalPackagesDir();
        // Resolve the on-disk folder the same way the global-packages writer does, so the path matches
        // regardless of how the version string is expressed (NuGet stores e.g. "1.0" under "1.0.0").
        var resolver = new VersionFolderPathResolver(cache.FullName);
        return new DirectoryInfo(resolver.GetInstallPath(packageName, parsed));
    }

    /// <summary>
    /// Reports whether <paramref name="package"/> <paramref name="version"/> is FULLY installed in the NuGet
    /// global-packages cache. The version directory merely existing is not sufficient: NuGet writes the
    /// ".nupkg.metadata" completion marker only after extraction finishes, so an interrupted download can
    /// leave a partial folder (missing nuspec / lib files) with no marker. Both this service and higher-level
    /// callers must gate "already installed" decisions on this single predicate so a partial cache entry is
    /// never mistaken for a complete install (which would let restore report a truncated dependency graph as
    /// success).
    /// </summary>
    public bool IsPackageInstalled(string package, string version) =>
        HasCompletionMarker(GetNuGetPackageDir(package, version));

    /// <summary>
    /// True when <paramref name="packageDir"/> exists and contains NuGet's ".nupkg.metadata" completion
    /// marker — the same signal NuGet itself uses to treat a global-packages entry as fully extracted.
    /// </summary>
    private static bool HasCompletionMarker(DirectoryInfo packageDir) =>
        packageDir.Exists && File.Exists(Path.Combine(packageDir.FullName, ".nupkg.metadata"));

    /// <summary>
    /// Normalizes a version string to NuGet's canonical form (e.g. "1.0" -> "1.0.0") so the value stored
    /// and returned by the installer matches the on-disk global-packages folder layout. Returns the input
    /// unchanged if it is not a parseable NuGet version.
    /// </summary>
    internal static string NormalizeVersion(string version) =>
        NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version;

    /// <summary>
    /// Parses a version string into a <see cref="NuGetVersion"/>, throwing an
    /// <see cref="InvalidOperationException"/> that names the package and the offending value when it is
    /// not a valid NuGet version. <see cref="NuGetVersion.Parse(string)"/> would otherwise surface a raw
    /// <see cref="ArgumentException"/> with a message less actionable than the rest of this service.
    /// </summary>
    private static NuGetVersion ParseVersion(string packageId, string version)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            throw new InvalidOperationException(
                $"'{version}' is not a valid NuGet version for package '{packageId}'. Specify a valid NuGet version such as 1.2.3 or 1.2.3-preview.");
        }

        return parsed;
    }

    /// <summary>
    /// Detects whether the global winapp directory is a test override (not the real user profile .winapp).
    /// </summary>
    private static bool IsTestOverride(DirectoryInfo globalDir)
    {
        var defaultWinapp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp");
        return !string.Equals(globalDir.FullName, defaultWinapp, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY"));
    }

    public async Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        NugetSourceProvider.EnsureCredentialService();
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dependencyFailures = new List<string>();
        // Every declared range seen for a given dependency id is accumulated here as the graph is walked, so a
        // later branch can be evaluated against the full constraint set rather than only its own range.
        var dependencyConstraints = new Dictionary<string, List<VersionRange>>(StringComparer.OrdinalIgnoreCase);
        using var cacheContext = new SourceCacheContext();
        await InstallPackageRecursiveAsync(package, version, packages, dependencyFailures, dependencyConstraints, taskContext, cacheContext, cancellationToken);

        // A downloaded root package with unresolvable/uninstallable REQUIRED transitive dependencies is an
        // incomplete install, not a success. Each gap was surfaced as a warning above (and the rest of the
        // tree was still installed best-effort); now fail the operation so callers such as `restore` exit
        // non-zero instead of reporting a partial install as complete.
        if (dependencyFailures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Installed {package} {version} but {dependencyFailures.Count} required dependency(ies) could not be installed: {string.Join("; ", dependencyFailures)}.");
        }

        return packages;
    }

    /// <summary>
    /// Downloads and extracts a NuGet package to the global packages cache, then recursively installs dependencies.
    /// </summary>
    private async Task InstallPackageRecursiveAsync(string package, string version, Dictionary<string, string> installed, List<string> dependencyFailures, Dictionary<string, List<VersionRange>> dependencyConstraints, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        // Already processed this package?
        if (installed.ContainsKey(package))
        {
            return;
        }

        // Store the canonical (normalized) version so the value recorded in `installed` matches the
        // on-disk NuGet folder layout (e.g. "1.0" -> "1.0.0"). Downstream consumers build cache paths by
        // concatenating this value, so an un-normalized shorthand would point them at a folder that the
        // global-packages writer never created.
        var normalizedVersion = NormalizeVersion(version);

        var packageDir = GetNuGetPackageDir(package, normalizedVersion);

        // Already fully installed on disk? Gate on the shared completion-marker predicate (see
        // IsPackageInstalled): the directory merely existing is not enough, because an interrupted extraction
        // can leave a partial folder with no ".nupkg.metadata" marker. Accepting that corrupt entry would let
        // ReadDependenciesFromNuspec return an empty set and restore report a truncated graph as success. When
        // the marker is missing, fall through so the downloader re-extracts and completes the entry.
        if (HasCompletionMarker(packageDir))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Skip} {package} {normalizedVersion} already present");
            installed[package] = normalizedVersion;
            // Still resolve dependencies to populate installed dictionary
            await ResolveDependenciesAsync(packageDir, package, normalizedVersion, installed, dependencyFailures, dependencyConstraints, taskContext, cacheContext, cancellationToken);
            return;
        }

        // Download and extract the package from the user's configured NuGet sources into the
        // global packages folder (using the standard NuGet on-disk layout). Throws with the
        // underlying source error if no configured source can provide the package.
        var identity = new PackageIdentity(package, ParseVersion(package, normalizedVersion));
        await _downloader.DownloadPackageAsync(identity, GetNuGetGlobalPackagesDir().FullName, cacheContext, cancellationToken);

        installed[package] = normalizedVersion;
        taskContext.AddStatusMessage($"{UiSymbols.Check} Installed {package} {normalizedVersion}");

        // Recursively install dependencies
        await ResolveDependenciesAsync(packageDir, package, normalizedVersion, installed, dependencyFailures, dependencyConstraints, taskContext, cacheContext, cancellationToken);
    }

    /// <summary>
    /// Reads the .nuspec from an extracted package and recursively installs dependencies.
    /// </summary>
    private async Task ResolveDependenciesAsync(DirectoryInfo packageDir, string package, string version, Dictionary<string, string> installed, List<string> dependencyFailures, Dictionary<string, List<VersionRange>> dependencyConstraints, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        Dictionary<string, VersionRange> deps;
        try
        {
            deps = ReadDependenciesFromNuspec(packageDir, package);
        }
        catch (Exception ex)
        {
            // The package was downloaded, but its .nuspec cannot be read so its dependency graph is unknown.
            // This is different from a package that simply declares no dependencies (that reads back as an
            // empty set without throwing): an unreadable manifest means required transitive packages may be
            // silently missing. Record it as a failure — like the dependency resolution/install errors below —
            // so the overall install fails loudly instead of reporting success with an incomplete graph.
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not read dependencies for {package} {version}: {ex.Message}");
            dependencyFailures.Add($"{package} {version}: dependency manifest could not be read: {ex.Message}");
            return;
        }

        foreach (var (depName, depVersionRange) in deps)
        {
            // Accumulate every range required for this dependency id across all branches of the graph so a
            // later branch can be evaluated against the full constraint set, not just its own range.
            if (!dependencyConstraints.TryGetValue(depName, out var accumulatedRanges))
            {
                accumulatedRanges = [];
                dependencyConstraints[depName] = accumulatedRanges;
            }
            accumulatedRanges.Add(depVersionRange);

            if (installed.TryGetValue(depName, out var installedDepVersion))
            {
                // The dependency id was already installed earlier in this operation, which fixed its version.
                // A package-id match alone is not enough: in a diamond graph two branches can require ranges
                // the selected version does not both satisfy. Distinguish two cases:
                //   * GENUINE conflict — no single version can satisfy every accumulated range (e.g. [1,2) and
                //     [2,3)). That graph is unsatisfiable and must fail the operation rather than be accepted
                //     as a complete install with an invalid graph.
                //   * Differing lower bounds only — some version satisfies every range (e.g. [1.0,) then
                //     [2.0,)), but winapp keeps the already-selected (lowest) version. That is a documented
                //     limitation of its resolve-as-it-installs strategy (winapp targets curated SDK graphs and
                //     does not perform NuGet's global upgrade/downgrade unification), not a conflict — warn at
                //     debug level and continue so common differing-minimum diamonds are not falsely failed.
                if (!(NuGetVersion.TryParse(installedDepVersion, out var installedNuGetVersion)
                        && RangeSatisfiesWithFloat(depVersionRange, installedNuGetVersion)))
                {
                    var rangeText = depVersionRange.OriginalString ?? depVersionRange.ToNormalizedString();
                    if (RangesHaveCommonVersion(accumulatedRanges))
                    {
                        taskContext.AddDebugMessage(
                            $"{UiSymbols.Skip} {depName} kept at already-selected {installedDepVersion}; {package} {version} requests '{rangeText}' (a higher version would also satisfy it, but winapp keeps the first-selected version).");
                    }
                    else
                    {
                        var conflict = $"{depName} cannot be resolved to a single version: already selected {installedDepVersion}, but {package} {version} requires '{rangeText}' and no version satisfies every requirement";
                        taskContext.AddStatusMessage($"{UiSymbols.Warning} Dependency version conflict: {conflict}");
                        dependencyFailures.Add(conflict);
                    }
                }
                continue;
            }

            try
            {
                var depVersion = await ResolveDependencyVersionAsync(depName, depVersionRange, cacheContext, cancellationToken);
                if (string.IsNullOrEmpty(depVersion))
                {
                    // A null result now means only a version-less / fully unbounded dependency (the range
                    // constrains nothing); skip it rather than guessing a version to install. A bounded range
                    // that cannot be resolved throws instead and is surfaced by the catch below as a
                    // non-fatal per-dependency warning, so it is no longer silently dropped.
                    continue;
                }

                await InstallPackageRecursiveAsync(depName, depVersion, installed, dependencyFailures, dependencyConstraints, taskContext, cacheContext, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Never mask a genuine cancellation as a successful (but incomplete) install.
                throw;
            }
            catch (Exception ex)
            {
                // A single transitive dependency that cannot be resolved (e.g. its only satisfying source
                // could not be queried) or installed should not abort the rest of the tree — keep installing
                // the remaining dependencies best-effort — but the failure must be both visible (not hidden
                // behind verbose-only logging) AND fail the overall operation. Record it so InstallPackageAsync
                // exits non-zero rather than reporting an incomplete install as success.
                taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not install dependency {depName} (required by {package} {version}): {ex.Message}");
                dependencyFailures.Add($"{depName} (required by {package} {version}): {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Like <see cref="VersionRange.Satisfies(NuGetVersion)"/> but also applies a floating range's full
    /// semantics. <c>VersionRange.Satisfies</c> only checks the declared min/max bounds, and a float declares no
    /// upper bound and carries prerelease/prefix eligibility that the bounds don't express — so a stable
    /// <c>1.*</c> reports satisfying <c>2.0.0</c> (above the band) and <c>1.5.0-preview</c> (a prerelease the
    /// stable float excludes). <see cref="FloatRange.Satisfies(NuGetVersion)"/> is the authoritative predicate
    /// for that (ceiling, prerelease eligibility, and prefix), so defer to it for floating ranges. Without this,
    /// a diamond where one branch already fixed the shared dependency to a version outside a later branch's
    /// float (e.g. the 2.* branch installs 2.0.0 first, then the 1.* branch is checked) would be silently
    /// accepted as satisfied.
    /// </summary>
    internal static bool RangeSatisfiesWithFloat(VersionRange range, NuGetVersion version)
    {
        if (!range.Satisfies(version))
        {
            return false;
        }

        if (range.IsFloating && range.Float is { } floatRange)
        {
            return floatRange.Satisfies(version);
        }

        return true;
    }

    /// <summary>
    /// Returns true when at least one version could satisfy ALL of <paramref name="ranges"/> simultaneously.
    /// Used to tell a genuinely unsatisfiable set of diamond constraints (e.g. [1.0,2.0) and [2.0,3.0), which
    /// must fail the install) apart from constraints that merely differ in their lower bound (e.g. [1.0,) and
    /// [2.0,), where a higher version satisfies both and keeping winapp's first-selected version is a
    /// documented limitation, not a conflict).
    ///
    /// The intersection of these upward version intervals, when non-empty, is anchored at the greatest lower
    /// bound, which is one of the ranges' (inclusive) minimum versions — including a float's floor (1.* => 1.0.0,
    /// 1.2.3-beta.* => 1.2.3-beta.0). So each candidate minimum is tested against every range with the full
    /// float-aware predicate (<see cref="RangeSatisfiesWithFloat"/>) rather than reducing floats to numeric
    /// bounds: that keeps prerelease/prefix eligibility intact, so two floats sharing a numeric prefix but no
    /// common version (1.2.3-beta.* and 1.2.3-rc.*) are correctly reported as a conflict, while an unbounded
    /// float (*) still overlaps anything. Real NuGet dependency ranges always carry an inclusive lower bound,
    /// so scanning the candidate minimums is exhaustive for them; a set with no lower bound at all is open below
    /// and therefore trivially satisfiable.
    /// </summary>
    internal static bool RangesHaveCommonVersion(IReadOnlyList<VersionRange> ranges)
    {
        var sawLowerBound = false;

        foreach (var range in ranges)
        {
            if (!range.HasLowerBound || range.MinVersion is not { } candidate)
            {
                continue;
            }

            sawLowerBound = true;
            if (ranges.All(r => RangeSatisfiesWithFloat(r, candidate)))
            {
                return true;
            }
        }

        // No range constrains the low end: the intersection is open below and non-empty, so treat it as
        // satisfiable rather than a conflict.
        return !sawLowerBound;
    }
}
