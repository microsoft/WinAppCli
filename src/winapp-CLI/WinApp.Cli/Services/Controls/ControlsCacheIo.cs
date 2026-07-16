// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Small file-IO helpers for the per-user find-ui cache. The upstream
/// winui-search tool spawned a detached background refresher; find-ui instead
/// refreshes on demand (cold cache, or an explicit refresh) so nothing runs
/// behind the user's back. Only the atomic-write and timestamp-read primitives
/// are retained here.
/// </summary>
internal static class ControlsCacheIo
{
    /// <summary>
    /// Parse a round-trip ("o" format) timestamp from <paramref name="path"/>, returning a
    /// UTC <see cref="DateTime"/> or <c>null</c> if the file is missing/unreadable/unparseable.
    /// </summary>
    public static DateTime? ReadTimestamp(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            if (DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var dt))
            {
                return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> via a temp file
    /// + rename. The rename is atomic on Windows for same-volume moves, so a crash
    /// mid-write can never leave a truncated/corrupted file — readers see either the
    /// previous contents or the full new contents, never a partial write.
    /// </summary>
    public static void AtomicWriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
