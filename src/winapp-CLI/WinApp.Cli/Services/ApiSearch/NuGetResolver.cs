// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Xml.Linq;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Discovers the <c>.winmd</c> metadata and XML documentation a project
/// references — from NuGet (<c>project.assets.json</c> / <c>packages.config</c>),
/// project references, the Windows SDK UnionMetadata, and the WinAppSDK runtime.
/// </summary>
internal static class NuGetResolver
{
    public static List<PackageWithWinMd> FindPackagesWithWinMd(string projectDir, string projectFile, string? winAppSdkRuntimePath)
    {
        var packages = new List<PackageWithWinMd>();

        string? assetsPath = FindProjectAssetsJson(projectDir);
        if (assetsPath != null)
        {
            packages.AddRange(FindPackagesFromAssets(assetsPath));
        }
        if (packages.Count == 0)
        {
            string configPath = Path.Combine(projectDir, "packages.config");
            if (File.Exists(configPath))
            {
                packages.AddRange(FindPackagesFromConfig(configPath, projectDir));
            }
        }

        packages.AddRange(FindWinMdFromProjectReferences(projectFile));
        packages.AddRange(CollectSdkPackages(winAppSdkRuntimePath));

        return Deduplicate(packages);
    }

    /// <summary>
    /// The machine-wide metadata that needs no project at all: the Windows SDK
    /// UnionMetadata and the installed WinAppSDK runtime, plus their XML docs from
    /// the NuGet global cache. This is what backs <c>find-api</c>'s SDK scope when
    /// a query runs outside any project, so it must not consult
    /// <c>project.assets.json</c>, <c>packages.config</c>, or project references.
    /// </summary>
    public static List<PackageWithWinMd> FindSdkPackages(string? winAppSdkRuntimePath) =>
        Deduplicate(CollectSdkPackages(winAppSdkRuntimePath));

    private static List<PackageWithWinMd> CollectSdkPackages(string? winAppSdkRuntimePath)
    {
        var packages = new List<PackageWithWinMd>();

        (List<string> Files, string Version) sdk = FindWindowsSdkWinMd();
        if (sdk.Files.Count > 0)
        {
            packages.Add(new PackageWithWinMd("WindowsSDK", sdk.Version, sdk.Files, new List<string>()));
        }

        (List<string> Files, string Version) runtime = FindWinAppSdkRuntimeWinMd(winAppSdkRuntimePath);
        if (runtime.Files.Count > 0)
        {
            packages.Add(new PackageWithWinMd("WinAppSdkRuntime", runtime.Version, runtime.Files, new List<string>()));
        }

        DiscoverSdkXmlDocs(packages);
        return packages;
    }

