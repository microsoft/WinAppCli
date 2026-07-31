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

    ApiRefreshOutput Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null);
}

internal sealed class ApiMetadataService(
    IWinappDirectoryService directoryService,
    ICurrentDirectoryProvider currentDirectory,
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

    public ApiRefreshOutput Refresh(ApiRequestScope scope, bool scan, Action<string>? onProgress = null)
    {
        string cacheDir = GetCacheDir();
        string projectDir = ResolveProjectDir(scope);
        string? runtimePath = ApiCacheBuilder.DetectWinAppSdkRuntime();
        return ApiCacheBuilder.BuildCache(projectDir, cacheDir, scan, runtimePath, onProgress);
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
        (ProjectManifest? manifest, string? error) = ResolveManifest(scope, cacheDir);
        if (manifest is null)
        {
            return ApiQueryResult<T>.NoProject(error ?? NoProjectMessage);
        }
        return query(cacheDir, manifest);
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
        try
        {
            using var lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            logger.LogInformation("Indexing API metadata for this project…");
            Refresh(scope, scan: false, onProgress: msg => logger.LogInformation("{Message}", msg));
        }
        catch (IOException)
        {
            // Another winapp process is indexing the same cache. Wait briefly for it
            // to finish, then fall through to querying whatever is on disk.
            logger.LogInformation("API metadata cache is being indexed by another process, waiting…");
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(1000);
                try
                {
                    using var lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Resolve the cached <see cref="ProjectManifest"/> for the request, mirroring
    /// the standalone tool's precedence: explicit <c>--project</c> name, then a
    /// <c>--project-dir</c> match (by recorded dir, then by discovered project
    /// name), then a lone cached project, then the project in the current
    /// directory. Returns a human-readable error when resolution is impossible.
    /// </summary>
    private (ProjectManifest? Manifest, string? Error) ResolveManifest(ApiRequestScope scope, string cacheDir)
    {
        string projectsDir = Path.Combine(cacheDir, "projects");
        if (!Directory.Exists(projectsDir))
        {
            return (null, NoProjectMessage);
        }
        string[] files = Directory.GetFiles(projectsDir, "*.json");
        if (files.Length == 0)
        {
            return (null, NoProjectMessage);
        }

        if (scope.Project is not null)
        {
            foreach (string path in files)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Equals(scope.Project, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(scope.Project + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return (DeserializeManifest(path), null);
                }
            }
            return (null, $"Project '{scope.Project}' is not indexed. Run 'winapp find-api refresh' in the project directory, or pick from: {AvailableProjects(files)}.");
        }

        if (scope.ProjectDir is not null)
        {
            string fullPath = Path.GetFullPath(scope.ProjectDir);
            foreach (string path in files)
            {
                ProjectManifest? manifest = DeserializeManifest(path);
                if (manifest is not null && manifest.ProjectDir.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return (manifest, null);
                }
            }
            string? projectName = ApiCacheBuilder.FindProjectNameInDir(fullPath);
            if (projectName is not null)
            {
                foreach (string path in files)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.Equals(projectName, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(projectName + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        return (DeserializeManifest(path), null);
                    }
                }
            }
        }

        if (files.Length == 1)
        {
            return (DeserializeManifest(files[0]), null);
        }

        string? currentName = ApiCacheBuilder.FindProjectNameInDir(scope.ProjectDir ?? currentDirectory.GetCurrentDirectory());
        if (currentName is not null)
        {
            foreach (string path in files)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Equals(currentName, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(currentName + "_", StringComparison.OrdinalIgnoreCase))
                {
                    return (DeserializeManifest(path), null);
                }
            }
        }

        return (null, $"Multiple projects are indexed — use --project to choose one: {AvailableProjects(files)}.");
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
}
