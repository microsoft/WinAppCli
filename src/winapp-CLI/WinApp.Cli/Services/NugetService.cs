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
internal class NugetService : INugetService
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
        using var cacheContext = new SourceCacheContext();
        await InstallPackageRecursiveAsync(package, version, packages, dependencyFailures, taskContext, cacheContext, cancellationToken);

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
    private async Task InstallPackageRecursiveAsync(string package, string version, Dictionary<string, string> installed, List<string> dependencyFailures, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
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

        // Already installed on disk?
        if (packageDir.Exists)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Skip} {package} {normalizedVersion} already present");
            installed[package] = normalizedVersion;
            // Still resolve dependencies to populate installed dictionary
            await ResolveDependenciesAsync(packageDir, package, normalizedVersion, installed, dependencyFailures, taskContext, cacheContext, cancellationToken);
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
        await ResolveDependenciesAsync(packageDir, package, normalizedVersion, installed, dependencyFailures, taskContext, cacheContext, cancellationToken);
    }

    /// <summary>
    /// Reads the .nuspec from an extracted package and recursively installs dependencies.
    /// </summary>
    private async Task ResolveDependenciesAsync(DirectoryInfo packageDir, string package, string version, Dictionary<string, string> installed, List<string> dependencyFailures, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        Dictionary<string, VersionRange> deps;
        try
        {
            deps = ReadDependenciesFromNuspec(packageDir, package);
        }
        catch (Exception ex)
        {
            // The .nuspec is best-effort metadata; a malformed/unreadable manifest should not fail the
            // install of the package that was already downloaded, but surface it so the gap is visible.
            taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not read dependencies for {package} {version}: {ex.Message}");
            return;
        }

        foreach (var (depName, depVersionRange) in deps)
        {
            if (installed.ContainsKey(depName))
            {
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

                await InstallPackageRecursiveAsync(depName, depVersion, installed, dependencyFailures, taskContext, cacheContext, cancellationToken);
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
    /// Reads dependencies from the .nuspec file embedded in an extracted NuGet package.
    /// Returns every declared dependency across all target-framework groups (first occurrence wins).
    /// </summary>
    private static Dictionary<string, VersionRange> ReadDependenciesFromNuspec(DirectoryInfo packageDir, string packageName)
    {
        var dependencies = new Dictionary<string, VersionRange>(StringComparer.OrdinalIgnoreCase);

        // The .nuspec file is at the root of the extracted package, named {lowercase-id}.nuspec
        var nuspecPath = Path.Combine(packageDir.FullName, $"{packageName.ToLowerInvariant()}.nuspec");
        if (!File.Exists(nuspecPath))
        {
            // Try finding any .nuspec file
            var nuspecFiles = Directory.GetFiles(packageDir.FullName, "*.nuspec", SearchOption.TopDirectoryOnly);
            if (nuspecFiles.Length == 0)
            {
                return dependencies;
            }
            nuspecPath = nuspecFiles[0];
        }

        var nuspec = new NuspecReader(nuspecPath);
        foreach (var group in nuspec.GetDependencyGroups())
        {
            foreach (var dependency in group.Packages)
            {
                // Skip framework/runtime reference packages (same filter as FetchDirectDependenciesAsync)
                // so we don't attempt to install non-winapp packages that aren't served by the feed.
                if (!string.IsNullOrEmpty(dependency.Id)
                    && dependency.VersionRange != null
                    && !IgnoredDependencyPrefixes.Any(p => dependency.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    dependencies.TryAdd(dependency.Id, dependency.VersionRange);
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Parses a NuGet version range and extracts the minimum version.
    /// Handles: "1.0.0", "[1.0.0]", "[1.0.0, )", "(1.0.0, 2.0.0)", and the
    /// bracket-stripped form "1.0.0, 2.0.0" (which can happen when callers
    /// pre-clean brackets without splitting on the range separator).
    /// </summary>
    internal static string ParseMinimumVersion(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return string.Empty;
        }

        // Strip brackets/parens (no-op if none are present)
        var trimmed = versionRange.Trim().TrimStart('[', '(').TrimEnd(']', ')');

        // Take the lower bound (before comma if present). Always check for a comma —
        // a NuGet range with brackets stripped (e.g. "1.0.0, 2.0.0") still needs
        // splitting; otherwise we'd treat the whole thing as a literal version.
        var commaIdx = trimmed.IndexOf(',');
        if (commaIdx >= 0)
        {
            trimmed = trimmed[..commaIdx].Trim();
        }

        return trimmed;
    }

    public async Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        if (sdkInstallMode == SdkInstallMode.None)
        {
            throw new ArgumentException("sdkInstallMode cannot be None", nameof(sdkInstallMode));
        }

        var list = await GetListedVersionsAsync(packageName, cancellationToken);
        var totalFound = list.Count;

        // If not winapp SDK, preview and experimental versions are the same
        if (packageName.StartsWith(BuildToolsService.WINAPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase))
        {
            if (sdkInstallMode == SdkInstallMode.Stable)
            {
                // Only stable versions (no prerelease suffix)
                list = [.. list.Where(v => !v.Contains('-', StringComparison.Ordinal))];
            }
            else if (sdkInstallMode == SdkInstallMode.Preview)
            {
                // Only with preview
                list = [.. list.Where(v => v.Contains("-preview", StringComparison.OrdinalIgnoreCase))];
            }
            else if (sdkInstallMode == SdkInstallMode.Experimental)
            {
                // Only with experimental
                list = [.. list.Where(v => v.Contains("-experimental", StringComparison.OrdinalIgnoreCase))];
            }
            // For Experimental mode: keep all versions (no filtering needed)
        }
        else
        {
            if (sdkInstallMode == SdkInstallMode.Stable)
            {
                // Only stable versions (no prerelease suffix)
                list = [.. list.Where(v => !v.Contains('-', StringComparison.Ordinal))];
            }
        }

        if (list.Count == 0)
        {
            // Distinguish "the sources returned versions but none matched the requested channel" from
            // "no versions came back at all" so the user knows whether to change the channel or to check
            // the package ID / configured sources / credentials.
            var reason = totalFound > 0
                ? $"found {totalFound} version(s) but none matched the '{sdkInstallMode}' channel"
                : "no versions were returned by the configured NuGet sources — verify the package ID, the configured sources, and any required credentials";
            throw new InvalidOperationException($"No matching versions found for {packageName} ({reason}).");
        }

        list.Sort(CompareVersions);
        return list[^1];
    }

    /// <summary>
    /// Fetches all listed (non-unlisted) versions of a package from every source eligible to serve it
    /// (honoring <c>&lt;packageSourceMapping&gt;</c>). Unlisted versions are excluded so they are never
    /// selected as "latest". Because the result feeds a MAX ("latest") decision, a source that cannot be
    /// queried is treated as fatal rather than silently skipped: a partial result could otherwise make a
    /// caller select an older version (e.g. <c>update</c> could downgrade a pinned package). A source that
    /// exposes only <c>PackageBaseAddress</c> (no registration resource) is enumerated via its flat container
    /// through <see cref="GetSourceVersionsAsync"/> rather than skipped, so private feeds of that shape still
    /// contribute versions.
    /// </summary>
    private async Task<List<string>> GetListedVersionsAsync(string packageName, CancellationToken cancellationToken)
    {
        NugetSourceProvider.EnsureCredentialService();

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? lastError = null;
        string? lastErrorSource = null;
        using var cacheContext = new SourceCacheContext();

        var repos = _sourceProvider.GetRepositoriesForPackage(packageName);
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Exclude unlisted versions so an unlisted build is never selected as "latest". A source that
                // exposes only PackageBaseAddress (no registration resource) is enumerated via its flat
                // container instead of being skipped, so latest resolution still works against such feeds.
                foreach (var version in await GetSourceVersionsAsync(repo, packageName, includeUnlisted: false, cacheContext, cancellationToken))
                {
                    versions.Add(version.ToNormalizedString());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Propagate genuine cancellation instead of masking it as "no versions found".
                throw;
            }
            catch (Exception ex)
            {
                // Record why this source failed (and which one). Because "latest" is a MAX across sources,
                // any eligible-source failure is treated as fatal after the loop (fail-closed) rather than
                // trusting a partial result: a source we could not reach/authenticate could hide a newer
                // version and cause a downgrade or a missed update.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
            }
        }

        // "Latest" is a MAX across sources, so a source we could not reach/authenticate would lower that
        // max and could cause a downgrade or a missed update. If any eligible source failed, surface it
        // (naming the source) instead of returning a partial, non-authoritative result. Inline the inner
        // message because top-level command handlers print only ex.Message.
        if (lastError != null)
        {
            throw new InvalidOperationException(
                $"Could not reliably determine the versions of {packageName}: source '{lastErrorSource}' could not be queried: {lastError.Message}",
                lastError);
        }

        // No source was eligible at all: give the same actionable guidance as the download/dependency
        // paths (missing mapping vs. disabled mapped source vs. no sources configured) rather than letting
        // the caller fall back to a generic "verify package ID / sources / credentials" message.
        if (repos.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot resolve versions for {packageName}: {_sourceProvider.DescribeNoEligibleSources(packageName)}.");
        }

        return [.. versions];
    }

    /// <summary>
    /// Resolves a dependency's declared <see cref="VersionRange"/> to a single concrete version to install by
    /// selecting the lowest available version that satisfies the range on the configured sources — matching
    /// NuGet's lowest-applicable resolution. The declared lower bound is never assumed to exist: a range such
    /// as <c>[1.2.3, )</c> is satisfied by 1.2.3 only if a source actually offers it, otherwise the next higher
    /// available version is selected (a mirror may carry 1.3.0 but not 1.2.3). This also honors floating ranges.
    /// The candidate set includes UNLISTED versions, because a package legitimately pins an exact dependency
    /// version that the publisher has unlisted (e.g. Windows App SDK experimental builds unlist their
    /// <c>.Runtime</c>/<c>.Foundation</c> sub-packages) and such a pin must still resolve. Returns null ONLY
    /// when the range constrains nothing (a version-less or fully unbounded dependency) — a deliberate skip.
    /// A BOUNDED range that no available version satisfies means a required transitive package cannot be
    /// resolved, so it throws instead of returning null (which both callers read as "omit this dependency"):
    /// otherwise the graph path would report success with a package missing and the install path would
    /// silently skip it. The thrown reason is specific — a source that could have satisfied the range failed
    /// to answer, no source was eligible at all (empty feed list or a <c>packageSourceMapping</c> exclusion),
    /// or eligible sources offered no satisfying version — so the graph path can surface it (fail loudly) and
    /// the install path catch it as a non-fatal per-dependency warning.
    /// </summary>
    private async Task<string?> ResolveDependencyVersionAsync(string packageId, VersionRange? range, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        if (range is null)
        {
            return null;
        }

        // A range with no bounds at all (a version-less dependency) constrains nothing; keep the historical
        // behavior of skipping it rather than pulling in an arbitrary version.
        if (!range.HasLowerBound && !range.HasUpperBound)
        {
            return null;
        }

        // Resolve every bounded/floating range against the versions the sources actually offer and pick the
        // lowest one that satisfies it (NuGet's lowest-applicable rule). Never shortcut to the declared lower
        // bound: it may be excluded by the range (an exclusive bound) or simply absent from the source.
        var candidates = await GetCandidateVersionsForRangeAsync(packageId, cacheContext, cancellationToken);
        var best = range.FindBestMatch(candidates.Versions);
        if (best is not null)
        {
            return best.ToNormalizedString();
        }

        // Nothing satisfied the range. A bounded range that matches no available version means a REQUIRED
        // transitive package cannot be resolved, so fail loudly instead of returning null (which both callers
        // read as "omit this dependency"): the graph path would otherwise report success with a package
        // missing and the install path would silently skip it. Distinguish the causes so the error is
        // actionable.
        if (candidates.Error is not null)
        {
            // A source that could have satisfied the range could not be queried (feed/auth/network error);
            // surface it rather than masking a real failure as a missing dependency.
            throw new InvalidOperationException(
                $"Could not resolve a version for dependency '{packageId}' satisfying '{range}': source '{candidates.ErrorSource}' could not be queried: {candidates.Error.Message}",
                candidates.Error);
        }

        if (candidates.EligibleSourceCount == 0)
        {
            // No source was eligible at all — an empty feed list or a packageSourceMapping exclusion left the
            // transitive package with nowhere to resolve from. Reuse the mapping-aware diagnosis so the error
            // points at the specific nuget.config fix (matching the download / direct-dependency paths).
            throw new InvalidOperationException(
                $"Cannot resolve dependency '{packageId}' (required version '{range}'): {_sourceProvider.DescribeNoEligibleSources(packageId)}.");
        }

        // Eligible sources were queried and answered, but none offers a version satisfying the range.
        throw new InvalidOperationException(
            $"Cannot resolve dependency '{packageId}': no version offered by the configured NuGet sources satisfies '{range}'.");
    }

    /// <summary>
    /// The candidate versions of a package collected across eligible sources, the number of sources that were
    /// eligible (0 signals a packageSourceMapping exclusion or an empty feed list), plus the last source
    /// failure (if any) so the caller can tell "no version satisfies the range", "no source was eligible" and
    /// "a source could not be queried" apart.
    /// </summary>
    private readonly record struct CandidateVersionsResult(IReadOnlyList<NuGetVersion> Versions, Exception? Error, string? ErrorSource, int EligibleSourceCount);

    /// <summary>
    /// Collects the candidate versions of a package across every eligible source, for resolving a dependency's
    /// declared version range to a concrete version. Includes UNLISTED versions: a package legitimately pins an
    /// exact dependency version that its publisher has unlisted (e.g. Windows App SDK experimental builds unlist
    /// their <c>.Runtime</c>/<c>.Foundation</c> sub-packages), and such a pinned dependency must still resolve —
    /// unlike a "latest version" decision, which excludes unlisted versions on purpose. Unlike
    /// <see cref="GetListedVersionsAsync"/> — which feeds a "latest" MAX decision and therefore fails closed if
    /// any source errors — this tolerates a per-source failure and moves on, matching the source-by-source
    /// failover the dependency paths already use: another eligible source may still offer a version that
    /// satisfies the range. The last such failure is still reported back so the caller can surface it when NO
    /// candidate satisfies the range (rather than masking a feed/authentication error as a silent skip).
    /// </summary>
    private async Task<CandidateVersionsResult> GetCandidateVersionsForRangeAsync(string packageId, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var versions = new HashSet<NuGetVersion>();
        Exception? lastError = null;
        string? lastErrorSource = null;

        var repos = _sourceProvider.GetRepositoriesForPackage(packageId);
        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Include unlisted versions: a dependency can pin an exact version its publisher has unlisted
                // (e.g. Windows App SDK experimental .Runtime/.Foundation sub-packages), and that pin must
                // still resolve. Unlisted only means "hidden from search/latest", not unavailable. A source
                // that exposes only PackageBaseAddress (no registration resource) is enumerated via its flat
                // container rather than skipped, so ranges still resolve against such feeds.
                foreach (var version in await GetSourceVersionsAsync(repo, packageId, includeUnlisted: true, cacheContext, cancellationToken))
                {
                    versions.Add(version);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Propagate genuine cancellation instead of masking it as "no versions found".
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort: a source we cannot query contributes no versions; another eligible source may
                // still satisfy the range (the dependency paths already fail over source-by-source). Remember
                // the failure so the caller can distinguish it from a clean "no satisfying version" when
                // nothing ends up matching.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
            }
        }

        return new CandidateVersionsResult([.. versions], lastError, lastErrorSource, repos.Count);
    }

    /// <summary>
    /// Enumerates a single source's versions of a package. Prefers the registration-backed
    /// <see cref="PackageMetadataResource"/> so <paramref name="includeUnlisted"/> is honored (the "latest"
    /// path relies on excluding unlisted versions, the dependency-range path on including them). A v3 HTTP
    /// feed that exposes only <c>PackageBaseAddress</c> advertises no registration resource, yet still hands
    /// back a non-functional <see cref="PackageMetadataResource"/> whose query throws — so this probes the
    /// service index first and, when no registration resource is advertised, reads versions from the
    /// flat-container <see cref="FindPackageByIdResource"/> instead, which every v3 source must support. That
    /// keeps latest/range resolution working against such private feeds (they can already restore pinned
    /// packages). Local/v2 feeds have no service index but a working metadata resource, so they keep using it.
    /// The flat container carries no listed flag, so the fallback cannot filter unlisted versions — acceptable
    /// because a feed without registration exposes no listed/unlisted signal at all. Cancellation is
    /// propagated; any other source failure is left to the caller's fail-closed / best-effort handling.
    /// </summary>
    private static async Task<IReadOnlyList<NuGetVersion>> GetSourceVersionsAsync(
        SourceRepository repo,
        string packageId,
        bool includeUnlisted,
        SourceCacheContext cacheContext,
        CancellationToken cancellationToken)
    {
        // Only a v3 HTTP source has a service index; when it advertises no registration resource its
        // PackageMetadataResource is non-functional (GetMetadataAsync throws), so route it to the flat
        // container. A null service index means a local/v2 feed, whose metadata resource works — keep it.
        // Probe with NuGet's own public, ordered registration service-type list so every registration shape —
        // including RegistrationsBaseUrl/Versioned and any future types NuGet adds — is recognized. A
        // hand-maintained subset could omit an advertised type (e.g. .../Versioned), misclassify a
        // registration-backed feed as flat-container-only, and let an unlisted version be picked as latest.
        var serviceIndex = await repo.GetResourceAsync<ServiceIndexResourceV3>(cancellationToken);
        var registrationUnavailable = serviceIndex is not null
            && serviceIndex.GetServiceEntryUri(ServiceTypes.RegistrationsBaseUrl) is null;

        if (!registrationUnavailable)
        {
            var metadataResource = await repo.GetResourceAsync<PackageMetadataResource>(cancellationToken);
            if (metadataResource is not null)
            {
                var metadata = await metadataResource.GetMetadataAsync(
                    packageId,
                    includePrerelease: true,
                    includeUnlisted,
                    cacheContext,
                    Logger,
                    cancellationToken);
                return [.. metadata.Select(m => m.Identity.Version)];
            }
        }

        // No registration resource (a PackageBaseAddress-only feed): enumerate versions from the flat
        // container, which every v3 source exposes, so such a feed still resolves latest/range versions
        // instead of contributing nothing.
        var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
        if (byIdResource is null)
        {
            return [];
        }

        var allVersions = await byIdResource.GetAllVersionsAsync(packageId, cacheContext, Logger, cancellationToken);
        return allVersions is null ? [] : [.. allVersions];
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        // Scope the process-wide cache to the effective configuration (feeds/global folder/mapping), not just
        // package/version: dependency results depend on the configured sources, so a bare package/version key
        // would let a lookup after SetConfigRoot (or another service instance with a different private feed)
        // return dependencies resolved against the previous source hierarchy.
        var cacheKey = $"{_sourceProvider.ConfigScopeKey}\n{packageName}/{version}";
        if (DependencyCache.TryGetValue(cacheKey, out var cached))
        {
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        }

        NugetSourceProvider.EnsureCredentialService();
        using var cacheContext = new SourceCacheContext();
        var directDeps = await FetchDirectDependenciesAsync(packageName, version, cacheContext, cancellationToken);

        // Recursively resolve transitive dependencies
        var allDeps = new Dictionary<string, string>(directDeps, StringComparer.OrdinalIgnoreCase);
        foreach (var (depId, depVersion) in directDeps)
        {
            var transitiveDeps = await GetPackageDependenciesAsync(depId, depVersion, cancellationToken);
            foreach (var (transitiveId, transitiveVersion) in transitiveDeps)
            {
                allDeps.TryAdd(transitiveId, transitiveVersion);
            }
        }

        DependencyCache[cacheKey] = allDeps;
        return new Dictionary<string, string>(allDeps, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<string, string>> FetchDirectDependenciesAsync(string packageName, string version, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nugetVersion = ParseVersion(packageName, version);
        var repos = _sourceProvider.GetRepositoriesForPackage(packageName);
        Exception? lastError = null;
        string? lastErrorSource = null;

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FindPackageByIdDependencyInfo? dependencyInfo;
            try
            {
                // Acquiring the resource loads the source's service index, which can throw for an
                // unreachable/unauthorized source; keep it inside the try so we fail over instead.
                var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                if (byIdResource is null)
                {
                    continue;
                }

                dependencyInfo = await byIdResource.GetDependencyInfoAsync(packageName, nugetVersion, cacheContext, Logger, cancellationToken);
            }
            catch (FatalProtocolException ex)
            {
                // A canceled nuspec fetch can be surfaced as a PackageNotFoundProtocolException (a
                // FatalProtocolException) once retries are exhausted; preserve cancellation instead of
                // recording it as a source failure and later throwing InvalidOperationException. This
                // matches the contract enforced in GetListedVersionsAsync.
                cancellationToken.ThrowIfCancellationRequested();

                // Source unreachable/unauthorized; remember why and try the next one.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
                continue;
            }

            if (dependencyInfo is null)
            {
                // This source responded that it does not have the requested version; try the next one.
                continue;
            }

            foreach (var group in dependencyInfo.DependencyGroups)
            {
                foreach (var dependency in group.Packages)
                {
                    if (string.IsNullOrEmpty(dependency.Id)
                        || IgnoredDependencyPrefixes.Any(p => dependency.Id.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // A bounded range that cannot be resolved now throws (surfacing loudly here so the graph
                    // is never reported complete with a required package missing); a null result means only a
                    // version-less / unbounded dependency, which is skipped.
                    var resolvedVersion = await ResolveDependencyVersionAsync(dependency.Id, dependency.VersionRange, cacheContext, cancellationToken);
                    if (!string.IsNullOrEmpty(resolvedVersion))
                    {
                        dependencies.TryAdd(dependency.Id, resolvedVersion);
                    }
                }
            }

            // Dependencies resolved from the first source that has the package.
            return dependencies;
        }

        // No source produced dependency metadata. If at least one source failed with a protocol error
        // (unreachable/401/403/network), surface it rather than returning an empty graph: a caller would
        // otherwise treat "no dependencies" as success and silently skip installing missing transitive
        // packages. An empty result is only returned when every source cleanly reported the package absent.
        if (lastError != null)
        {
            throw new InvalidOperationException(
                $"Failed to resolve dependencies for {packageName} {version} from the configured NuGet sources. Last error from source '{lastErrorSource}': {lastError.Message}",
                lastError);
        }

        // No source was even eligible. Fail closed (matching the download path) instead of reporting a
        // dependency-free graph, which a caller would treat as success while required transitive packages
        // remain uninstalled. The reason is either an empty feed list or a packageSourceMapping exclusion.
        if (repos.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot resolve dependencies for {packageName} {version}: {_sourceProvider.DescribeNoEligibleSources(packageName)}.");
        }

        return dependencies;
    }

    public static int CompareVersions(string a, string b)
    {
        // Prefer correct NuGet SemVer 2.0 ordering whenever both inputs parse as NuGet versions. This
        // accounts for prerelease tags (e.g. 1.0.0-preview1 < 1.0.0-preview2 < 1.0.0), which the plain
        // numeric-segment comparison below cannot distinguish (it parses tags as 0, making them equal).
        if (NuGetVersion.TryParse(a, out var va) && NuGetVersion.TryParse(b, out var vb))
        {
            return va.CompareTo(vb);
        }

        // Fallback for inputs that are not valid NuGet versions: compare numeric segments.
        var ap = a.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        var bp = b.Split('.', '-', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            int ai = i < ap.Length && int.TryParse(ap[i], out var av) ? av : 0;
            int bi = i < bp.Length && int.TryParse(bp[i], out var bv) ? bv : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }
        return 0;
    }
}
