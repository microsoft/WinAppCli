// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal partial class NugetService(IWinappDirectoryService winappDirectoryService) : INugetService
{
    private static readonly HttpClient Http = new();
    private const string FlatIndex = "https://api.nuget.org/v3-flatcontainer";
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> DependencyCache = new(StringComparer.OrdinalIgnoreCase);

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

        // NUGET_PACKAGES env var takes priority
        var envPath = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrEmpty(envPath))
        {
            var envDir = new DirectoryInfo(envPath);
            if (!envDir.Exists)
            {
                envDir.Create();
            }
            return envDir;
        }

        // Default: %USERPROFILE%/.nuget/packages
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nugetDir = new DirectoryInfo(Path.Combine(userProfile, ".nuget", "packages"));
        if (!nugetDir.Exists)
        {
            nugetDir.Create();
        }
        return nugetDir;
    }

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
    {
        var cache = GetNuGetGlobalPackagesDir();
        return new DirectoryInfo(Path.Combine(cache.FullName, packageName.ToLowerInvariant(), version));
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
        BuildToolsService.BUILD_TOOLS_PACKAGE,
        BuildToolsService.WINAPP_SDK_PACKAGE,
        "Microsoft.Windows.ImplementationLibrary",
        BuildToolsService.CPP_SDK_PACKAGE,
        $"{BuildToolsService.CPP_SDK_PACKAGE}.x64",
        $"{BuildToolsService.CPP_SDK_PACKAGE}.arm64"
    ];

    public async Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await InstallPackageRecursiveAsync(package, version, packages, taskContext, cancellationToken);
        return packages;
    }

    /// <summary>
    /// Downloads and extracts a NuGet package to the global packages cache, then recursively installs dependencies.
    /// </summary>
    private async Task InstallPackageRecursiveAsync(string package, string version, Dictionary<string, string> installed, TaskContext taskContext, CancellationToken cancellationToken)
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
            await ResolveDependenciesAsync(packageDir, package, version, installed, taskContext, cancellationToken);
            return;
        }

        // Download .nupkg from the NuGet flat container API
        var lowerId = package.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var url = $"{FlatIndex}/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

        using var resp = await Http.GetAsync(url, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to download {package} {version} from NuGet (HTTP {resp.StatusCode})");
        }

        // Extract to the NuGet global cache location
        Directory.CreateDirectory(packageDir.FullName);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        ZipFile.ExtractToDirectory(stream, packageDir.FullName, overwriteFiles: true);

        installed[package] = version;
        taskContext.AddStatusMessage($"{UiSymbols.Check} Installed {package} {version}");

        // Recursively install dependencies
        await ResolveDependenciesAsync(packageDir, package, version, installed, taskContext, cancellationToken);
    }

    /// <summary>
    /// Reads the .nuspec from an extracted package and recursively installs dependencies.
    /// </summary>
    private async Task ResolveDependenciesAsync(DirectoryInfo packageDir, string package, string version, Dictionary<string, string> installed, TaskContext taskContext, CancellationToken cancellationToken)
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

                var depVersion = ParseMinimumVersion(depVersionRange);
                if (!string.IsNullOrEmpty(depVersion))
                {
                    await InstallPackageRecursiveAsync(depName, depVersion, installed, taskContext, cancellationToken);
                }
            }
        }
        catch
        {
            // Dependency resolution failures are non-fatal; the main package is installed
        }
    }

    /// <summary>
    /// Reads dependencies from the .nuspec file embedded in an extracted NuGet package.
    /// </summary>
    private static Dictionary<string, string> ReadDependenciesFromNuspec(DirectoryInfo packageDir, string packageName)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        var doc = new XmlDocument();
        doc.Load(nuspecPath);

        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        var ns = doc.DocumentElement?.NamespaceURI ?? string.Empty;
        if (!string.IsNullOrEmpty(ns))
        {
            nsMgr.AddNamespace("ns", ns);
        }

        var prefix = string.IsNullOrEmpty(ns) ? "" : "ns:";
        var depNodes = doc.SelectNodes($"//{prefix}dependency", nsMgr);
        if (depNodes != null)
        {
            foreach (XmlNode node in depNodes)
            {
                var depId = node.Attributes?["id"]?.Value;
                var depVersion = node.Attributes?["version"]?.Value;
                if (!string.IsNullOrEmpty(depId) && !string.IsNullOrEmpty(depVersion))
                {
                    dependencies.TryAdd(depId, depVersion);
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Parses a NuGet version range and extracts the minimum version.
    /// Handles: "1.0.0", "[1.0.0]", "[1.0.0, )", "(1.0.0, 2.0.0)", etc.
    /// </summary>
    internal static string ParseMinimumVersion(string versionRange)
    {
        if (string.IsNullOrWhiteSpace(versionRange))
        {
            return string.Empty;
        }

        var trimmed = versionRange.Trim();

        // Simple version (no brackets)
        if (!trimmed.Contains('[') && !trimmed.Contains('('))
        {
            return trimmed;
        }

        // Strip brackets/parens
        trimmed = trimmed.TrimStart('[', '(').TrimEnd(']', ')');

        // Take the lower bound (before comma if present)
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

        var url = $"{FlatIndex}/{packageName.ToLowerInvariant()}/index.json";
        using var resp = await Http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        using var s = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("versions", out var versionsElem) || versionsElem.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"No versions found for {packageName}");
        }

        var list = new List<string>();
        foreach (var el in versionsElem.EnumerateArray())
        {
            var v = el.GetString();
            if (!string.IsNullOrWhiteSpace(v))
            {
                list.Add(v);
            }
        }

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

    [GeneratedRegex(@"[\[\]\(\)]")]
    private static partial Regex BracketsAndParenthesesRegex();

    /// <inheritdoc />
    public async Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{packageName}/{version}";
        if (DependencyCache.TryGetValue(cacheKey, out var cached))
        {
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        }

        var directDeps = await FetchDirectDependenciesAsync(packageName, version, cancellationToken);

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

    private static async Task<Dictionary<string, string>> FetchDirectDependenciesAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Fetch the .nuspec from NuGet flat container API
        var id = packageName.ToLowerInvariant();
        var ver = version.ToLowerInvariant();
        var nuspecUrl = $"{FlatIndex}/{id}/{ver}/{id}.nuspec";

        using var resp = await Http.GetAsync(nuspecUrl, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return dependencies;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var doc = new XmlDocument();
        doc.Load(stream);

        // The nuspec uses a default namespace; we need a namespace manager
        var nsMgr = new XmlNamespaceManager(doc.NameTable);
        var ns = doc.DocumentElement?.NamespaceURI ?? string.Empty;
        if (!string.IsNullOrEmpty(ns))
        {
            nsMgr.AddNamespace("ns", ns);
        }

        var prefix = string.IsNullOrEmpty(ns) ? "" : "ns:";
        var depNodes = doc.SelectNodes($"//{prefix}dependency", nsMgr);
        if (depNodes != null)
        {
            foreach (XmlNode node in depNodes)
            {
                var depId = node.Attributes?["id"]?.Value;
                var depVersion = node.Attributes?["version"]?.Value;
                if (!string.IsNullOrEmpty(depId) && !string.IsNullOrEmpty(depVersion)
                    && !IgnoredDependencyPrefixes.Any(p => depId.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    // Remove any brackets or parentheses from the version string
                    var cleanedVersion = BracketsAndParenthesesRegex().Replace(depVersion, "");
                    dependencies.TryAdd(depId, cleanedVersion);
                }
            }
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
