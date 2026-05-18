// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

// Reads / writes .winapp/winmds.lock.json. Best-effort; callers fall back
// to live discovery on any error.
internal interface IWinmdsLockfileService
{
    // Absolute path of the lockfile under winappDir.
    FileInfo GetLockfilePath(DirectoryInfo winappDir);

    // Build a WinmdsLockfile from restore's in-memory state and write to disk.
    Task WriteAsync(
        DirectoryInfo winappDir,
        IReadOnlyDictionary<string, string> usedVersions,
        IReadOnlyList<FileInfo> discoveredWinmds,
        DirectoryInfo nugetCacheDir,
        string? yamlPackagesHash = null,
        CancellationToken cancellationToken = default);

    // Read the lockfile if present and schema-compatible; else null. Never throws.
    Task<WinmdsLockfile?> TryReadAsync(
        DirectoryInfo winappDir,
        CancellationToken cancellationToken = default);
}
