// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// UTF-8 (no BOM) indented JSON, LF endings. Atomic writes via tmp + File.Move.
internal sealed class WinmdsLockfileService(ILogger<WinmdsLockfileService> logger) : IWinmdsLockfileService
{
    public const string LockfileName = "winmds.lock.json";

    public FileInfo GetLockfilePath(DirectoryInfo winappDir) =>
        new(Path.Combine(winappDir.FullName, LockfileName));

    // Refuse to read/write the lockfile if `.winapp/` (or any segment of
    // its path up to the workspace) is a symlink / junction. The lockfile
    // lives next to user-controlled state; a malicious workspace can plant
    // `.winapp` as a junction to a UNC share or a victim file before
    // winapp ever runs, so we cannot trust the path even though we'd
    // normally consider `.winapp/` winapp-managed.
    private static bool IsLockfilePathUnsafe(DirectoryInfo winappDir, FileInfo lockfilePath)
    {
        // Use the parent of `.winapp` (i.e. the workspace) as the boundary
        // when discoverable. Fall back to `.winapp` itself otherwise (the
        // call still flags the dir being a reparse point because PathSafety
        // checks the boundary).
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
        string? tempPath = null;
        try
        {
            var path = GetLockfilePath(winappDir);
            if (IsLockfilePathUnsafe(winappDir, path))
            {
                // Lockfile is an optimization, not a correctness requirement —
                // log + skip rather than throw, so codegen still proceeds via
                // live discovery.
                logger.LogDebug(
                    "Skipping winmds lockfile write at {LockfilePath}: .winapp or one of its ancestors is a symlink / reparse point.",
                    path.FullName);
                return;
            }

            winappDir.Create();
            var lockfile = BuildLockfile(usedVersions, discoveredWinmds, nugetCacheDir, yamlPackagesHash);

            // Atomic write via tmp + rename; guid suffix avoids concurrent
            // writers colliding on staging.
            tempPath = $"{path.FullName}.tmp.{Guid.NewGuid():N}";
            var json = JsonSerializer.Serialize(lockfile, WinmdsLockfileJsonContext.Default.WinmdsLockfile);
            await File.WriteAllTextAsync(
                tempPath,
                json + "\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(tempPath, path.FullName, overwrite: true);
            tempPath = null;

            logger.LogDebug(
                "Wrote winmds lockfile ({PackageCount} packages, {WinmdCount} winmds) → {LockfilePath}",
                lockfile.Packages.Count, discoveredWinmds.Count, path.FullName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lockfile is an optimization, not a correctness requirement.
            logger.LogDebug(ex, "Failed to write winmds lockfile (continuing without)");
        }
        finally
        {
            // Clean up staging if Move never ran.
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch { /* ignore — leaked tmp file is harmless */ }
            }
        }
    }

    public async Task<WinmdsLockfile?> TryReadAsync(
        DirectoryInfo winappDir,
        CancellationToken cancellationToken = default)
    {
        var path = GetLockfilePath(winappDir);
        if (IsLockfilePathUnsafe(winappDir, path))
        {
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

    // Bucket winmds by package, classify. Paths off the NuGet cache layout
    // are dropped.
    internal static WinmdsLockfile BuildLockfile(
        IReadOnlyDictionary<string, string> usedVersions,
        IReadOnlyList<FileInfo> discoveredWinmds,
        DirectoryInfo nugetCacheDir,
        string? yamlPackagesHash = null)
    {
        // NuGet cache layout is lowercase; bucket by lowercased id. Output
        // entries keep usedVersions's original casing.
        var winmdsByPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in discoveredWinmds)
        {
            var pkgIdLc = JsBindingsPresets.ExtractPackageIdFromPath(w.FullName, nugetCacheDir.FullName);
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
            var category = JsBindingsPresets.ClassifyPackage(name) switch
            {
                WinmdPackageCategory.Skip => "skip",
                WinmdPackageCategory.RefOnly => "refOnly",
                _ => "emit",
            };
            packages.Add(new WinmdsLockfilePackage
            {
                Name = name,
                Version = version,
                Category = category,
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
