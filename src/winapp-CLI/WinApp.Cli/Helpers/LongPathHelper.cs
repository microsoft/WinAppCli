// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Win32;
using Windows.Win32;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Utilities for handling paths that exceed the Windows MAX_PATH (260 character) limit.
/// </summary>
internal static class LongPathHelper
{
    private const int MaxPath = 260;
    private const string ExtendedLengthPathPrefix = @"\\?\";

    /// <summary>
    /// Checks whether the system-level long path support is enabled via the
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled</c> registry key.
    /// </summary>
    internal static bool IsSystemLongPathEnabled()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates that a path can be used for package operations. If the path exceeds MAX_PATH
    /// and the system does not have long path support enabled, throws an <see cref="InvalidOperationException"/>
    /// with an actionable message.
    /// </summary>
    internal static void ValidatePathLength(string path)
    {
        if (path.Length <= MaxPath)
        {
            return;
        }

        if (!IsSystemLongPathEnabled())
        {
            throw new InvalidOperationException(
                $"The path exceeds the Windows MAX_PATH limit of {MaxPath} characters and long path support is not enabled on this system. " +
                "To fix this, either:\n" +
                "  1. Enable long paths: run 'reg add HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem /v LongPathsEnabled /t REG_DWORD /d 1 /f' as Administrator and restart your terminal, or\n" +
                "  2. Use a shorter directory path.");
        }
    }

    /// <summary>
    /// Returns the path with the <c>\\?\</c> extended-length prefix if the path exceeds MAX_PATH
    /// and does not already have it. This prefix bypasses the MAX_PATH limit for Win32 file I/O APIs.
    /// Does not apply to UNC paths (<c>\\server\share</c>).
    /// </summary>
    internal static string EnsureExtendedLengthPrefix(string path)
    {
        if (path.Length <= MaxPath)
        {
            return path;
        }

        if (path.StartsWith(ExtendedLengthPathPrefix, StringComparison.Ordinal))
        {
            return path;
        }

        // UNC paths need \\?\UNC\ prefix instead
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return ExtendedLengthPathPrefix + path;
    }

    /// <summary>
    /// Converts the directory portion of a long path to its short (8.3) form using the Win32
    /// <c>GetShortPathName</c> API, preserving the original filename. WinRT deployment APIs
    /// (PackageManager) do not support extended-length paths or symlinks, and require specific
    /// filenames like <c>AppxManifest.xml</c>, so only the directory is shortened.
    /// Returns the original path unchanged if it is already within MAX_PATH or if 8.3 name
    /// generation is not available.
    /// </summary>
    internal static string GetShortPath(string path)
    {
        if (path.Length < MaxPath)
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);

        if (string.IsNullOrEmpty(directory))
        {
            return path;
        }

        var shortDir = GetShortPathRaw(directory);
        var result = Path.Combine(shortDir, fileName);

        return result.Length < MaxPath ? result : path;
    }

    /// <summary>
    /// Converts an entire path (including filename) to its short (8.3) form.
    /// </summary>
    private static string GetShortPathRaw(string path)
    {
        // GetShortPathName needs the \\?\ prefix to accept paths > MAX_PATH
        var extendedPath = EnsureExtendedLengthPrefix(path);

        unsafe
        {
            fixed (char* pInput = extendedPath)
            {
                var bufferSize = PInvoke.GetShortPathName(pInput, null, 0);
                if (bufferSize == 0)
                {
                    return path;
                }

                Span<char> buffer = stackalloc char[(int)bufferSize];
                fixed (char* pBuffer = buffer)
                {
                    var result = PInvoke.GetShortPathName(pInput, new Windows.Win32.Foundation.PWSTR(pBuffer), bufferSize);
                    if (result == 0)
                    {
                        return path;
                    }

                    var shortPath = new string(pBuffer, 0, (int)result);

                    // Strip the \\?\ prefix if GetShortPathName preserved it
                    if (shortPath.StartsWith(ExtendedLengthPathPrefix, StringComparison.Ordinal))
                    {
                        shortPath = shortPath[ExtendedLengthPathPrefix.Length..];
                    }

                    return shortPath;
                }
            }
        }
    }
}
