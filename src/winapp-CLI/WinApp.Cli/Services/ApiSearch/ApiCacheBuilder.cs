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
                ProjectDir = Path.GetFullPath(dir),
                ProjectFile = Path.GetFileName(projectFile),
                Packages = packageRefs,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            pendingManifests.Add((ManifestName(projectFile), manifest));
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
            else if (!mustRebuild && IsReusableCache(packageCacheDir))
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
    /// Whether an existing package cache can be reused as-is. A cache written by an
    /// older layout is *not* reusable: the file naming it used no longer matches what
    /// the query side looks for, so reusing it would read as an empty index — a silent
    /// wrong answer — rather than an error. It is also not reusable when the previous
    /// run failed to parse one of its metadata files, so a transient read failure heals
    /// on the next refresh instead of being cached forever.
    /// </summary>
    private static bool IsReusableCache(string packageCacheDir)
    {
        string metaPath = Path.Combine(packageCacheDir, "meta.json");
        if (!File.Exists(metaPath))
        {
            return false;
        }
        try
        {
            PackageMeta? meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), ApiSearchJsonContext.Default.PackageMeta);
            return meta is { Incomplete: false } && meta.Format == ApiCachePaths.CacheFormatVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable or malformed meta.json reads as "no usable cache", so the
            // package is simply re-exported.
            return false;
        }
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
        var parseErrors = new List<string>();
        if (package.WinMdFiles.Count == 1)
        {
            WinMdParser.WinMdParseResult single = WinMdParser.ParseFile(package.WinMdFiles[0]);
            types.AddRange(single.Types);
            if (single.Error is not null)
            {
                parseErrors.Add($"{Path.GetFileName(package.WinMdFiles[0])}: {single.Error}");
            }
        }
        else if (package.WinMdFiles.Count > 1)
        {
            // Parsing is CPU-bound and each metadata file is independent, so fan
            // out and reassemble in file order to keep the output deterministic.
            var perFile = new WinMdParser.WinMdParseResult[package.WinMdFiles.Count];
            Parallel.For(0, package.WinMdFiles.Count, i => perFile[i] = WinMdParser.ParseFile(package.WinMdFiles[i]));
            for (int i = 0; i < perFile.Length; i++)
            {
                types.AddRange(perFile[i].Types);
                if (perFile[i].Error is not null)
                {
                    parseErrors.Add($"{Path.GetFileName(package.WinMdFiles[i])}: {perFile[i].Error}");
                }
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
            Format = ApiCachePaths.CacheFormatVersion,
            PackageId = package.Id,
            Version = package.Version,
            WinMdFiles = package.WinMdFiles.Select(Path.GetFileName).Where(n => n != null).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TotalTypes = types.Count,
            TotalMembers = types.Sum(t => t.Members.Count),
            TotalNamespaces = namespaceNames.Count,
            // Recorded so the query side can qualify a "not found" answer: a file that
            // failed to parse contributed no types, and without this marker a missing
            // API is indistinguishable from one that was simply never indexed.
            Incomplete = parseErrors.Count > 0,
            ParseErrors = parseErrors.Count > 0 ? parseErrors : null,
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
    /// Writes content atomically (temp file + rename) so readers never see partial writes.
    /// There is deliberately no direct-write fallback: <see cref="File.WriteAllText(string, string)"/>
    /// follows a symlink planted at the destination, whereas the staged rename replaces it,
    /// and cache writes are already serialized by the cache lock — so a fallback would only
    /// ever run in the case where it is unsafe.
    /// </summary>
    private static void WriteFileAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        PathSafety.AtomicWriteAllText(path, content);
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
    /// <summary>
    /// Cache file name for a project's manifest. The project's full path is hashed
    /// into the name so that identically-named projects in different directories
    /// (a monorepo, or the same template scaffolded repeatedly) each get their own
    /// manifest instead of silently overwriting one another.
    /// </summary>
    internal static string ManifestName(string projectFile)
    {
        string fullPath = Path.GetFullPath(projectFile);
        return Path.GetFileNameWithoutExtension(fullPath) + "_" + ApiCachePaths.ShortHash(fullPath);
    }

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
    /// <summary>
    /// Selects the newest installed Windows App Runtime for the current architecture.
    /// </summary>
    /// <remarks>
    /// Sorting on the raw Appx <c>Version</c> picks the wrong runtime, because the two
    /// release lines number their packages differently: <c>Microsoft.WindowsAppRuntime.1.8</c>
    /// reports <c>8000.946.1701.0</c> while the newer <c>Microsoft.WindowsAppRuntime.2</c>
    /// reports <c>2.4.0.0</c>, so a descending Version sort ranks 1.8 far above 2.4.
    /// The release identity therefore comes from the package name: a
    /// <c>major.minor</c> suffix is the release outright, while a major-only suffix
    /// takes its minor from the package version. Stable beats experimental within a
    /// release, and experimental is still used when it is all that is installed.
    /// </remarks>
    private const string NewestRuntimeScript =
        "Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' | " +
        "Where-Object { $_.Name -notmatch 'CBS' -and $_.Architecture -eq '{ARCH}' } | " +
        "ForEach-Object { " +
            "$suffix = $_.Name -replace '^Microsoft\\.WindowsAppRuntime\\.',''; " +
            "$core = ($suffix -split '-')[0]; " +
            "$v = [version]$_.Version; " +
            "if ($core -match '^\\d+\\.\\d+$') { $rel = [version]$core } " +
            "elseif ($core -match '^\\d+$') { $rel = [version]('{0}.{1}' -f $v.Major, $v.Minor) } " +
            "else { $rel = [version]'0.0' }; " +
            "[pscustomobject]@{ Rel = $rel; Stable = [int]($suffix -notmatch '-'); Ver = $v; Path = $_.InstallLocation } " +
        "} | " +
        "Sort-Object Rel, Stable, Ver -Descending | " +
        "Select-Object -First 1 -ExpandProperty Path";

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
                Arguments = "-NoProfile -Command \"" + NewestRuntimeScript.Replace("{ARCH}", arch, StringComparison.Ordinal) + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                return null;
            }

            // Both streams are drained asynchronously *before* waiting. Reading
            // stdout synchronously would block until the process exits — making the
            // timeout below unreachable — and leaving stderr undrained lets a chatty
            // or hung query fill its pipe and deadlock the very first index build.
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (stdout)
                    {
                        stdout.AppendLine(e.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(15000))
            {
                KillProcessTree(process);
                return null;
            }
            // Second, untimed wait: it is what flushes the async output handlers, so
            // the buffer below is complete rather than racing the last callback.
            process.WaitForExit();

            string output;
            lock (stdout)
            {
                output = stdout.ToString().Trim();
            }
            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output) && Directory.Exists(output))
            {
                return output;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Probing for an installed WindowsAppRuntime is best-effort: if PowerShell
            // cannot be launched or the query fails, report "not found" and let the
            // caller fall back to the packages it already resolved.
        }
        return null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or AggregateException)
        {
            // The process already exited, or the OS refused the kill. Either way the
            // caller only needs "no runtime detected".
        }
    }
}
