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
    private static int _credentialServiceInitialized;

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
    /// plugins. Interactive prompting is only enabled for real interactive terminals.
    /// </summary>
    private static void EnsureCredentialService()
    {
        if (Interlocked.Exchange(ref _credentialServiceInitialized, 1) != 0)
        {
            return;
        }

        var nonInteractive = Console.IsInputRedirected
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD"));

        DefaultCredentialServiceUtility.SetupDefaultCredentialService(Logger, nonInteractive);
    }

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

        var packageDir = GetNuGetPackageDir(package, version);

        // Already installed on disk?
        if (packageDir.Exists)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Skip} {package} {version} already present");
            installed[package] = version;
            // Still resolve dependencies to populate installed dictionary
            await ResolveDependenciesAsync(packageDir, package, version, installed, taskContext, cacheContext, cancellationToken);
            return;
        }

        // Download and extract the package from the user's configured NuGet sources into the
        // global packages folder (using the standard NuGet on-disk layout). Throws with the
        // underlying source error if no configured source can provide the package.
        await DownloadPackageAsync(package, version, cacheContext, cancellationToken);

        installed[package] = version;
        taskContext.AddStatusMessage($"{UiSymbols.Check} Installed {package} {version}");

        // Recursively install dependencies
        await ResolveDependenciesAsync(packageDir, package, version, installed, taskContext, cacheContext, cancellationToken);
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

                        copied = await byIdResource.CopyNupkgToStreamAsync(identity.Id, identity.Version, fileStream, cacheContext, Logger, cancellationToken);
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
            ? $"Failed to download {package} {version}: no configured NuGet source is mapped to this package (check <packageSourceMapping> in nuget.config)."
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
        try
        {
            var deps = ReadDependenciesFromNuspec(packageDir, package);
            foreach (var (depName, depVersionRange) in deps)
            {
                if (installed.ContainsKey(depName))
                {
                    continue;
                }

                var depVersion = depVersionRange.MinVersion?.ToNormalizedString();
                if (!string.IsNullOrEmpty(depVersion))
                {
                    await InstallPackageRecursiveAsync(depName, depVersion, installed, taskContext, cacheContext, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            // Dependency resolution failures are non-fatal; the main package is installed.
            // Log so transitive dependency issues are visible in verbose/debug output.
            taskContext.AddDebugMessage($"{UiSymbols.Note} Dependency resolution for {package} {version}: {ex.Message}");
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
                if (!string.IsNullOrEmpty(dependency.Id) && dependency.VersionRange != null)
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
            throw new InvalidOperationException($"No versions found for {packageName}");
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
                // resolving the package. Remember the error in case no source yields a version.
                lastError = ex;
            }
        }

        if (versions.Count == 0 && lastError != null)
        {
            throw new InvalidOperationException($"No versions found for {packageName}", lastError);
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

        foreach (var repo in GetRepositoriesForPackage(packageName))
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
            catch (FatalProtocolException)
            {
                // Source unreachable/unauthorized; try the next one.
                continue;
            }

            if (dependencyInfo is null)
            {
                // This source does not have the requested version; try the next one.
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

        return dependencies;
    }

    public static int CompareVersions(string a, string b)
    {
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
