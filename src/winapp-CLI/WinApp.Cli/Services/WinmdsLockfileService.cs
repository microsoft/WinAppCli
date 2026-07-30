// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// UTF-8 (no BOM) indented JSON, LF endings. Atomic writes via PathSafety.AtomicWriteAllTextAsync.
internal sealed class WinmdsLockfileService(ILogger<WinmdsLockfileService> logger) : IWinmdsLockfileService
{
    public const string LockfileName = "winmds.lock.json";

    public FileInfo GetLockfilePath(DirectoryInfo winappDir) =>
        new(Path.Combine(winappDir.FullName, LockfileName));

    // `.winapp` can be user-planted as a junction/UNC target before winapp runs,
    // so the lockfile path is untrusted even though winapp normally manages it.
    private static bool IsLockfilePathUnsafe(DirectoryInfo winappDir, FileInfo lockfilePath)
    {
        // Use the workspace as boundary when discoverable.
        // PathSafety checks the boundary too, so `.winapp` itself is covered.
        var boundary = winappDir.Parent?.FullName ?? winappDir.FullName;
        return PathSafety.HasReparsePointOnPath(lockfilePath.FullName, boundary);
    }

    public async Task WriteAsync(
        DirectoryInfo winappDir,
        IReadOnlyDictionary<string, string> usedVersions,
        IReadOnlyList<FileInfo> discoveredWinmds,
        DirectoryInfo nugetCacheDir,
        string? yamlPackagesHash = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetLockfilePath(winappDir);
            if (IsLockfilePathUnsafe(winappDir, path))
            {
                // Lockfile is optional; skip unsafe writes so live discovery can proceed.
                logger.LogDebug(
                    "Skipping winmds lockfile write at {LockfilePath}: .winapp or one of its ancestors is a symlink / reparse point.",
                    path.FullName);
                return;
            }

            winappDir.Create();
            var lockfile = BuildLockfile(usedVersions, discoveredWinmds, nugetCacheDir, yamlPackagesHash);
            var json = JsonSerializer.Serialize(lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);

            // Shared helper owns staging + fsync + rename semantics.
            await PathSafety.AtomicWriteAllTextAsync(
                path.FullName,
                json + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            logger.LogDebug(
                "Wrote winmds lockfile ({PackageCount} packages, {WinmdCount} winmds) → {LockfilePath}",
                lockfile.Packages.Count, discoveredWinmds.Count, path.FullName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lockfile is optional.
            logger.LogDebug(ex, "Failed to write winmds lockfile (continuing without)");
        }
    }

    public async Task<WinmdsLockfile?> TryReadAsync(
        DirectoryInfo winappDir,
        CancellationToken cancellationToken = default)
    {
        var path = GetLockfilePath(winappDir);
        if (IsLockfilePathUnsafe(winappDir, path))
        {
            // Unsafe reads fall back to live discovery.
            logger.LogDebug(
                "Skipping winmds lockfile read at {LockfilePath}: .winapp or one of its ancestors is a symlink / reparse point.",
                path.FullName);
            return null;
        }

        if (!path.Exists)
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path.FullName);
            var lockfile = await JsonSerializer.DeserializeAsync(
                stream,
                WinmdsLockfileJsonContext.Default.WinmdsLockfile,
                cancellationToken);

            if (lockfile is null)
            {
                logger.LogDebug("Winmds lockfile {LockfilePath} deserialized to null; ignoring", path.FullName);
                return null;
            }

            if (lockfile.Schema != WinmdsLockfile.CurrentSchema)
            {
                logger.LogDebug(
                    "Winmds lockfile {LockfilePath} schema mismatch (got {Got}, expected {Expected}); ignoring",
                    path.FullName, lockfile.Schema, WinmdsLockfile.CurrentSchema);
                return null;
            }

            return lockfile;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read winmds lockfile {LockfilePath}; falling back to live discovery", path.FullName);
            return null;
        }
    }

    // Bucket winmds by package; paths off the NuGet cache layout are dropped.
    // Classification stays in the npm wrapper so policy changes don't require native redeploy.
    internal static WinmdsLockfile BuildLockfile(
        IReadOnlyDictionary<string, string> usedVersions,
        IReadOnlyList<FileInfo> discoveredWinmds,
        DirectoryInfo nugetCacheDir,
        string? yamlPackagesHash = null)
    {
        // NuGet cache layout is lowercase; output keeps usedVersions casing.
        var winmdsByPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in discoveredWinmds)
        {
            var pkgIdLc = PackageLayoutService.TryGetPackageIdFromPath(nugetCacheDir, w.FullName);
            if (pkgIdLc is null)
            {
                continue;
            }
            if (!winmdsByPackage.TryGetValue(pkgIdLc, out var list))
            {
                list = new List<string>();
                winmdsByPackage[pkgIdLc] = list;
            }
            list.Add(w.FullName);
        }

        var packages = new List<WinmdsLockfilePackage>(usedVersions.Count);
        foreach (var (name, version) in usedVersions)
        {
            var pkgIdLc = name.ToLowerInvariant();
            winmdsByPackage.TryGetValue(pkgIdLc, out var winmds);
            packages.Add(new WinmdsLockfilePackage
            {
                Name = name,
                Version = version,
                Winmds = winmds is null
                    ? new List<string>()
                    : winmds.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            });
        }

        // Stable diff-friendly order: alpha by package name.
        packages.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new WinmdsLockfile
        {
            Schema = WinmdsLockfile.CurrentSchema,
            GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
            NugetCacheDir = nugetCacheDir.FullName,
            YamlPackagesHash = yamlPackagesHash,
            Packages = packages,
        };
    }
}
