// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
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
/// private/custom feeds and mirrors are honored when restoring SDK packages.
/// </summary>
internal class NugetService(
    IWinappDirectoryService winappDirectoryService,
    ICurrentDirectoryProvider currentDirectoryProvider) : INugetService
{
    private static readonly ILogger Logger = NullLogger.Instance;
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> DependencyCache = new(StringComparer.OrdinalIgnoreCase);

    // Lazy (ExecutionAndPublication) guarantees the credential-service setup runs exactly once AND that
    // every caller blocks until it has fully completed. Publishing an "initialized" flag before setup
    // finished (e.g. via Interlocked.Exchange) would let a concurrent NuGet operation build its HTTP
    // resources against a not-yet-configured credential service and hit a private feed anonymously.
    private static readonly Lazy<bool> CredentialServiceInitializer = new(() =>
    {
        var nonInteractive = Console.IsInputRedirected
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

        DefaultCredentialServiceUtility.SetupDefaultCredentialService(Logger, nonInteractive);
        return true;
    });

    private readonly Lazy<ISettings> _settings = new(() =>
        NuGet.Configuration.Settings.LoadDefaultSettings(root: currentDirectoryProvider.GetCurrentDirectory()));

    private SourceRepositoryProvider? _cachedSourceRepositoryProvider;
    private PackageSourceMapping? _cachedPackageSourceMapping;

    private ISettings Settings => _settings.Value;

    private IReadOnlyList<SourceRepository> GetRepositories()
    {
        _cachedSourceRepositoryProvider ??= new SourceRepositoryProvider(
            new PackageSourceProvider(Settings),
            Repository.Provider.GetCoreV3());
        return [.. _cachedSourceRepositoryProvider.GetRepositories()];
    }

    private PackageSourceMapping PackageSourceMapping =>
        _cachedPackageSourceMapping ??= PackageSourceMapping.GetPackageSourceMapping(Settings);

    /// <summary>
    /// Returns the configured package sources eligible to serve <paramref name="packageId"/>, honoring
    /// <c>&lt;packageSourceMapping&gt;</c> when it is enabled. When mapping is enabled but no source is
    /// mapped to the package, an empty list is returned (matching NuGet restore semantics, which fails
    /// rather than falling back to an unmapped feed).
    /// </summary>
    internal IReadOnlyList<SourceRepository> GetRepositoriesForPackage(string packageId)
    {
        var repositories = GetRepositories();

        var mapping = PackageSourceMapping;
        if (!mapping.IsEnabled)
        {
            return repositories;
        }

        var mappedSources = mapping.GetConfiguredPackageSources(packageId);
        if (mappedSources is null || mappedSources.Count == 0)
        {
            return [];
        }

        var allowed = new HashSet<string>(mappedSources, StringComparer.OrdinalIgnoreCase);
        return [.. repositories.Where(r => allowed.Contains(r.PackageSource.Name))];
    }

    /// <summary>
    /// Explains why no source was eligible to serve <paramref name="packageId"/>, distinguishing the
    /// distinct causes — no sources configured at all, the package matching no
    /// <c>&lt;packageSourceMapping&gt;</c> pattern, or the package being mapped to a source that is
    /// disabled/missing — so the error points the user at the right nuget.config fix.
    /// </summary>
    private string DescribeNoEligibleSources(string packageId)
    {
        // If there are no enabled sources at all, mapping is irrelevant — an empty eligible set can only
        // mean the feed list itself is empty, regardless of whether packageSourceMapping is enabled.
        if (GetRepositories().Count == 0)
        {
            return "no enabled NuGet sources are configured (add or enable a source in the <packageSources> section of your nuget.config)";
        }

        // Sources exist, so packageSourceMapping is what pruned them. Separate "the package matches no
        // mapping pattern" from "the package is mapped, but to a source that isn't enabled/configured"
        // (e.g. the mapped key names a disabled or misspelled source) — the fixes are different.
        var mappedSources = PackageSourceMapping.GetConfiguredPackageSources(packageId);
        if (mappedSources is null || mappedSources.Count == 0)
        {
            return $"no <packageSourceMapping> pattern maps '{packageId}' to a source (add a matching entry in nuget.config)";
        }

        var mapped = string.Join(", ", mappedSources);
        return $"'{packageId}' is mapped to source(s) [{mapped}] that are not enabled/configured (enable or fix the mapped source in the <packageSources> section of your nuget.config)";
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

    /// <summary>
    /// Configures NuGet's default credential service so authenticated (private) feeds work using
    /// credentials stored in nuget.config, environment-based credentials, or credential-provider
    /// plugins. Interactive prompting is only enabled for real interactive terminals. Setup runs
    /// exactly once and every caller blocks until it has completed, so concurrent NuGet operations
    /// never observe a half-initialized credential service.
    /// </summary>
    private static void EnsureCredentialService() => _ = CredentialServiceInitializer.Value;

    public DirectoryInfo GetNuGetGlobalPackagesDir()
    {
        // In test mode (cache override set), use a "packages" subdir of the override directory
        var globalDir = winappDirectoryService.GetGlobalWinappDirectory();
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
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(Settings);
        var nugetDir = new DirectoryInfo(globalPackagesFolder);
        if (!nugetDir.Exists)
        {
            nugetDir.Create();
        }
        return nugetDir;
    }

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
    {
        var cache = GetNuGetGlobalPackagesDir();
        // Resolve the on-disk folder the same way the global-packages writer does, so the path matches
        // regardless of how the version string is expressed (NuGet stores e.g. "1.0" under "1.0.0").
        if (NuGetVersion.TryParse(version, out var parsed))
        {
            var resolver = new VersionFolderPathResolver(cache.FullName);
            return new DirectoryInfo(resolver.GetInstallPath(packageName, parsed));
        }

        return new DirectoryInfo(Path.Combine(cache.FullName, packageName.ToLowerInvariant(), version.ToLowerInvariant()));
    }

    /// <summary>
    /// Normalizes a version string to NuGet's canonical form (e.g. "1.0" -> "1.0.0") so the value stored
    /// and returned by the installer matches the on-disk global-packages folder layout. Returns the input
    /// unchanged if it is not a parseable NuGet version.
    /// </summary>
    internal static string NormalizeVersion(string version) =>
        NuGetVersion.TryParse(version, out var parsed) ? parsed.ToNormalizedString() : version;

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
        EnsureCredentialService();
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cacheContext = new SourceCacheContext();
        await InstallPackageRecursiveAsync(package, version, packages, taskContext, cacheContext, cancellationToken);
        return packages;
    }

    /// <summary>
    /// Downloads and extracts a NuGet package to the global packages cache, then recursively installs dependencies.
    /// </summary>
    private async Task InstallPackageRecursiveAsync(string package, string version, Dictionary<string, string> installed, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
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
            await ResolveDependenciesAsync(packageDir, package, normalizedVersion, installed, taskContext, cacheContext, cancellationToken);
            return;
        }

        // Download and extract the package from the user's configured NuGet sources into the
        // global packages folder (using the standard NuGet on-disk layout). Throws with the
        // underlying source error if no configured source can provide the package.
        await DownloadPackageAsync(package, normalizedVersion, cacheContext, cancellationToken);

        installed[package] = normalizedVersion;
        taskContext.AddStatusMessage($"{UiSymbols.Check} Installed {package} {normalizedVersion}");

        // Recursively install dependencies
        await ResolveDependenciesAsync(packageDir, package, normalizedVersion, installed, taskContext, cacheContext, cancellationToken);
    }

    /// <summary>
    /// Downloads a package from the first configured source that has it and extracts it into the
    /// global packages folder. Honors <c>&lt;packageSourceMapping&gt;</c> for source selection and
    /// throws an <see cref="InvalidOperationException"/> (preserving the underlying source error) when
    /// no configured source can provide the package.
    /// </summary>
    private async Task DownloadPackageAsync(string package, string version, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var identity = new PackageIdentity(package, NuGetVersion.Parse(version));
        var globalPackagesFolder = GetNuGetGlobalPackagesDir().FullName;
        var clientPolicyContext = ClientPolicyContext.GetClientPolicy(Settings, Logger);

        var repos = GetRepositoriesForPackage(package);
        Exception? lastError = null;
        string? lastErrorSource = null;

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Buffer to a temp file rather than memory: SDK packages (e.g. Windows App SDK) are large.
            var tempFile = Path.GetTempFileName();
            try
            {
                bool copied;

                // Capture warning/error diagnostics for this source. CopyNupkgToStreamAsync reports content
                // failures (e.g. a 401/403 on the .nupkg endpoint) through the logger and can then return
                // false instead of throwing; with NullLogger that detail would be lost and the failure
                // misreported as "not found".
                var downloadLogger = new CollectingLogger();
                await using (var fileStream = File.Create(tempFile))
                {
                    try
                    {
                        // Acquiring the resource loads the source's service index, which can throw for an
                        // unreachable/unauthorized source; keep it inside the try so we fail over instead.
                        var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                        if (byIdResource is null)
                        {
                            continue;
                        }

                        copied = await byIdResource.CopyNupkgToStreamAsync(identity.Id, identity.Version, fileStream, cacheContext, downloadLogger, cancellationToken);
                    }
                    catch (FatalProtocolException ex)
                    {
                        // Source unreachable/unauthorized or does not have this package; remember why
                        // (e.g. 401/403/network) and try the next source.
                        lastError = ex;
                        lastErrorSource = repo.PackageSource.Name;
                        continue;
                    }
                }

                if (!copied)
                {
                    // A false return covers both "this source doesn't have the package" (normal failover)
                    // and a content-endpoint failure (e.g. 401/403) that was retried and logged rather than
                    // thrown. Preserve any captured error so an auth/network failure isn't later reported as
                    // a plain "package/version was not found".
                    if (downloadLogger.LastErrorMessage is not null)
                    {
                        lastError = new InvalidOperationException(downloadLogger.LastErrorMessage);
                        lastErrorSource = repo.PackageSource.Name;
                    }
                    continue;
                }

                await using var readStream = File.OpenRead(tempFile);
                using var addResult = await GlobalPackagesFolderUtility.AddPackageAsync(
                    source: repo.PackageSource.Source,
                    packageIdentity: identity,
                    packageStream: readStream,
                    globalPackagesFolder: globalPackagesFolder,
                    parentId: Guid.Empty,
                    clientPolicyContext: clientPolicyContext,
                    logger: Logger,
                    token: cancellationToken);

                return;
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch
                {
                    // Best-effort cleanup of the temp download.
                }
            }
        }

        // No configured source could provide the package. Surface the underlying reason when we have it
        // so authentication/network failures are distinguishable from a genuinely missing package.
        var sources = string.Join(", ", repos.Select(r => r.PackageSource.Name));
        var baseMessage = string.IsNullOrEmpty(sources)
            ? $"Failed to download {package} {version}: {DescribeNoEligibleSources(package)}."
            : $"Failed to download {package} {version} from the configured NuGet sources ({sources}).";

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{baseMessage} Last error from source '{lastErrorSource}': {lastError.Message}", lastError);
        }

        throw new InvalidOperationException($"{baseMessage} The package/version was not found on any configured source.");
    }

    /// <summary>
    /// Reads the .nuspec from an extracted package and recursively installs dependencies.
    /// </summary>
    private async Task ResolveDependenciesAsync(DirectoryInfo packageDir, string package, string version, Dictionary<string, string> installed, TaskContext taskContext, SourceCacheContext cacheContext, CancellationToken cancellationToken)
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

            var depVersion = depVersionRange.MinVersion?.ToNormalizedString();
            if (string.IsNullOrEmpty(depVersion))
            {
                continue;
            }

            try
            {
                await InstallPackageRecursiveAsync(depName, depVersion, installed, taskContext, cacheContext, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Never mask a genuine cancellation as a successful (but incomplete) install.
                throw;
            }
            catch (Exception ex)
            {
                // A single transitive dependency that cannot be installed should not abort the whole
                // install, but the failure must be visible (not hidden behind verbose-only logging) so an
                // incomplete install is not silently reported as success.
                taskContext.AddStatusMessage($"{UiSymbols.Warning} Could not install dependency {depName} {depVersion} (required by {package} {version}): {ex.Message}");
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
    /// Fetches all listed (non-unlisted) versions of a package, aggregated across every enabled
    /// package source. Unlisted versions are excluded so they are never selected as "latest".
    /// </summary>
    private async Task<List<string>> GetListedVersionsAsync(string packageName, CancellationToken cancellationToken)
    {
        EnsureCredentialService();

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? lastError = null;
        string? lastErrorSource = null;
        using var cacheContext = new SourceCacheContext();

        foreach (var repo in GetRepositoriesForPackage(packageName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var metadataResource = await repo.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                if (metadataResource is null)
                {
                    continue;
                }

                var metadata = await metadataResource.GetMetadataAsync(
                    packageName,
                    includePrerelease: true,
                    includeUnlisted: false,
                    cacheContext,
                    Logger,
                    cancellationToken);

                foreach (var package in metadata)
                {
                    versions.Add(package.Identity.Version.ToNormalizedString());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Propagate genuine cancellation instead of masking it as "no versions found".
                throw;
            }
            catch (Exception ex)
            {
                // A single unreachable/unauthorized source should not prevent other sources from
                // resolving the package. Remember the error (and which source) in case no source
                // yields a version.
                lastError = ex;
                lastErrorSource = repo.PackageSource.Name;
            }
        }

        if (versions.Count == 0 && lastError != null)
        {
            // Inline the underlying source error in the message rather than only wrapping it: top-level
            // command handlers print ex.Message, so 401/403/network detail carried by the inner exception
            // would otherwise be invisible to the user.
            throw new InvalidOperationException(
                $"No versions found for {packageName}. Last error from source '{lastErrorSource}': {lastError.Message}",
                lastError);
        }

        return [.. versions];
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{packageName}/{version}";
        if (DependencyCache.TryGetValue(cacheKey, out var cached))
        {
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        }

        EnsureCredentialService();
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
        var nugetVersion = NuGetVersion.Parse(version);
        var repos = GetRepositoriesForPackage(packageName);
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

                    var minVersion = dependency.VersionRange?.MinVersion?.ToNormalizedString();
                    if (!string.IsNullOrEmpty(minVersion))
                    {
                        dependencies.TryAdd(dependency.Id, minVersion);
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
                $"Cannot resolve dependencies for {packageName} {version}: {DescribeNoEligibleSources(packageName)}.");
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

    /// <summary>
    /// An <see cref="ILogger"/> that captures the most recent warning/error message emitted by a NuGet
    /// operation. Used to recover the underlying reason (e.g. a 401/403 on a package-content endpoint)
    /// when an API such as <c>CopyNupkgToStreamAsync</c> reports failure by returning <c>false</c> and
    /// logging rather than throwing, so the failure is not later misreported as a plain "not found".
    /// </summary>
    private sealed class CollectingLogger : LoggerBase
    {
        public string? LastErrorMessage { get; private set; }

        public override void Log(ILogMessage message)
        {
            if (message.Level >= LogLevel.Warning)
            {
                LastErrorMessage = message.Message;
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
