// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Builds and refreshes the on-disk API metadata cache: discovers a project's
/// referenced <c>.winmd</c>/<c>.dll</c> metadata, parses it (plus XML docs),
/// and writes structured JSON per package under the cache directory. Ported
/// from the standalone <c>winmd update</c> flow; progress is reported through a
/// callback so it never touches stdout/stderr directly.
/// </summary>
internal static class ApiCacheBuilder
{
    public static ApiRefreshOutput BuildCache(
        string projectDir,
        string cacheDir,
        bool scan,
        string? winAppSdkRuntimePath,
        Action<string>? onProgress = null,
        bool force = false)
    {
        string fullDir = Path.GetFullPath(projectDir);
        winAppSdkRuntimePath ??= DetectWinAppSdkRuntime();

        List<string> projectFiles = DiscoverProjectFiles(fullDir, scan);
        int parsed = 0, reused = 0, processed = 0;
        var projectNames = new List<string>();

        // Progress is reported from worker threads during the parallel export
        // phase, so serialize it — the console sink behind it is not thread-safe.
        object progressLock = new();
        Action<string>? report = onProgress is null
            ? null
            : msg =>
            {
                lock (progressLock)
                {
                    onProgress(msg);
                }
            };

        // Packages are commonly shared between projects (Windows SDK, WinAppSDK),
        // so resolve every project first and collect the distinct packages that
        // still need parsing. They are then exported in parallel below, and each
        // one is parsed only once per run no matter how many projects use it.
        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingManifests = new List<(string Name, ProjectManifest Manifest)>();

        foreach (string projectFile in projectFiles)
        {
            string dir = Path.GetDirectoryName(projectFile)!;
            string projectName = Path.GetFileNameWithoutExtension(projectFile);
            report?.Invoke($"Indexing {projectName}…");

            List<PackageWithWinMd> packages = NuGetResolver.FindPackagesWithWinMd(dir, projectFile, winAppSdkRuntimePath);
            if (packages.Count == 0)
            {
                continue;
            }
            processed++;
            projectNames.Add(projectName);

            List<ProjectPackageRef> packageRefs = ResolvePackageExports(
                packages, cacheDir, force, pendingExports, seenPackageDirs, ref reused, report);

            var manifest = new ProjectManifest
            {
                ProjectName = projectName,
                ProjectDir = dir,
                ProjectFile = Path.GetFileName(projectFile),
                Packages = packageRefs,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            string manifestName = projectName;
            if (scan)
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectFile))).Substring(0, 8).ToLowerInvariant();
                manifestName = projectName + "_" + hash;
            }
            pendingManifests.Add((manifestName, manifest));
        }

        if (pendingExports.Count > 0)
        {
            report?.Invoke($"Parsing {pendingExports.Count} package(s)…");
            Parallel.ForEach(pendingExports, entry =>
            {
                ExportPackageCache(entry.Value, entry.Key);
                Interlocked.Increment(ref parsed);
            });
        }

        // Manifests are written last so a project is only ever advertised as indexed
        // once the package caches it points at are actually on disk.
        string projectsDir = Path.Combine(cacheDir, "projects");
        if (pendingManifests.Count > 0)
        {
            Directory.CreateDirectory(projectsDir);
            foreach ((string manifestName, ProjectManifest manifest) in pendingManifests)
            {
                WriteFileAtomic(
                    Path.Combine(projectsDir, manifestName + ".json"),
                    JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
            }
        }

        return new ApiRefreshOutput
        {
            ProjectsProcessed = processed,
            PackagesParsed = parsed,
            PackagesReused = reused,
            ProjectNames = projectNames,
        };
    }

    /// <summary>
    /// Decides, for each package, whether its cache can be reused or must be
    /// exported, accumulating the work into <paramref name="pendingExports"/> so
    /// every distinct package is parsed at most once per run. Returns the package
    /// references to record in the owning manifest.
    /// </summary>
    private static List<ProjectPackageRef> ResolvePackageExports(
        List<PackageWithWinMd> packages,
        string cacheDir,
        bool force,
        Dictionary<string, PackageWithWinMd> pendingExports,
        HashSet<string> seenPackageDirs,
        ref int reused,
        Action<string>? report)
    {
        var packageRefs = new List<ProjectPackageRef>();
        foreach (PackageWithWinMd package in packages)
        {
            string packagesRoot = Path.Combine(cacheDir, "packages");
            if (!ApiCachePaths.TryCombineContained(packagesRoot, new[] { package.Id, package.Version }, out string packageCacheDir))
            {
                // Untrusted Id/Version would escape the cache dir — skip it.
                report?.Invoke($"Skipping package with unsafe path: {package.Id} {package.Version}");
                continue;
            }
            // A project reference is exported with version "local" and can change
            // without a version bump, so its cache is never safe to reuse. An
            // explicit refresh (force) rebuilds every package.
            bool mustRebuild = force || string.Equals(package.Version, "local", StringComparison.OrdinalIgnoreCase);
            if (!seenPackageDirs.Add(packageCacheDir))
            {
                // Already resolved for an earlier project in this run.
                reused++;
            }
            else if (!mustRebuild && File.Exists(Path.Combine(packageCacheDir, "meta.json")))
            {
                reused++;
            }
            else
            {
                pendingExports[packageCacheDir] = package;
            }
            packageRefs.Add(new ProjectPackageRef { Id = package.Id, Version = package.Version });
        }
        return packageRefs;
    }

    /// <summary>
    /// Builds the machine-wide SDK scope from <paramref name="sdkPackages"/> (the
    /// Windows SDK UnionMetadata and the installed WinAppSDK runtime), which need
    /// no project on disk. The manifest is written to <c>sdk.json</c> alongside —
    /// never inside — the <c>projects</c> directory, and, as with project
    /// manifests, only after the package caches it points at are on disk.
    /// </summary>
    public static ApiRefreshOutput BuildSdkCache(
        string cacheDir,
        List<PackageWithWinMd> sdkPackages,
        Action<string>? onProgress = null,
        bool force = false)
    {
        if (sdkPackages.Count == 0)
        {
            return new ApiRefreshOutput
            {
                ProjectsProcessed = 0,
                PackagesParsed = 0,
                PackagesReused = 0,
                ProjectNames = [],
            };
        }

        int parsed = 0, reused = 0;
        object progressLock = new();
        Action<string>? report = onProgress is null
            ? null
            : msg =>
            {
                lock (progressLock)
                {
                    onProgress(msg);
                }
            };

        var pendingExports = new Dictionary<string, PackageWithWinMd>(StringComparer.OrdinalIgnoreCase);
        var seenPackageDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        report?.Invoke($"Indexing {ApiCachePaths.SdkScopeName}…");
        List<ProjectPackageRef> packageRefs = ResolvePackageExports(
            sdkPackages, cacheDir, force, pendingExports, seenPackageDirs, ref reused, report);

        if (pendingExports.Count > 0)
        {
            report?.Invoke($"Parsing {pendingExports.Count} package(s)…");
            Parallel.ForEach(pendingExports, entry =>
            {
                ExportPackageCache(entry.Value, entry.Key);
                Interlocked.Increment(ref parsed);
            });
        }

        var manifest = new ProjectManifest
        {
            ProjectName = ApiCachePaths.SdkScopeName,
            ProjectDir = string.Empty,
            ProjectFile = string.Empty,
            Packages = packageRefs,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        Directory.CreateDirectory(cacheDir);
        WriteFileAtomic(
            ApiCachePaths.SdkManifestPath(cacheDir),
            JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));

        return new ApiRefreshOutput
        {
            ProjectsProcessed = 1,
            PackagesParsed = parsed,
            PackagesReused = reused,
            ProjectNames = [ApiCachePaths.SdkScopeName],
        };
    }

    private static void ExportPackageCache(PackageWithWinMd package, string cacheDir)
    {
        string typesDir = Path.Combine(cacheDir, "types");
        Directory.CreateDirectory(typesDir);

        var types = new List<WinMdTypeInfo>();
        if (package.WinMdFiles.Count == 1)
        {
            types.AddRange(WinMdParser.ParseFile(package.WinMdFiles[0]));
        }
        else if (package.WinMdFiles.Count > 1)
        {
            // Parsing is CPU-bound and each metadata file is independent, so fan
            // out and reassemble in file order to keep the output deterministic.
            var perFile = new List<WinMdTypeInfo>[package.WinMdFiles.Count];
            Parallel.For(0, package.WinMdFiles.Count, i => perFile[i] = WinMdParser.ParseFile(package.WinMdFiles[i]));
            foreach (List<WinMdTypeInfo> fileTypes in perFile)
            {
                types.AddRange(fileTypes);
            }
        }

        if (package.XmlDocFiles.Count > 0)
        {
            var perFileDocs = new Dictionary<string, string>[package.XmlDocFiles.Count];
            Parallel.For(0, package.XmlDocFiles.Count, i => perFileDocs[i] = XmlDocParser.ParseFile(package.XmlDocFiles[i]));

            var docs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Dictionary<string, string> fileDocs in perFileDocs)
            {
                foreach (var kvp in fileDocs)
                {
                    docs.TryAdd(kvp.Key, kvp.Value);
                }
            }
            if (docs.Count > 0)
            {
                XmlDocParser.MergeDescriptions(types, docs);
            }
        }

        var byNamespace = types
            .GroupBy(t => t.Namespace)
            .ToDictionary(g => g.Key, g => g.ToList());
        var namespaceNames = byNamespace.Keys
            .Where(ns => !string.IsNullOrEmpty(ns))
            .OrderBy(ns => ns, StringComparer.Ordinal)
            .ToList();
        if (byNamespace.TryGetValue(string.Empty, out var globalTypes) && globalTypes.Count > 0)
        {
            namespaceNames.Insert(0, "_GlobalNamespace");
        }

        var meta = new PackageMeta
        {
            PackageId = package.Id,
            Version = package.Version,
            WinMdFiles = package.WinMdFiles.Select(Path.GetFileName).Where(n => n != null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TotalTypes = types.Count,
            TotalMembers = types.Sum(t => t.Members.Count),
            TotalNamespaces = namespaceNames.Count,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };
        WriteFileAtomic(Path.Combine(cacheDir, "namespaces.json"), JsonSerializer.Serialize(namespaceNames, ApiSearchJsonContext.Default.ListString));

        Parallel.ForEach(namespaceNames, ns =>
        {
            string key = ns == "_GlobalNamespace" ? string.Empty : ns;
            List<WinMdTypeInfo> namespaceTypes = byNamespace[key];
            string fileName = ApiCachePaths.NamespaceFileName(ns);
            WriteFileAtomic(Path.Combine(typesDir, fileName), JsonSerializer.Serialize(namespaceTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        });

        // meta.json is the cache-reuse sentinel, so it is written last — its presence
        // then means the namespace and type payloads it describes are already on disk.
        WriteFileAtomic(Path.Combine(cacheDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
    }

    /// <summary>
    /// Writes content atomically (temp file + rename) so readers never see partial writes,
    /// falling back to a direct write if the staged rename fails.
    /// </summary>
    private static void WriteFileAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        try
        {
            PathSafety.AtomicWriteAllText(path, content);
        }
        catch
        {
            File.WriteAllText(path, content);
        }
    }

    internal static List<string> DiscoverProjectFiles(string inputPath, bool scan)
    {
        var results = new List<string>();
        if (scan)
        {
            if (!Directory.Exists(inputPath))
            {
                return results;
            }
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchType = MatchType.Simple,
            };
            results.AddRange(Directory.EnumerateFiles(inputPath, "*.csproj", options));
            results.AddRange(Directory.EnumerateFiles(inputPath, "*.vcxproj", options));
            return results
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        if (File.Exists(inputPath) && (inputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || inputPath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(inputPath);
        }
        else if (Directory.Exists(inputPath))
        {
            results.AddRange(Directory.GetFiles(inputPath, "*.csproj"));
            results.AddRange(Directory.GetFiles(inputPath, "*.vcxproj"));
        }
        return results;
    }

    /// <summary>Returns the first project name (<c>.csproj</c>/<c>.vcxproj</c>) in a directory, or null.</summary>
    internal static string? FindProjectNameInDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        string[] projectFiles = Directory.GetFiles(dir, "*.csproj").Concat(Directory.GetFiles(dir, "*.vcxproj")).ToArray();
        return projectFiles.Length == 0 ? null : Path.GetFileNameWithoutExtension(projectFiles[0]);
    }

    /// <summary>
    /// Best-effort discovery of the installed WinAppSDK runtime path via
    /// <c>Get-AppxPackage</c>, used to index framework-dependent apps whose
    /// <c>.winmd</c> files live in the runtime rather than a NuGet package.
    /// Failures are non-fatal — NuGet and Windows SDK metadata still index.
    /// </summary>
    internal static string? DetectWinAppSdkRuntime()
    {
        try
        {
            string arch = RuntimeInformation.OSArchitecture.ToString();
            // Use the absolute, well-known Windows PowerShell path so a
            // "powershell.exe" planted in the project directory (or anywhere on
            // PATH) can't be executed instead.
            string systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string powershellPath = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershellPath))
            {
                return null;
            }
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = "-NoProfile -Command \"Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' | Where-Object { $_.Name -notmatch 'CBS' -and $_.Architecture -eq '" + arch + "' } | Sort-Object -Property Version -Descending | Select-Object -First 1 -ExpandProperty InstallLocation\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                return null;
            }
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(15000);
            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                return output;
            }
        }
        catch
        {
        }
        return null;
    }
}