    private static List<PackageWithWinMd> Deduplicate(List<PackageWithWinMd> packages) =>
        packages
            .GroupBy(p => (p.Id.ToLowerInvariant(), p.Version.ToLowerInvariant()))
            .Select(g =>
            {
                var winMdFiles = g.SelectMany(p => p.WinMdFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var xmlDocFiles = g.SelectMany(p => p.XmlDocFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var first = g.First();
                return new PackageWithWinMd(first.Id, first.Version, winMdFiles, xmlDocFiles);
            })
            .ToList();

    /// <summary>
    /// Finds XML documentation files in a NuGet package folder that contains a
    /// <c>metadata</c> directory (WinUI pattern) or <c>lib</c> directory.
    /// </summary>
    internal static List<string> FindXmlDocsInPackageFolder(string packageFolder)
    {
        var xmlFiles = new List<string>();
        try
        {
            string metadataDir = Path.Combine(packageFolder, "metadata");
            if (Directory.Exists(metadataDir))
            {
                xmlFiles.AddRange(Directory.GetFiles(metadataDir, "*.xml"));
            }

            string libDir = Path.Combine(packageFolder, "lib");
            if (Directory.Exists(libDir))
            {
                foreach (var xml in Directory.GetFiles(libDir, "*.xml", SearchOption.AllDirectories))
                {
                    try
                    {
                        // Skip trivial (< 1KB) XML files that carry no real docs.
                        if (new FileInfo(xml).Length > 1024)
                        {
                            xmlFiles.Add(xml);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file that vanished or is not readable simply contributes no docs.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Doc discovery is best-effort: an unreadable package folder yields no docs
            // rather than failing the whole index.
        }
        return xmlFiles;
    }

    /// <summary>
    /// Discovers XML documentation from well-known SDK NuGet packages
    /// (<c>microsoft.windows.sdk.net.ref</c>, <c>microsoft.windowsappsdk.winui</c>)
    /// that provide WinRT API docs, and attaches them to the matching package.
    /// </summary>
    private static void DiscoverSdkXmlDocs(List<PackageWithWinMd> packages)
    {
        string nugetPackagesDir = GetNuGetPackagesDir();

        string sdkRefDir = Path.Combine(nugetPackagesDir, "microsoft.windows.sdk.net.ref");
        if (Directory.Exists(sdkRefDir))
        {
            try
            {
                var latest = Directory.GetDirectories(sdkRefDir).OrderByDescending(Path.GetFileName).FirstOrDefault();
                if (latest != null)
                {
                    var xmlDocs = FindXmlDocsInPackageFolder(latest);
                    if (xmlDocs.Count > 0)
                    {
                        var sdkPkg = packages.FirstOrDefault(p => p.Id.Equals("WindowsSDK", StringComparison.OrdinalIgnoreCase));
                        sdkPkg?.XmlDocFiles.AddRange(xmlDocs);
                    }
                }
            }
            catch
            {
            }
        }

        string winuiDir = Path.Combine(nugetPackagesDir, "microsoft.windowsappsdk.winui");
        if (Directory.Exists(winuiDir))
        {
            try
            {
                var latest = Directory.GetDirectories(winuiDir).OrderByDescending(Path.GetFileName).FirstOrDefault();
                if (latest != null)
                {
                    var xmlDocs = FindXmlDocsInPackageFolder(latest);
                    if (xmlDocs.Count > 0)
                    {
                        var runtimePkg = packages.FirstOrDefault(p => p.Id.Equals("WinAppSdkRuntime", StringComparison.OrdinalIgnoreCase))
                            ?? packages.FirstOrDefault(p =>
                                p.Id.Contains("WinUI", StringComparison.OrdinalIgnoreCase) ||
                                p.Id.Contains("WindowsAppSDK", StringComparison.OrdinalIgnoreCase));
                        runtimePkg?.XmlDocFiles.AddRange(xmlDocs);
                    }
                }
            }
            catch
            {
            }
        }
    }

    internal static List<PackageWithWinMd> FindWinMdFromProjectReferences(string projectFile)
    {
        var packages = new List<PackageWithWinMd>();
        try
        {
            XDocument doc = XDocument.Load(projectFile);
            XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var references = doc.Descendants(ns + "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => v != null)
                .Select(v => v!)
                .ToList();
            if (references.Count == 0)
            {
                return packages;
            }

            string projectDir = Path.GetDirectoryName(projectFile)!;
            foreach (string reference in references)
            {
                string fullPath = Path.GetFullPath(Path.Combine(projectDir, reference));
                if (!File.Exists(fullPath))
                {
                    continue;
                }
                string refDir = Path.GetDirectoryName(fullPath)!;
                string refName = Path.GetFileNameWithoutExtension(fullPath);
                string binDir = Path.Combine(refDir, "bin");
                if (!Directory.Exists(binDir))
                {
                    continue;
                }
                var winmds = Directory.GetFiles(binDir, "*.winmd", SearchOption.AllDirectories)
                    .Where(f => !Path.GetFileName(f).Equals("Windows.winmd", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                winmds = winmds.Where(f => seen.Add(Path.GetFileName(f))).ToList();
                if (winmds.Count > 0)
                {
                    packages.Add(new PackageWithWinMd("ProjectRef." + refName, "local", winmds, new List<string>()));
                }
            }
        }
        catch
        {
        }
        return packages;
    }

    internal static string? FindProjectAssetsJson(string projectDir)
    {
        string direct = Path.Combine(projectDir, "obj", "project.assets.json");
        if (File.Exists(direct))
        {
            return direct;
        }
        string objDir = Path.Combine(projectDir, "obj");
        if (!Directory.Exists(objDir))
        {
            return null;
        }
        string[] files = Directory.GetFiles(objDir, "project.assets.json", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            return null;
        }
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        foreach (string file in files)
        {
            try
            {
                DateTime writeTime = File.GetLastWriteTimeUtc(file);
                if (writeTime > newestTime)
                {
                    newestTime = writeTime;
                    newest = file;
                }
            }
            catch
            {
            }
        }
        return newest;
    }

    internal static List<PackageWithWinMd> FindPackagesFromAssets(string assetsPath)
    {
        var packages = new List<PackageWithWinMd>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
            JsonElement root = doc.RootElement;

            var packageFolders = new List<string>();
            if (root.TryGetProperty("packageFolders", out var packageFoldersEl))
            {
                foreach (JsonProperty folder in packageFoldersEl.EnumerateObject())
                {
                    packageFolders.Add(folder.Name);
                }
            }

            if (!root.TryGetProperty("libraries", out var librariesEl))
            {
                return packages;
            }

            // Map "id/version" -> compile-time .dll relative paths from the first target.
            var compileByLibrary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("targets", out var targetsEl))
            {
                using JsonElement.ObjectEnumerator targets = targetsEl.EnumerateObject().GetEnumerator();
                if (targets.MoveNext())
                {
                    foreach (JsonProperty library in targets.Current.Value.EnumerateObject())
                    {
                        if (!library.Value.TryGetProperty("compile", out var compileEl))
                        {
                            continue;
                        }
                        var dlls = compileEl.EnumerateObject()
                            .Select(entry => entry.Name)
                            .Where(entryName => entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                && !entryName.EndsWith("/_._", StringComparison.Ordinal))
                            .ToList();
                        if (dlls.Count > 0)
                        {
                            compileByLibrary[library.Name] = dlls;
                        }
                    }
                }
            }

            foreach (JsonProperty library in librariesEl.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var typeEl) || !string.Equals(typeEl.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int slash = library.Name.IndexOf('/');
                if (slash < 0)
                {
                    continue;
                }
                string id = library.Name.Substring(0, slash);
                string version = library.Name.Substring(slash + 1);
                if (IsFrameworkPackage(id) || !library.Value.TryGetProperty("path", out var pathEl))
                {
                    continue;
                }
                string? relativePath = pathEl.GetString();
                if (relativePath == null)
                {
                    continue;
                }

                var files = new List<string>();
                foreach (string packageFolder in packageFolders)
                {
                    string packageDir = Path.Combine(packageFolder, relativePath);
                    if (!Directory.Exists(packageDir))
                    {
                        continue;
                    }
                    files.AddRange(Directory.GetFiles(packageDir, "*.winmd", SearchOption.AllDirectories));
                    if (compileByLibrary.TryGetValue(library.Name, out var dlls))
                    {
                        files.AddRange(dlls
                            .Select(dll => Path.Combine(packageDir, dll.Replace('/', Path.DirectorySeparatorChar)))
                            .Where(File.Exists));
                    }
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                files = files.Where(f => seen.Add(Path.GetFileName(f))).ToList();
                if (files.Count == 0)
                {
                    continue;
                }

                var xmlDocs = packageFolders
                    .Select(packageFolder => Path.Combine(packageFolder, relativePath))
                    .Where(Directory.Exists)
                    .SelectMany(FindXmlDocsInPackageFolder)
                    .ToList();
                packages.Add(new PackageWithWinMd(id, version, files, xmlDocs));
            }
        }
        catch
        {
        }
        return packages;
    }

    internal static bool IsFrameworkPackage(string packageId)
    {
        if (packageId.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase) || packageId.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string[] prefixes = { "System.", "Microsoft.NETCore.", "Microsoft.NET.", "runtime.", "Microsoft.Build.", "Microsoft.CodeAnalysis.", "Microsoft.DiaSymReader." };
        return prefixes.Any(prefix => packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static List<PackageWithWinMd> FindPackagesFromConfig(string configPath, string projectDir)
    {
        var packages = new List<PackageWithWinMd>();
        try
        {
            IEnumerable<XElement>? entries = XDocument.Load(configPath).Root?.Elements("package");
            if (entries == null)
            {
                return packages;
            }
            string? solutionPackages = FindSolutionPackagesFolder(projectDir);
            string globalPackages = GetNuGetPackagesDir();
            foreach (XElement entry in entries)
            {
                string? id = entry.Attribute("id")?.Value;
                string? version = entry.Attribute("version")?.Value;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
                {
                    continue;
                }
                var files = new List<string>();
                if (solutionPackages != null)
                {
                    string dir = Path.Combine(solutionPackages, id + "." + version);
                    if (Directory.Exists(dir))
                    {
                        files.AddRange(Directory.GetFiles(dir, "*.winmd", SearchOption.AllDirectories));
                    }
                }
                if (files.Count == 0 && Directory.Exists(globalPackages))
                {
                    string dir = Path.Combine(globalPackages, id.ToLowerInvariant(), version);
                    if (Directory.Exists(dir))
                    {
                        files.AddRange(Directory.GetFiles(dir, "*.winmd", SearchOption.AllDirectories));
                    }
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                files = files.Where(f => seen.Add(Path.GetFileName(f))).ToList();
                if (files.Count > 0)
                {
                    packages.Add(new PackageWithWinMd(id, version, files, new List<string>()));
                }
            }
        }
        catch
        {
        }
        return packages;
    }

    internal static string? FindSolutionPackagesFolder(string startDir)
    {
        string current = startDir;
        for (int i = 0; i < 5; i++)
        {
            string candidate = Path.Combine(current, "packages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }
            current = parent.FullName;
        }
        return null;
    }

    internal static (List<string> Files, string Version) FindWindowsSdkWinMd()
    {
        string unionMetadata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "UnionMetadata");
        if (!Directory.Exists(unionMetadata))
        {
            return (new List<string>(), "unknown");
        }

        var versioned = Directory.GetDirectories(unionMetadata)
            .Select(d => (Dir: d, Name: Path.GetFileName(d)))
            .Where(x => !string.IsNullOrEmpty(x.Name) && char.IsDigit(x.Name[0]))
            .Select(x => Version.TryParse(x.Name, out var v) ? (x.Dir, Version: v) : (x.Dir, Version: null))
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .ToList();

        foreach (string dir in versioned)
        {
            string windowsWinmd = Path.Combine(dir, "Windows.winmd");
            if (File.Exists(windowsWinmd))
            {
                return (new List<string> { windowsWinmd }, Path.GetFileName(dir));
            }
        }
        return (new List<string>(), "unknown");
    }

    internal static (List<string> Files, string Version) FindWinAppSdkRuntimeWinMd(string? runtimePath)
    {
        if (string.IsNullOrEmpty(runtimePath) || !Directory.Exists(runtimePath))
        {
            return (new List<string>(), "unknown");
        }
        try
        {
            var files = Directory.EnumerateFiles(runtimePath, "*.winmd", SearchOption.TopDirectoryOnly).ToList();
            if (files.Count > 0)
            {
                string folderName = Path.GetFileName(runtimePath);
                const string prefix = "Microsoft.WindowsAppRuntime.";
                string head = folderName.Split('_')[0];
                string version = head.Length <= prefix.Length ? folderName : head.Substring(prefix.Length);
                return (files, version);
            }
        }
        catch
        {
        }
        return (new List<string>(), "unknown");
    }

    private static string GetNuGetPackagesDir()
    {
        string? env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }
}
