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

    ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope, string? filter = null, bool includeAll = false);

    ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope);

    ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope);

    ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope, string? filter = null);

    ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope);

    ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope);

    ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope);

    ApiProjectsOutput Projects();

    ApiQueryResult<ApiRefreshOutput> Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false);
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

    public ApiQueryResult<ApiMembersOutput> Members(string fullName, ApiRequestScope scope, string? filter = null, bool includeAll = false) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Members(fullName, filter, cacheDir, manifest, includeAll));

    public ApiQueryResult<ApiCheckPropertyOutput> CheckProperty(string typeName, string propertyName, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.CheckProperty(typeName, propertyName, cacheDir, manifest));

    public ApiQueryResult<ApiTypesOutput> Types(string ns, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Types(ns, cacheDir, manifest));

    public ApiQueryResult<ApiEnumsOutput> Enums(string fullName, ApiRequestScope scope, string? filter = null) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Enums(fullName, filter, cacheDir, manifest));

    public ApiQueryResult<ApiNamespacesOutput> Namespaces(string? filter, ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Namespaces(filter, cacheDir, manifest));

    public ApiQueryResult<ApiPackagesOutput> Packages(ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Packages(cacheDir, manifest));

    public ApiQueryResult<ApiStatsOutput> Stats(ApiRequestScope scope) =>
        WithManifest(scope, (cacheDir, manifest) => ApiQueryEngine.Stats(cacheDir, manifest));

    public ApiProjectsOutput Projects() => ApiQueryEngine.Projects(GetCacheDir());

    public ApiQueryResult<ApiRefreshOutput> Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null, bool force = false)
    {
        string cacheDir = GetCacheDir();
        string? runtimePath = ApiCacheBuilder.DetectWinAppSdkRuntime();

        // 'refresh --project sdk' rebuilds the machine-wide scope explicitly (the
        // only way to pick up a newly installed Windows SDK / WinAppSDK runtime).
        if (scope.Project is not null && IsSdkScopeName(scope.Project))
        {
            return ApiQueryResult<ApiRefreshOutput>.Ok(
                ApiCacheBuilder.BuildSdkCache(cacheDir, sdkPackages.GetSdkPackages(), onProgress, force: true));
        }

        string projectDir;
        if (scope.Project is not null)
        {
            // A named project that resolves to nothing must fail: refreshing the
            // current directory instead would report success for a project the
            // caller never asked about.
            string? named = TryResolveNamedProjectDir(scope.Project, cacheDir);
            if (named is null)
            {
                return ApiQueryResult<ApiRefreshOutput>.InvalidInput(UnknownProjectMessage(scope.Project, cacheDir));
            }
            projectDir = named;
        }
        else
        {
            projectDir = ResolveProjectDir(scope);
        }

        ApiRefreshOutput output = ApiCacheBuilder.BuildCache(projectDir, cacheDir, scan, runtimePath, onProgress, force);

        // Nothing to index here means there is no project in this directory, which is
        // exactly when queries fall back to the SDK scope — so build that instead of
        // leaving the user with an index they can't query.
        if (output.ProjectsProcessed == 0)
        {
            return ApiQueryResult<ApiRefreshOutput>.Ok(
                ApiCacheBuilder.BuildSdkCache(cacheDir, sdkPackages.GetSdkPackages(), onProgress, force));
        }
        return ApiQueryResult<ApiRefreshOutput>.Ok(output);
    }

    /// <summary>
    /// Error text for a <c>--project</c> name that matches no indexed project, listing
    /// what is actually indexed so the caller can correct the name.
    /// </summary>
    private static string UnknownProjectMessage(string projectName, string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        string[] files = Directory.Exists(projectsDir) ? Directory.GetFiles(projectsDir, "*.json") : [];
        string names = AvailableProjects(files);
        string known = names.Length == 0
            ? "No projects are indexed yet."
            : $"Indexed projects: {names}.";
        return $"No single indexed project matches '{projectName}'. {known} " +
            "Run 'winapp find-api refresh' in the project directory, or pass --project-dir.";
    }

    /// <summary>
    /// Resolve an explicit <c>--project</c> name to the recorded directory of the one
    /// indexed project it names. Returns <c>null</c> when the name matches no indexed
    /// project or several in different directories, so callers fail loudly instead of
    /// silently acting on the current directory.
    /// </summary>
    private static string? TryResolveNamedProjectDir(string projectName, string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        if (!Directory.Exists(projectsDir))
        {
            return null;
        }

        var dirs = new List<string>();
        foreach (string path in Directory.GetFiles(projectsDir, "*.json"))
        {
            if (!IsManifestForProject(path, projectName))
            {
                continue;
            }
            ProjectManifest? manifest = DeserializeManifest(path);
            if (manifest is not null &&
                manifest.ProjectName.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(manifest.ProjectDir))
            {
                dirs.Add(Path.GetFullPath(manifest.ProjectDir));
            }
        }

        var distinct = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// Cheap pre-filter on the manifest file name, which is <c>ProjectName_hash</c>. It is a
    /// superset test only — <c>App</c> also prefixes <c>App_Tests_hash</c> — so every caller
    /// must still compare the deserialized <see cref="ProjectManifest.ProjectName"/>.
    /// </summary>
    private static bool IsManifestForProject(string manifestPath, string projectName)
    {
        string name = Path.GetFileNameWithoutExtension(manifestPath);
        return name.Equals(projectName, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(projectName + "_", StringComparison.OrdinalIgnoreCase);
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
        string? indexError = AutoIndexIfStale(scope, cacheDir);
        if (indexError is not null)
        {
            // Answering from a cache we know is stale or absent would look
            // authoritative while being wrong, so surface the failure instead.
            return ApiQueryResult<T>.NoProject(indexError);
        }
        ResolvedScope resolved = ResolveManifest(scope, cacheDir);
        if (resolved.Manifest is null)
        {
            return ApiQueryResult<T>.NoProject(resolved.Error ?? NoProjectMessage);
        }

        ApiQueryResult<T> result = query(cacheDir, resolved.Manifest);

        // Stamp scope and project identity centrally so every verb reports them
        // identically and no payload can silently omit which index answered.
        // Project names are not unique across directories, so the directory is
        // the only reliable identity — callers auditing which project served a
        // query must not have to infer it from cache timestamps.
        if (result.Data is IApiScopedOutput scoped)
        {
            scoped.Scope = resolved.IsSdk ? ApiScopeNames.Sdk : ApiScopeNames.Project;
            scoped.ProjectName = resolved.Manifest.ProjectName;
            scoped.ProjectDir = string.IsNullOrEmpty(resolved.Manifest.ProjectDir)
                ? null
                : resolved.Manifest.ProjectDir;
        }
        return result;
    }

    private string ResolveProjectDir(ApiRequestScope scope) =>
        Path.GetFullPath(scope.ProjectDir ?? currentDirectory.GetCurrentDirectory());

    /// <summary>
    /// Re-index the project when its restore output (<c>project.assets.json</c>)
    /// is newer than the cached manifest, or no manifest exists yet. A file lock
    /// under the cache dir serializes concurrent winapp invocations so two
    /// processes don't index into the same tree at once. Returns an error message
    /// when indexing was required but failed, so the caller can refuse to answer
    /// from a cache it knows is stale.
    /// </summary>
    private string? AutoIndexIfStale(ApiRequestScope scope, string cacheDir)
    {
        // The machine-wide SDK scope is rebuilt only by an explicit refresh; it has
        // no project.assets.json to stale-check against.
        if (scope.Project is not null && IsSdkScopeName(scope.Project))
        {
            return null;
        }

        // A named project is stale-checked against its own recorded directory. Using
        // the current directory here would refresh an unrelated project and leave the
        // requested one stale. An unresolvable name is left to ResolveManifest, which
        // reports the ambiguity or the unknown name properly.
        string projectDir;
        if (scope.Project is not null)
        {
            string? named = TryResolveNamedProjectDir(scope.Project, cacheDir);
            if (named is null)
            {
                return null;
            }
            projectDir = named;
        }
        else
        {
            projectDir = ResolveProjectDir(scope);
        }

        string? assetsPath = NuGetResolver.FindProjectAssetsJson(projectDir);
        if (assetsPath is null)
        {
            return null;
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
                // Match on the manifest's recorded ProjectDir, not on its file name:
                // a same-named project in another directory must not be mistaken for
                // this one and suppress indexing of the project actually being queried.
                string? manifestPath = FindManifestPathForDir(Directory.GetFiles(projectsDir, "*.json"), projectDir);
                if (manifestPath is null || assetsWriteTime > File.GetLastWriteTimeUtc(manifestPath))
                {
                    needsUpdate = true;
                }
            }
        }

        if (needsUpdate)
        {
            return RunIndexWithLock(projectDir, cacheDir);
        }
        return null;
    }

    /// <summary>
    /// Runs an index pass under the cache lock. Returns an error message when the
    /// index failed, or <c>null</c> when it succeeded (or when another process is
    /// already indexing, which is not this caller's failure).
    /// </summary>
    private string? RunIndexWithLock(string projectDir, string cacheDir)
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
            return null;
        }

        using (lockFile)
        {
            try
            {
                logger.LogInformation("Indexing API metadata for this project…");
                // Index the directory already resolved for this scope so a named
                // project is not re-resolved (or resolved differently) here.
                var indexScope = new ApiRequestScope(projectDir, Project: null);
                var result = Refresh(indexScope, scan: false, onProgress: msg => logger.LogInformation("{Message}", msg));
                if (result.Outcome != ApiQueryOutcome.Ok)
                {
                    logger.LogWarning("Failed to index API metadata: {Message}", result.Message);
                    return result.Message ?? "Failed to index API metadata for this project.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cancellation is deliberately excluded so Ctrl+C is not reported
                // as an indexing failure.
                logger.LogWarning(ex, "Failed to index API metadata.");
                return $"Failed to index API metadata for this project: {ex.Message}. " +
                    "Run 'winapp find-api refresh' to see the full error.";
            }
        }
        return null;
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
    /// <summary>
    /// Finds the cached manifest that actually belongs to <paramref name="projectDir"/>.
    /// Manifests are matched on their recorded <c>ProjectDir</c> rather than on file
    /// name: identically-named projects in different directories must never resolve
    /// to each other, and legacy caches may still hold unhashed manifest names.
    /// </summary>
    private static string? FindManifestPathForDir(string[] files, string projectDir)
    {
        string fullPath = Path.GetFullPath(projectDir);
        foreach (string path in files)
        {
            ProjectManifest? manifest = DeserializeManifest(path);
            if (manifest is not null && manifest.ProjectDir.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }
        return null;
    }

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
            var matches = new List<ProjectManifest>();
            foreach (string path in files)
            {
                if (!IsManifestForProject(path, scope.Project))
                {
                    continue;
                }
                ProjectManifest? manifest = DeserializeManifest(path);
                if (manifest is not null &&
                    manifest.ProjectName.Equals(scope.Project, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(manifest);
                }
            }

            List<IGrouping<string, ProjectManifest>> distinct = matches
                .GroupBy(m => Path.GetFullPath(m.ProjectDir), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count == 1)
            {
                return ResolvedScope.Project(distinct[0].First());
            }
            if (distinct.Count > 1)
            {
                // Several indexed projects share this name. Answering from whichever
                // was enumerated first would make the result depend on directory
                // ordering, so make the caller disambiguate explicitly.
                string dirs = string.Join(", ", distinct
                    .Select(g => $"'{g.Key}'")
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
                return ResolvedScope.Failed(
                    $"Project '{scope.Project}' is ambiguous — {distinct.Count} indexed projects share that name: {dirs}. " +
                    "Use '--project-dir <path>' to pick one.");
            }
            string available = AvailableProjects(files);
            return ResolvedScope.Failed(available.Length == 0
                ? $"Project '{scope.Project}' is not indexed — no projects are indexed yet. Run 'winapp find-api refresh' in the project directory, or use '--project sdk' for the Windows SDK scope."
                : $"Project '{scope.Project}' is not indexed. Run 'winapp find-api refresh' in the project directory, or pick from: {available}.");
        }

        if (scope.ProjectDir is not null)
        {
            string fullPath = Path.GetFullPath(scope.ProjectDir);

            // A path that does not exist is a mistake — almost always a typo. Answering
            // it from the SDK scope returns a confident, plausible result for a project
            // the caller never named, which is worse than no answer at all.
            if (!Directory.Exists(fullPath))
            {
                return ResolvedScope.Failed(
                    $"Project directory not found: '{fullPath}'. Check the path passed to '--project-dir', " +
                    "or use '--project sdk' to query the machine-wide Windows SDK scope.");
            }

            string? match = FindManifestPathForDir(files, fullPath);
            if (match is not null)
            {
                return ResolvedScope.Project(DeserializeManifest(match));
            }
            string? dirProjectName = ApiCacheBuilder.FindProjectNameInDir(fullPath);
            if (dirProjectName is not null)
            {
                // An explicit --project-dir named a real project that isn't indexed.
                // Do NOT quietly widen to the SDK scope — the caller asked about that
                // project specifically, and its NuGet packages would be missing — and
                // do NOT fall back to a same-named manifest from another directory.
                string available = AvailableProjects(files);
                return ResolvedScope.Failed($"No indexed API metadata was found for '{fullPath}'. Restore the project (so 'project.assets.json' exists), " +
                    $"then run 'winapp find-api refresh' in that directory.{(available.Length == 0 ? "" : $" Indexed projects: {available}.")}");
            }

            // --project-dir pointed at a directory with no project in it: same
            // situation as running from a projectless directory, so answer from the
            // SDK scope rather than erroring.
            return ResolveSdkScope(cacheDir);
        }

        string cwd = currentDirectory.GetCurrentDirectory();
        string? currentName = ApiCacheBuilder.FindProjectNameInDir(cwd);
        if (currentName is not null)
        {
            // Resolve strictly by directory. Matching a same-named manifest from a
            // different directory would answer from an unrelated project's packages.
            string? match = FindManifestPathForDir(files, cwd);
            if (match is not null)
            {
                return ResolvedScope.Project(DeserializeManifest(match));
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Indexing the machine-wide SDK is best-effort; cancellation still
            // propagates so Ctrl+C is not reported as an SDK failure.
            logger.LogWarning(ex, "Failed to index Windows SDK metadata.");
            return ResolvedScope.Failed(NoSdkMessage);
        }

        manifest = File.Exists(manifestPath) ? DeserializeManifest(manifestPath) : null;
        return manifest is not null ? ResolvedScope.Sdk(manifest) : ResolvedScope.Failed(NoSdkMessage);
    }

    /// <summary>
    /// Human-readable list of indexed project names for an error message. Manifest
    /// *file* names carry a path hash (<c>App_ab12cd34.json</c>) to keep same-named
    /// projects apart, but <c>--project</c> matches <see cref="ProjectManifest.ProjectName"/>,
    /// so the file names must never be surfaced — a caller copying one back would get
    /// "not indexed". Returns an empty string when nothing readable is indexed.
    /// </summary>
    private static string AvailableProjects(IEnumerable<string> files) =>
        string.Join(", ", files
            .Select(DeserializeManifest)
            .Where(m => m is not null && !string.IsNullOrWhiteSpace(m.ProjectName))
            .Select(m => m!.ProjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));

    private static ProjectManifest? DeserializeManifest(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ApiSearchJsonContext.Default.ProjectManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt manifest reads as "no manifest" so the caller can
            // report an unindexed project instead of crashing.
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
