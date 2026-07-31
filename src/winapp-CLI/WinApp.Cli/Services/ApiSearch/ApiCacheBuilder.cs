// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

        foreach (string projectFile in projectFiles)
        {
            string dir = Path.GetDirectoryName(projectFile)!;
            string projectName = Path.GetFileNameWithoutExtension(projectFile);
            onProgress?.Invoke($"Indexing {projectName}…");

            List<PackageWithWinMd> packages = NuGetResolver.FindPackagesWithWinMd(dir, projectFile, winAppSdkRuntimePath);
            if (packages.Count == 0)
            {
                continue;
            }
            processed++;
            projectNames.Add(projectName);

            var packageRefs = new List<ProjectPackageRef>();
            foreach (PackageWithWinMd package in packages)
            {
                string packagesRoot = Path.Combine(cacheDir, "packages");
                if (!ApiCachePaths.TryCombineContained(packagesRoot, new[] { package.Id, package.Version }, out string packageCacheDir))
                {
                    // Untrusted Id/Version would escape the cache dir — skip it.
                    onProgress?.Invoke($"Skipping package with unsafe path: {package.Id} {package.Version}");
                    continue;
                }
                // A project reference is exported with version "local" and can change
                // without a version bump, so its cache is never safe to reuse. An
                // explicit refresh (force) rebuilds every package.
                bool mustRebuild = force || string.Equals(package.Version, "local", StringComparison.OrdinalIgnoreCase);
                if (!mustRebuild && File.Exists(Path.Combine(packageCacheDir, "meta.json")))
                {
                    reused++;
                }
                else
                {
                    ExportPackageCache(package, packageCacheDir);
                    parsed++;
                }
                packageRefs.Add(new ProjectPackageRef { Id = package.Id, Version = package.Version });
            }

            var manifest = new ProjectManifest
            {
                ProjectName = projectName,
                ProjectDir = dir,
                ProjectFile = Path.GetFileName(projectFile),
                Packages = packageRefs,
                GeneratedAt = DateTime.UtcNow.ToString("o"),
            };
            string projectsDir = Path.Combine(cacheDir, "projects");
            Directory.CreateDirectory(projectsDir);
            string manifestName = projectName;
            if (scan)
            {
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectFile))).Substring(0, 8).ToLowerInvariant();
                manifestName = projectName + "_" + hash;
            }
            WriteFileAtomic(
                Path.Combine(projectsDir, manifestName + ".json"),
                JsonSerializer.Serialize(manifest, ApiSearchJsonContext.Default.ProjectManifest));
        }

        return new ApiRefreshOutput
        {
            ProjectsProcessed = processed,
            PackagesParsed = parsed,
            PackagesReused = reused,
            ProjectNames = projectNames,
        };
    }

    private static void ExportPackageCache(PackageWithWinMd package, string cacheDir)
    {
        string typesDir = Path.Combine(cacheDir, "types");
        Directory.CreateDirectory(typesDir);

        var types = new List<WinMdTypeInfo>();
        foreach (string winMdFile in package.WinMdFiles)
        {
            types.AddRange(WinMdParser.ParseFile(winMdFile));
        }

        if (package.XmlDocFiles.Count > 0)
        {
            var docs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string xmlFile in package.XmlDocFiles)
            {
                foreach (var kvp in XmlDocParser.ParseFile(xmlFile))
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
        WriteFileAtomic(Path.Combine(cacheDir, "meta.json"), JsonSerializer.Serialize(meta, ApiSearchJsonContext.Default.PackageMeta));
        WriteFileAtomic(Path.Combine(cacheDir, "namespaces.json"), JsonSerializer.Serialize(namespaceNames, ApiSearchJsonContext.Default.ListString));

        foreach (string ns in namespaceNames)
        {
            string key = ns == "_GlobalNamespace" ? string.Empty : ns;
            List<WinMdTypeInfo> namespaceTypes = byNamespace[key];
            string fileName = ApiCachePaths.NamespaceFileName(ns);
            WriteFileAtomic(Path.Combine(typesDir, fileName), JsonSerializer.Serialize(namespaceTypes, ApiSearchJsonContext.Default.ListWinMdTypeInfo));
        }
    }

    /// <summary>Writes content atomically (temp file + rename) so readers never see partial writes.</summary>
    private static void WriteFileAtomic(string path, string content)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        string tempPath = path + ".tmp." + Environment.ProcessId;
        try
        {
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }
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
