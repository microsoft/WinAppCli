// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO;

namespace WinApp.Cli.Helpers;

// Filesystem-safety helpers for workspace writes and lockfiles.
internal static class PathSafety
{
    // Rejects paths outside boundary, UNC paths, and reparse points below boundary.
    // Walks downward so symlinks/junctions are detected before they can be followed.
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

        // String-only containment. Boundary itself is a valid target;
        // otherwise path must live under boundary + a separator.
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

        // Check boundary itself first — a reparse-point boundary would make
        // every descendant probe silently follow it.
        if (IsReparseOrProbeUnknown(normalizedBoundary))
        {
            return true;
        }

        if (isBoundaryItself)
        {
            return false;
        }

        var remainder = normalizedPath.Substring(normalizedBoundary.Length);
        var segments = remainder.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        var current = normalizedBoundary;
        foreach (var seg in segments)
        {
            current = Path.Combine(current, seg);
            if (IsReparseOrProbeUnknown(current))
            {
                return true;
            }
        }

        return false;
    }

    // True for UNC / network paths (`\\server\share`, `\\?\UNC\…`,
    // `\\.\UNC\…`). Local DOS device paths (`\\?\C:\…`) are not network.
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var p = path.Replace('/', '\\');

        // Plain UNC: \\server\share…
        if (p.Length >= 3
            && p[0] == '\\' && p[1] == '\\'
            && p[2] != '?' && p[2] != '.')
        {
            return true;
        }

        // Device-prefixed UNC: \\?\UNC\… or \\.\UNC\…
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

    // Preserve `C:\`; `C:` is drive-relative and would probe the wrong path.
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

    // True if the path is a reparse point, OR if attributes cannot be probed
    // for an unknown reason (access denied, IO error). FileNotFound /
    // DirectoryNotFound return false — genuinely absent paths have no reparse
    // metadata to follow. Any other failure biases callers to "unsafe".
    private static bool IsReparseOrProbeUnknown(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch
        {
            // Access denied / IO error — bias to unsafe so callers refuse to follow.
            return true;
        }
    }

    // Stage to a sibling temp file (same volume so the move stays atomic),
    // flush to disk, rename over the destination. Prevents a crash mid-write
    // from leaving the file truncated.
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

    /// <summary>
    /// Synchronous counterpart to <see cref="AtomicWriteAllTextAsync"/>: stage to a sibling
    /// temp file, flush to disk, rename over the destination. Defaults to UTF-8 without a BOM.
    /// </summary>
    public static void AtomicWriteAllText(string path, string contents, System.Text.Encoding? encoding = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096))
            using (var sw = new StreamWriter(fs, encoding ?? Utf8NoBom))
            {
                sw.Write(contents);
                sw.Flush();
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

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
