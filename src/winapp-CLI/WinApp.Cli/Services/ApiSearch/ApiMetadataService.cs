// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Services;

/// <summary>
/// A caller-supplied scope for an API metadata query: an optional project
/// directory (defaults to the current directory) and an optional project name
/// used to disambiguate when several projects are indexed.
/// </summary>
internal readonly record struct ApiRequestScope(string? ProjectDir, string? Project);

/// <summary>
/// The winapp-native façade over the ported API-metadata engine that backs
/// <c>winapp find-api</c>. It owns cache-directory resolution (under the managed
/// global <c>.winapp</c> directory), lazy auto-indexing when a project's
/// <c>project.assets.json</c> is newer than the cached manifest, project-manifest
/// resolution (honoring <c>--project</c>/<c>--project-dir</c>), and forwards to
/// <see cref="ApiQueryEngine"/>. Query methods never write to stdout — they
/// return <see cref="ApiQueryResult{T}"/> so the command renders text or JSON.
/// </summary>
internal interface IApiMetadataService
{
    ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, ApiRequestScope scope);

    ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope);

    ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope);

    ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope);

    ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope);

    ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope);

    ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope);

    ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope);

    ApiProjectsOutput Projects();

    ApiRefreshOutput Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false);
}

internal sealed class ApiMetadataService(
    IWinappDirectoryService directoryService,
    ICurrentDirectoryProvider currentDirectory,
    ISdkPackageSource sdkPackages,
    ILogger<ApiMetadataService> logger) : IApiMetadataService
{
    private string GetCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "find-api");

    public ApiQueryResult<ApiSearchOutput> Search(string query, int maxResults, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Search(query, maxResults, cacheDir, manifest));

    public ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Members(fullName, cacheDir, manifest));

    public ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.CheckProperty(typeName, propertyName, cacheDir, manifest));

    public ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Types(ns, cacheDir, manifest));

    public ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Enums(fullName, cacheDir, manifest));

    public ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Namespaces(filter, cacheDir, manifest));

    public ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Packages(cacheDir, manifest));

    public ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Stats(cacheDir, manifest));

    public ApiProjectsOutput Projects() => ApiQueryEngine.Projects(GetCacheDir());

    public ApiRefreshOutput Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false)
    {
        string cacheDir = GetCacheDir();
        string? runtimePath = ApiCacheBuilder.DetectWinAppSdkRuntime();

        // 'refresh --project sdk' rebuilds the machine-wide scope explicitly (the
        // only way to pick up a newly installed Windows SDK / WinAppSDK runtime).
        if (scope.Project is not null && IsSdkScopeName(scope.Project))
        {
            return ApiCacheBuilder.BuildSdkCache(cacheDir, sdkPackages.GetSdkPackages(), onProgress, force: true);
        }

        string projectDir = ResolveRefreshProjectDir(scope, cacheDir);
        ApiRefreshOutput output = ApiCacheBuilder.BuildCache(projectDir, cacheDir, scan, runtimePath, onProgress, force);

        // Nothing to index here means there is no project in this directory, which is
        // exactly when queries fall back to the SDK scope — so build that instead of
        // leaving the user with an index they can't query.
        if (output.ProjectsProcessed == 0)
        {
            return ApiCacheBuilder.BuildSdkCache(cacheDir, sdkPackages.GetSdkPackages(), onProgress, force);
        }
        return output;
    }

    /// <summary>
    /// Resolve the directory to (re)index for a refresh. An explicit
    /// <c>--project</c> name refreshes that indexed project's recorded directory;
    /// otherwise the <c>--project-dir</c> (or current directory) is used.
    /// </summary>
    private string ResolveRefreshProjectDir(ApiRequestScope scope, string cacheDir)
    {
        if (scope.Project is not null)
        {
            string projectsDir = Path.Combine(cacheDir, "projects");
            if (Directory.Exists(projectsDir))
            {
                foreach (string path in Directory.GetFiles(projectsDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.Equals(scope.Project, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(scope.Project + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        ProjectManifest? manifest = DeserializeManifest(path);
                        if (manifest is not null && !string.IsNullOrEmpty(manifest.ProjectDir))
                        {
                            return manifest.ProjectDir;
                        }
                    }
                }
            }
        }
        return ResolveProjectDir(scope);
    }

    /// <summary>
    /// Shared read path for every query verb: ensure the cache is fresh, resolve
    /// the project manifest, and either forward to the engine or return a
    /// <see cref="ApiQueryOutcome.NoProject"/> result with actionable guidance.
    /// </summary>
    private ApiQueryResult<T> WithManifest<T>(ApiRequestScope scope, Func<string, ProjectManifest, ApiQueryResult<T>> query)
        where T : class
    {
        string cacheDir = GetCacheDir();
        AutoIndexIfStale(scope, cacheDir);
        ResolvedScope resolved = ResolveManifest(scope, cacheDir);
        if (resolved.Manifest is null)
        {
            return ApiQueryResult<T>.NoProject(resolved.Error ?? NoProjectMessage);
        }

        ApiQueryResult<T> result = query(cacheDir, resolved.Manifest);

        // Stamp the scope centrally so every verb reports it identically and no
        // payload can silently omit which source answered.
        if (result.Data is IApiScopedOutput scoped)
        {
            scoped.Scope = resolved.IsSdk ? ApiScopeNames.Sdk : ApiScopeNames.Project;
        }
        return result;
    }

    private string ResolveProjectDir(ApiRequestScope scope) =>
        Path.GetFullPath(scope.ProjectDir ?? currentDirectory.GetCurrentDirectory());

    /// <summary>
    /// Re-index the project when its restore output (<c>project.assets.json</c>)
    /// is newer than the cached manifest, or no manifest exists yet. A file lock
    /// under the cache dir serializes concurrent winapp invocations so two
    /// processes don't index into the same tree at once.
    /// </summary>
    private void AutoIndexIfStale(ApiRequestScope scope, string cacheDir)
    {
        string projectDir = ResolveProjectDir(scope);
        string? assetsPath = NuGetResolver.FindProjectAssetsJson(projectDir);
        if (assetsPath is null)
        {
            return;
        }

        DateTime assetsWriteTime = File.GetLastWriteTimeUtc(assetsPath);
        string projectsDir = Path.Combine(cacheDir, "projects");
        bool needsUpdate = false;

        if (!Directory.Exists(projectsDir))
        {
            needsUpdate = true;
        }
        else
        {
            string? projectName = ApiCacheBuilder.FindProjectNameInDir(projectDir);
            if (projectName is not null)
            {
                bool found = false;
                foreach (string manifestPath in Directory.GetFiles(projectsDir, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(manifestPath);
                    if (name.Equals(projectName, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(projectName + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        if (assetsWriteTime > File.GetLastWriteTimeUtc(manifestPath))
                        {
                            needsUpdate = true;
                        }
                        break;
                    }
                }
                if (!found)
                {
                    needsUpdate = true;
                }
            }
        }

        if (needsUpdate)
        {
            RunIndexWithLock(scope, cacheDir);
        }
    }

    private void RunIndexWithLock(ApiRequestScope scope, string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        string lockPath = Path.Combine(cacheDir, ".lock");

        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another winapp process holds the lock and is indexing the same cache.
            // Wait briefly for it to finish, then fall through to querying whatever
            // is on disk. This contention path must NOT wrap the indexing work
            // below, or a genuine I/O failure during Refresh would be misreported
            // as lock contention (and trigger a needless 30s wait).
            logger.LogInformation("API metadata cache is being indexed by another process, waiting…");
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);
                try
                {
                    using var probe = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException)
                {
                }
            }
            return;
        }

        using (lockFile)
        {
            try
            {
                logger.LogInformation("Indexing API metadata for this project…");
                Refresh(scope, scan: false, onProgress: msg => logger.LogInformation("{Message}", msg));
            }
            catch (Exception ex)
            {
                // Auto-indexing is best-effort: log and let the caller query the
                // existing (possibly stale/empty) cache rather than failing hard.
                logger.LogWarning(ex, "Failed to index API metadata; continuing with the existing cache.");
            }
        }
    }

    /// <summary>
    /// Resolve the scope for the request: an explicit <c>--project</c> name (or the
    /// reserved SDK scope name), then a <c>--project-dir</c> match (by recorded dir,
    /// then by discovered project name), then the project in the current directory.
    /// When the current directory holds no project at all the machine-wide SDK scope
    /// answers instead — deliberately regardless of how many projects are cached, so
    /// the result never depends on unrelated global state. Returns a human-readable
    /// error when resolution is impossible.
    /// </summary>
    private ResolvedScope ResolveManifest(ApiRequestScope scope, string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        string[] files = Directory.Exists(projectsDir)
            ? Directory.GetFiles(projectsDir, "*.json")
            : [];

        if (scope.Project is not null)
        {
            if (IsSdkScopeName(scope.Project))
            {
                return ResolveSdkScope(cacheDir);
            }
            foreach (string path in files)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Equals(scope.Project, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(scope.Project + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return ResolvedScope.Project(DeserializeManifest(path));
                }
            }
            return ResolvedScope.Failed(files.Length == 0
                ? $"Project '{scope.Project}' is not indexed — no projects are indexed yet. Run 'winapp find-api refresh' in the project directory, or use '--project sdk' for the Windows SDK scope."
                : $"Project '{scope.Project}' is not indexed. Run 'winapp find-api refresh' in the project directory, or pick from: {AvailableProjects(files)}.");
        }

        if (scope.ProjectDir is not null)
        {
            string fullPath = Path.GetFullPath(scope.ProjectDir);
            foreach (string path in files)
            {
                ProjectManifest? manifest = DeserializeManifest(path);
                if (manifest is not null && manifest.ProjectDir.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return ResolvedScope.Project(manifest);
                }
            }
            string? dirProjectName = ApiCacheBuilder.FindProjectNameInDir(fullPath);
            if (dirProjectName is not null)
            {
                foreach (string path in files)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.Equals(dirProjectName, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(dirProjectName + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        return ResolvedScope.Project(DeserializeManifest(path));
                    }
                }

                // An explicit --project-dir named a real project that isn't indexed.
                // Do NOT quietly widen to the SDK scope — the caller asked about that
                // project specifically, and its NuGet packages would be missing.
                return ResolvedScope.Failed($"No indexed API metadata was found for '{fullPath}'. Restore the project (so 'project.assets.json' exists), " +
                    $"then run 'winapp find-api refresh' in that directory.{(files.Length == 0 ? "" : $" Indexed projects: {AvailableProjects(files)}.")}");
            }

            // --project-dir pointed at a directory with no project in it: same
            // situation as running from a projectless directory, so answer from the
            // SDK scope rather than erroring.
            return ResolveSdkScope(cacheDir);
        }

        string? currentName = ApiCacheBuilder.FindProjectNameInDir(currentDirectory.GetCurrentDirectory());
        if (currentName is not null)
        {
            foreach (string path in files)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Equals(currentName, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(currentName + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return ResolvedScope.Project(DeserializeManifest(path));
                }
            }
            return ResolvedScope.Failed(NoProjectMessage);
        }

        // No project here. Answer from the machine-wide SDK scope. Note this does not
        // consult the cached project list at all: a query from a projectless directory
        // must not change meaning just because some unrelated project was indexed.
        return ResolveSdkScope(cacheDir);
    }

    private static bool IsSdkScopeName(string name) =>
        name.Equals(ApiScopeNames.Sdk, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(ApiCachePaths.SdkScopeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads the SDK-scope manifest, building it on first use. Indexing the Windows
    /// SDK is the same work a project index does for its SDK packages, so a warm
    /// project cache usually makes this a no-op reuse.
    /// </summary>
    private ResolvedScope ResolveSdkScope(string cacheDir)
    {
        string manifestPath = ApiCachePaths.SdkManifestPath(cacheDir);
        ProjectManifest? manifest = File.Exists(manifestPath) ? DeserializeManifest(manifestPath) : null;
        if (manifest is not null)
        {
            return ResolvedScope.Sdk(manifest);
        }

        try
        {
            List<PackageWithWinMd> packages = sdkPackages.GetSdkPackages();
            if (packages.Count == 0)
            {
                return ResolvedScope.Failed(NoSdkMessage);
            }
            logger.LogInformation("No project here — indexing {Scope} metadata…", ApiCachePaths.SdkScopeName);
            ApiCacheBuilder.BuildSdkCache(cacheDir, packages, onProgress: msg => logger.LogInformation("{Message}", msg));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to index Windows SDK metadata.");
            return ResolvedScope.Failed(NoSdkMessage);
        }

        manifest = File.Exists(manifestPath) ? DeserializeManifest(manifestPath) : null;
        return manifest is not null ? ResolvedScope.Sdk(manifest) : ResolvedScope.Failed(NoSdkMessage);
    }

    private static string AvailableProjects(IEnumerable<string> files) =>
        string.Join(", ", files.Select(Path.GetFileNameWithoutExtension));

    private static ProjectManifest? DeserializeManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ApiSearchJsonContext.Default.ProjectManifest);
        }
        catch
        {
            return null;
        }
    }

    private const string NoProjectMessage =
        "No indexed API metadata was found for this project. Restore the project (so 'project.assets.json' exists), " +
        "then run 'winapp find-api refresh' in the project directory to build the API index.";

    private const string NoSdkMessage =
        "No project was found here and no Windows SDK metadata is available on this machine. " +
        "Run 'winapp find-api' from a project directory, or install the Windows SDK / Windows App SDK.";

    /// <summary>
    /// A resolved query scope: the manifest to read plus whether it came from the
    /// machine-wide SDK scope (which excludes project NuGet packages) so callers
    /// can label the answer.
    /// </summary>
    private readonly record struct ResolvedScope(ProjectManifest? Manifest, string? Error, bool IsSdk)
    {
        public static ResolvedScope Project(ProjectManifest? manifest) => manifest is null
            ? Failed(NoProjectMessage)
            : new ResolvedScope(manifest, null, false);

        public static ResolvedScope Sdk(ProjectManifest manifest) => new(manifest, null, true);

        public static ResolvedScope Failed(string error) => new(null, error, false);
    }
}
