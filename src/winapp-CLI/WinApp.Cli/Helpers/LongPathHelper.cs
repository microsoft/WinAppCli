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
    private const string ExtendedLengthUncPrefix = @"\\?\UNC\";

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
                $"The path exceeds the Windows MAX_PATH limit of {MaxPath} characters and long path support is not enabled on this system. Visit https://aka.ms/enable-long-paths-on-windows for guidance on enabling long paths.");
        }
    }

    /// <summary>
    /// Returns the path with an extended-length prefix if the path exceeds MAX_PATH
    /// and does not already have one. This bypasses the MAX_PATH limit for Win32 file I/O APIs.
    /// For local paths, the prefix is <c>\\?\</c>. For UNC paths (<c>\\server\share</c>),
    /// the method uses the <c>\\?\UNC\</c> prefix instead.
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
            return ExtendedLengthUncPrefix + path[2..];
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
        if (path.Length <= MaxPath)
        {
            return path;
        }

        var trailingSep = Path.EndsInDirectorySeparator(path);
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);

        if (string.IsNullOrEmpty(directory))
        {
            return path;
        }

        var shortDir = GetShortPathRaw(directory);
        var result = Path.Combine(shortDir, fileName);

        // Preserve trailing separator: Path.Combine drops it when fileName is empty
        if (trailingSep && !Path.EndsInDirectorySeparator(result))
        {
            result += Path.DirectorySeparatorChar;
        }

        return result.Length <= MaxPath ? result : path;
    }

    /// <summary>
    /// Converts the directory portion of a long path to its short (8.3) form, and throws an
    /// <see cref="InvalidOperationException"/> if the path still exceeds MAX_PATH after shortening.
    /// This can happen when 8.3 name generation is disabled on the volume or the path does not
    /// yet exist on disk, causing <c>GetShortPathName</c> to return the original long path.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the path exceeds MAX_PATH and cannot be shortened to a usable length.
    /// </exception>
    internal static string GetShortPathOrThrow(string path)
    {
        var shortPath = GetShortPath(path);
        if (shortPath.Length > MaxPath)
        {
            throw new InvalidOperationException(
                $"The path is too long for the Windows deployment API (limit: {MaxPath} characters) " +
                "and could not be converted to a short (8.3) path. " +
                "This may occur when 8.3 name generation is disabled on the volume or the path does not yet exist. " +
                "To fix this, use a shorter directory path.");
        }

        return shortPath;
    }

    /// <summary>
    /// Converts an entire path (including filename) to its short (8.3) form.
    /// </summary>
    private static string GetShortPathRaw(string path)
    {
        // GetShortPathName needs the \\?\ prefix to accept paths > MAX_PATH
        var extendedPath = EnsureExtendedLengthPrefix(path);

        try
        {
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

                        // Strip the extended-length prefix added by EnsureExtendedLengthPrefix.
                        // \\?\UNC\server\share\... must be converted back to \\server\share\...
                        // not to UNC\server\share\... (which would be invalid).
                        if (shortPath.StartsWith(ExtendedLengthUncPrefix, StringComparison.Ordinal))
                        {
                            shortPath = @"\\" + shortPath[ExtendedLengthUncPrefix.Length..];
                        }
                        else if (shortPath.StartsWith(ExtendedLengthPathPrefix, StringComparison.Ordinal))
                        {
                            shortPath = shortPath[ExtendedLengthPathPrefix.Length..];
                        }

                        return shortPath;
                    }
                }
            }
        }
        catch (DllNotFoundException)
        {
            // GetShortPathName is not available on this platform; return the original path unchanged.
            return path;
        }
    }
}
