// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;

namespace WinApp.Cli.Helpers;

// Shared filesystem-safety helpers. Centralizing the reparse-point /
// containment check keeps every "write into the user's workspace" site
// (e.g. WinmdsLockfileService) consistent — we don't want one to drift
// behind the others.
internal static class PathSafety
{
    // True if `path` is not safely contained under `boundary`, or if any
    // segment from `boundary` down to `path` is a reparse point, or if
    // either side is a UNC path. Used to refuse rewriting / probing files
    // that a hostile workspace could redirect via a symlink/junction to a
    // victim location elsewhere on the machine.
    //
    // Implementation notes:
    //   * Walks DOWN from `boundary` instead of UP from `path`. Walking up
    //     would force the OS to traverse any junctions / symlinks in `path`
    //     to look up the leaf's attributes, which on Windows can trigger
    //     SMB negotiation (and NTLM leak) before we ever see the
    //     reparse-point flag. Walking down lets us reject as soon as a
    //     suspicious segment is observed, without ever probing past it.
    //   * Uses `File.GetAttributes` rather than `FileInfo.Exists` /
    //     `DirectoryInfo.Exists`; the latter call FindFirstFile internally,
    //     which on a UNC ancestor would also probe the network before the
    //     reparse-point flag can be inspected.
    //   * UNC inputs are rejected outright (long-path `\\?\C:\…` is fine;
    //     `\\server\share` and `\\?\UNC\…` are not).
    //   * Missing segments are skipped (no I/O), so a caller about to
    //     create the file still passes the guard.
    public static bool HasReparsePointOnPath(string path, string boundary)
    {
        string fullPath;
        string fullBoundary;
        try
        {
            fullPath = Path.GetFullPath(path);
            fullBoundary = Path.GetFullPath(boundary);
        }
        catch
        {
            return true;
        }

        if (IsNetworkPath(fullPath) || IsNetworkPath(fullBoundary))
        {
            return true;
        }

        var normalizedBoundary = NormalizeForContainment(fullBoundary);
        var normalizedPath = NormalizeForContainment(fullPath);

        // Containment (string-only — no I/O). The boundary itself is a
        // valid target (path == boundary), otherwise path must live under
        // boundary + a separator. Boundary may already end in a separator
        // (drive root, e.g. `C:\`) — don't double up.
        bool isBoundaryItself = string.Equals(
            normalizedPath,
            normalizedBoundary,
            StringComparison.OrdinalIgnoreCase);
        var boundaryWithSep = normalizedBoundary.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedBoundary
            : normalizedBoundary + Path.DirectorySeparatorChar;
        bool isUnderBoundary = normalizedPath.StartsWith(
            boundaryWithSep,
            StringComparison.OrdinalIgnoreCase);
        if (!isBoundaryItself && !isUnderBoundary)
        {
            return true;
        }

        // Check the boundary itself FIRST. If the boundary is a reparse
        // point, every descendant probe would silently follow it; refuse
        // before we ever touch a descendant path.
        if (TryGetAttributes(normalizedBoundary, out var boundaryAttr)
            && boundaryAttr.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        if (isBoundaryItself)
        {
            return false;
        }

        // Walk DOWN from boundary one segment at a time. The remainder
        // after the boundary cannot contain `..` (Path.GetFullPath
        // normalised it) so each segment is a literal directory / file
        // name.
        var remainder = normalizedPath.Substring(normalizedBoundary.Length);
        var segments = remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = normalizedBoundary;
        foreach (var seg in segments)
        {
            current = Path.Combine(current, seg);
            if (TryGetAttributes(current, out var segAttr)
                && segAttr.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
            // Missing segments are fine — we don't refuse on absence.
        }

        return false;
    }

    // True for UNC / network paths (`\\server\share`, `\\?\UNC\…`,
    // `\\.\UNC\…`). Local DOS device paths (`\\?\C:\…`) are not network.
    // Centralized here so every caller shares the same definition of
    // "a path that would trigger an SMB probe / NTLM leak".
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var p = path.Replace('/', '\\');

        // Plain UNC: \\server\share…  (server is non-empty, not a device
        // designator like '?' or '.').
        if (p.Length >= 3
            && p[0] == '\\' && p[1] == '\\'
            && p[2] != '?' && p[2] != '.')
        {
            return true;
        }

        // Device-prefixed UNC: \\?\UNC\server\… or \\.\UNC\server\…
        if (p.Length >= 8
            && p[0] == '\\' && p[1] == '\\'
            && (p[2] == '?' || p[2] == '.')
            && p[3] == '\\'
            && (p[4] == 'U' || p[4] == 'u')
            && (p[5] == 'N' || p[5] == 'n')
            && (p[6] == 'C' || p[6] == 'c')
            && p[7] == '\\')
        {
            return true;
        }

        return false;
    }

    // Trims trailing separators but preserves the root separator for a
    // bare drive designator. `C:\` would otherwise collapse to `C:` (a
    // drive-relative reference) and the descent loop would then call
    // Path.Combine("C:", seg) — yielding "C:foo" (drive-relative, resolved
    // against the per-drive CWD) instead of "C:\foo". That silently
    // bypasses the reparse-point check for any workspace/config-dir
    // rooted at a drive letter.
    private static string NormalizeForContainment(string path)
    {
        var trimmed = TrimTrailingSeparators(path);
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return trimmed + Path.DirectorySeparatorChar;
        }
        return trimmed;
    }

    private static string TrimTrailingSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch
        {
            // Any other error (access denied, IO, etc.): treat as "no
            // attributes available". Callers can still refuse the
            // operation when they hit the actual read/write.
            attributes = default;
            return false;
        }
    }

    // Write `contents` to `path` atomically: stage to a sibling temp file
    // (same volume so the move stays atomic), flush to disk, then rename
    // over the destination. Prevents a crash / power loss mid-write from
    // leaving the file truncated or empty. Supports cancellation while
    // staging (cleanup still runs).
    public static async Task AtomicWriteAllTextAsync(
        string path,
        string contents,
        System.Text.Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            await using (var sw = new StreamWriter(fs, encoding))
            {
                await sw.WriteAsync(contents.AsMemory(), cancellationToken);
                await sw.FlushAsync(cancellationToken);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup; surface original error.
            }
            throw;
        }
    }
}
