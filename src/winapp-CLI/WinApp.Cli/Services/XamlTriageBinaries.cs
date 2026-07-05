// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinApp.Cli.Services;

/// <summary>
/// Native debugging binaries required to host DbgEng and run the WinUI JavaScript
/// extension (<c>!xamlstowed</c> / <c>!xamltriage</c>).
/// </summary>
/// <param name="BinDir">Directory containing <c>dbgeng.dll</c> (and co-located providers).</param>
/// <param name="JsProviderPath">Full path to the resolved <c>JsProvider.dll</c> (may live in a
/// <c>winext</c> subfolder rather than directly in <see cref="BinDir"/>).</param>
/// <param name="HasSymSrv"><c>symsrv.dll</c> is co-located, enabling <c>srv*</c> symbol paths.</param>
/// <param name="Source">Human-readable description of where the binaries were resolved from.</param>
internal sealed record ResolvedTriageBinaries(string BinDir, string JsProviderPath, bool HasSymSrv, string Source);

/// <summary>
/// Locates (and, when missing, downloads on first use) the host-architecture native
/// debugging binaries needed by <see cref="XamlTriageService"/>.
/// <para>
/// Resolution precedence:
/// <list type="number">
///   <item><c>WINAPP_DBGTOOLS_DIR</c> environment override.</item>
///   <item>An installed copy of <em>Debugging Tools for Windows</em> (Windows Kits).</item>
///   <item>A download-on-first-use cache populated from NuGet (mirrors the tool command).</item>
/// </list>
/// </para>
/// <para>
/// <c>JsProvider.dll</c> (the JS scripting host for <c>.scriptload</c>) is <strong>not</strong>
/// distributed on NuGet — it ships only inside the WinDbg bundle. The engine bits come from NuGet
/// (global cache or download), while <c>JsProvider.dll</c> is acquired separately via
/// <see cref="WinDbgJsProviderAcquirer"/>; when neither can be obtained the caller degrades
/// gracefully.
/// </para>
/// </summary>
internal static class XamlTriageBinaries
{
    /// <summary>
    /// Authoritative override directory containing a full debugger layout (dbgeng + JsProvider).
    /// When set, only this directory is considered (installed tools and cache are skipped).
    /// </summary>
    public const string EnvOverride = "WINAPP_DBGTOOLS_DIR";

