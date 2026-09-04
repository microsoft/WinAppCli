// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Tests;

/// <summary>
/// Creates and removes the reparse points the no-follow walks are tested against.
/// </summary>
/// <remarks>
/// Shared because the same links have to be planted on both sides of the boundary: the host walk
/// that decides what is sent, and the guest walk that decides what is reported back. Testing only
/// one of them would leave the other's rule asserted nowhere.
/// <para>
/// Every creator returns <see langword="false"/> rather than throwing when the machine will not
/// allow the link, so a caller can report inconclusive instead of passing without having tested
/// anything.
/// </para>
/// </remarks>
internal static class LinkTestHelpers
{
    /// <summary>
    /// Replaces an existing file with a link, keeping its original timestamp.
    /// </summary>
    /// <remarks>
    /// The timestamp is preserved so the replacement is indistinguishable to any check that stats
    /// without following, which is what makes a leaf-swap test depend on the leaf check rather than
    /// on an unrelated safety net.
    /// </remarks>
    public static bool TryReplaceWithLink(string path, string fileTarget, string directoryTarget)
    {
        DateTime lastWriteUtc;

        try
        {
            lastWriteUtc = File.GetLastWriteTimeUtc(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            File.CreateSymbolicLink(path, fileTarget);
            TryPreserveTimestamp(path, lastWriteUtc);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode or elevation; fall back to a junction.
        }

        if (!TryCreateJunction(path, directoryTarget))
        {
            return false;
        }

        TryPreserveTimestamp(path, lastWriteUtc);
        return true;
    }

    private static void TryPreserveTimestamp(string path, DateTime lastWriteUtc)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The link is in place either way; only the disguise is best effort.
        }
    }

    /// <summary>
    /// Creates a directory link, preferring a junction.
    /// </summary>
    /// <remarks>
    /// A junction is a reparse point that any user can create, so this exercises the real defect on
    /// an ordinary developer machine. A symbolic link needs Developer Mode or elevation and is only
    /// the fallback.
    /// </remarks>
    public static bool TryCreateDirectoryLink(string linkPath, string target) =>
        TryCreateJunction(linkPath, target) || TryCreateSymbolicLink(linkPath, target);

    public static bool TryCreateJunction(string linkPath, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                ArgumentList = { "/c", "mklink", "/J", linkPath, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(milliseconds: 30_000);

            return Directory.Exists(linkPath)
                && new DirectoryInfo(linkPath).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool TryCreateSymbolicLink(string linkPath, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a temp tree without ever following a link out of it.
    /// </summary>
    /// <remarks>
    /// Cleanup has to obey the same rule the code under test does. A recursive delete that followed
    /// a junction would delete the contents of the "outside" folder these tests assert about, and a
    /// recursive <em>enumeration</em> that followed one would not terminate against the
    /// self-referencing link. So links are unlinked in place, depth-first, and only real directories
    /// are descended into.
    /// </remarks>
    public static void TryDeleteDirectory(string path)
    {
        UnlinkReparsePoints(path);

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Temp cleanup is not worth failing a test over.
        }
    }

    /// <summary>Removes every link beneath a directory, link itself only, never its target.</summary>
    private static void UnlinkReparsePoints(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        FileSystemInfo[] entries;

        try
        {
            entries = new DirectoryInfo(path).GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Directory.Delete on a junction removes the link and leaves the target alone.
                    if (entry is DirectoryInfo)
                    {
                        Directory.Delete(entry.FullName);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }

                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    UnlinkReparsePoints(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // Best effort.
            }
        }
    }
}