    /// <summary>
    /// <c>true</c> when <see cref="EnvOverride"/> is configured. The override is authoritative:
    /// installed tools, the download-on-first-use cache, and cache acquisition are all skipped so the
    /// override remains the single source of truth (see <see cref="CandidateDirectories"/>).
    /// </summary>
    public static bool IsEnvOverrideSet =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvOverride));

    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer";

    /// <summary>
    /// Pinned native-debugger package version. Must stay in sync with the
    /// <c>Microsoft.Debugging.Platform.*</c> entries in <c>Directory.Packages.props</c>; a unit test
    /// asserts they match so runtime acquisition uses the same version that <c>dotnet restore</c> pins.
    /// </summary>
    public const string DbgPackageVersion = "20260319.1511.0";

    // Native engine bits available from NuGet. DbgEng ships the full engine layout (including
    // dbgmodel.dll and msdia140.dll), so no separate DbgX package is required. JsProvider.dll is
    // intentionally absent here — it is not on NuGet and is acquired from the WinDbg bundle instead.
    private static readonly (string Package, string Version, string[] Files)[] NuGetComponents =
    [
        ("Microsoft.Debugging.Platform.DbgEng", DbgPackageVersion, ["dbgeng.dll", "dbghelp.dll", "dbgcore.dll", "dbgmodel.dll", "msdia140.dll"]),
        ("Microsoft.Debugging.Platform.SymSrv", DbgPackageVersion, ["symsrv.dll"]),
    ];

    /// <summary>Folder token used by the Windows Kits Debuggers layout for the host arch.</summary>
    public static string KitsArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    /// <summary>Folder token used by the NuGet debugging packages for the host arch.</summary>
    public static string NuGetArch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "amd64",
    };

    /// <summary>
    /// Resolves an existing directory that contains both <c>dbgeng.dll</c> and
    /// <c>JsProvider.dll</c> for the host architecture, or <c>null</c> when none is found.
    /// </summary>
    public static ResolvedTriageBinaries? ResolveExisting(DirectoryInfo cacheBinDir, ILogger logger)
    {
        foreach (var (dir, source) in CandidateDirectories(cacheBinDir))
        {
            var resolved = TryDirectory(dir, source);
            if (resolved != null)
            {
                logger.LogDebug("Resolved WinUI triage debugging binaries from {Source}: {Dir}", source, dir);
                return resolved;
            }
        }

        return null;
    }

    private static IEnumerable<(string Dir, string Source)> CandidateDirectories(DirectoryInfo cacheBinDir)
    {
        // An explicit override is authoritative: when set, only that directory is considered.
        if (IsEnvOverrideSet)
        {
            var overrideDir = Environment.GetEnvironmentVariable(EnvOverride)!;
            yield return (overrideDir, $"{EnvOverride} override");
            yield break;
        }

        foreach (var root in InstalledDebuggerRoots())
        {
            yield return (Path.Combine(root, "Windows Kits", "10", "Debuggers", KitsArch), "installed Debugging Tools for Windows");
        }

        yield return (cacheBinDir.FullName, "download-on-first-use cache");
    }

    private static IEnumerable<string> InstalledDebuggerRoots()
    {
        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramW6432", "ProgramFiles" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Returns a resolved descriptor when <paramref name="dir"/> contains a usable engine
    /// (dbgeng.dll) and a co-located JsProvider.dll (in the directory or its <c>winext</c> child).
    /// </summary>
    private static ResolvedTriageBinaries? TryDirectory(string dir, string source)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var dbgeng = Path.Combine(dir, "dbgeng.dll");
        if (!File.Exists(dbgeng))
        {
            return null;
        }

        // dbgeng searches its own directory and the winext subfolder for extension providers, but the
        // child runner must .load JsProvider.dll by explicit path, so capture where it actually lives.
        var jsProviderPath = new[]
        {
            Path.Combine(dir, "JsProvider.dll"),
            Path.Combine(dir, "winext", "JsProvider.dll"),
        }.FirstOrDefault(File.Exists);
        if (jsProviderPath == null)
        {
            return null;
        }

        var hasSymSrv = File.Exists(Path.Combine(dir, "symsrv.dll"));
        return new ResolvedTriageBinaries(dir, jsProviderPath, hasSymSrv, source);
    }

    /// <summary>
    /// Returns <c>true</c> when the cache directory already contains the native engine
    /// (<c>dbgeng.dll</c>), independent of whether <c>JsProvider.dll</c> is present yet.
    /// </summary>
    public static bool HasEngine(DirectoryInfo cacheBinDir) =>
        File.Exists(Path.Combine(cacheBinDir.FullName, "dbgeng.dll"));

    /// <summary>
    /// Best-effort population of the cache directory with the NuGet-available native debugging bits.
    /// Prefers copying from the NuGet global packages cache (populated by <c>dotnet restore</c>) and
    /// falls back to downloading the flat-container <c>.nupkg</c> on first use. Does not acquire
    /// <c>JsProvider.dll</c>. Returns the number of component packages successfully materialized.
    /// </summary>
    public static async Task<int> TryAcquireFromNuGetAsync(
        DirectoryInfo cacheBinDir, DirectoryInfo? nugetCacheDir, ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheBinDir.FullName);
        using var http = new HttpClient();
        var acquired = 0;

        foreach (var (package, version, files) in NuGetComponents)
        {
            try
            {
                if (files.All(f => File.Exists(Path.Combine(cacheBinDir.FullName, f))))
                {
                    acquired++;
                    continue;
                }

                if (nugetCacheDir != null && TryCopyFromGlobalCache(package, version, files, nugetCacheDir, cacheBinDir, logger))
                {
                    acquired++;
                    continue;
                }

                if (await TryMaterializePackageAsync(http, package, version, files, cacheBinDir, logger, cancellationToken))
                {
                    acquired++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to acquire debugging component {Package} from NuGet.", package);
            }
        }

        return acquired;
    }

    /// <summary>
    /// Copies the required files for a component from the NuGet global packages cache
    /// (<c>&lt;cache&gt;/&lt;id&gt;/&lt;version&gt;/content/&lt;arch&gt;/</c>). Prefers the
    /// <paramref name="pinnedVersion"/> (the version pinned in <c>Directory.Packages.props</c> and
    /// guaranteed present after <c>dotnet restore</c>) and only falls back to the newest cached
    /// version when the pinned one is absent, so dev/CI builds are deterministic.
    /// </summary>
    internal static bool TryCopyFromGlobalCache(
        string package, string pinnedVersion, string[] files, DirectoryInfo nugetCacheDir, DirectoryInfo cacheBinDir, ILogger logger)
    {
        var packageDir = new DirectoryInfo(Path.Combine(nugetCacheDir.FullName, package.ToLowerInvariant()));
        if (!packageDir.Exists)
        {
            return false;
        }

        // Pinned version first (deterministic); then newest available as a graceful fallback.
        var pinnedDir = new DirectoryInfo(Path.Combine(packageDir.FullName, pinnedVersion));
        var candidates = new[] { pinnedDir }
            .Where(d => d.Exists)
            .Concat(packageDir.EnumerateDirectories()
                .Where(d => !d.Name.Equals(pinnedVersion, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase));

        foreach (var versionDir in candidates)
        {
            var archDir = Path.Combine(versionDir.FullName, "content", NuGetArch);
            if (!Directory.Exists(archDir) || !files.All(f => File.Exists(Path.Combine(archDir, f))))
            {
                continue;
            }

            foreach (var file in files)
            {
                File.Copy(Path.Combine(archDir, file), Path.Combine(cacheBinDir.FullName, file), overwrite: true);
            }

            if (!versionDir.Name.Equals(pinnedVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Pinned version {Pinned} of {Package} not in NuGet cache; used {Used} instead.",
                    pinnedVersion, package, versionDir.Name);
            }

            logger.LogDebug("Copied {Count} file(s) for {Package} from NuGet global cache {Version}.", files.Length, package, versionDir.Name);
            return true;
        }

        return false;
    }

    private static async Task<bool> TryMaterializePackageAsync(
        HttpClient http, string package, string pinnedVersion, string[] files, DirectoryInfo cacheBinDir, ILogger logger, CancellationToken cancellationToken)
    {
        var id = package.ToLowerInvariant();
        var version = await ResolveDownloadVersionAsync(http, id, pinnedVersion, logger, cancellationToken);
        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        // Download and extract the .nupkg (a zip archive) to a temp directory.
        var nupkgUrl = $"{FlatContainer}/{id}/{version}/{id}.{version}.nupkg";
        using var nupkgResponse = await http.GetAsync(nupkgUrl, cancellationToken);
        if (!nupkgResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var tempPkgDir = Path.Combine(Path.GetTempPath(), $"winapp-dbgtools-{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPkgDir);
        try
        {
            await using (var nupkgStream = await nupkgResponse.Content.ReadAsStreamAsync(cancellationToken))
            using (var archive = new ZipArchive(nupkgStream, ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tempPkgDir, overwriteFiles: true);
            }

            var copied = 0;
            foreach (var file in files)
            {
                var source = FindBestArchMatch(tempPkgDir, file);
                if (source != null)
                {
                    File.Copy(source, Path.Combine(cacheBinDir.FullName, file), overwrite: true);
                    copied++;
                }
            }

            if (copied > 0)
            {
                logger.LogDebug("Materialized {Count}/{Total} file(s) from {Package} {Version}.", copied, files.Length, package, version);
            }

            return copied == files.Length;
        }
        finally
        {
            try { Directory.Delete(tempPkgDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Resolves the version to download: the pinned version when the flat-container index lists it
    /// (the deterministic, expected path), otherwise the latest stable version as a graceful fallback.
    /// </summary>
    private static async Task<string?> ResolveDownloadVersionAsync(
        HttpClient http, string id, string pinnedVersion, ILogger logger, CancellationToken cancellationToken)
    {
        using var indexResponse = await http.GetAsync($"{FlatContainer}/{id}/index.json", cancellationToken);
        if (!indexResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var indexStream = await indexResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var indexDoc = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);
        var versions = indexDoc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        if (versions.Any(v => string.Equals(v, pinnedVersion, StringComparison.OrdinalIgnoreCase)))
        {
            return pinnedVersion;
        }

        var latest = versions.LastOrDefault(v => !v!.Contains('-', StringComparison.Ordinal));
        if (latest != null)
        {
            logger.LogDebug("Pinned version {Pinned} of {Id} not available on the feed; downloading {Latest} instead.",
                pinnedVersion, id, latest);
        }

        return latest;
    }

    /// <summary>
    /// Finds the copy of <paramref name="fileName"/> whose path best matches the host
    /// architecture, falling back to any match. Prefers paths containing the host arch token.
    /// </summary>
    private static string? FindBestArchMatch(string root, string fileName)
    {
        var matches = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var archTokens = new[] { NuGetArch, KitsArch };
        var preferred = matches.FirstOrDefault(m =>
            archTokens.Any(token => m.Contains(Path.DirectorySeparatorChar + token + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

        return preferred ?? matches[0];
    }
}
