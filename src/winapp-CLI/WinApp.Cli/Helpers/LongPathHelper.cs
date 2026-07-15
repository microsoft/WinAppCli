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
        ValidatePathLength(path, IsSystemLongPathEnabled());
    }

    /// <summary>
    /// Pure core of <see cref="ValidatePathLength(string)"/>: throws when <paramref name="path"/>
    /// exceeds MAX_PATH and <paramref name="longPathEnabled"/> is <c>false</c>. Split out so the
    /// throwing branch is unit testable without depending on the machine's registry state.
    /// </summary>
    internal static void ValidatePathLength(string path, bool longPathEnabled)
    {
        if (path.Length <= MaxPath)
        {
            return;
        }

        if (!longPathEnabled)
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
    internal static string GetShortPath(string path) => GetShortPath(path, NativeGetShortPathName);

    /// <summary>
    /// Core of <see cref="GetShortPath(string)"/> with the native 8.3 shortener injected as
    /// <paramref name="shortener"/> (returning the shortened path, or <c>null</c> when it cannot
    /// shorten). The seam lets unit tests drive the prefix-stripping and failure paths without
    /// depending on the volume's 8.3 name-generation state.
    /// </summary>
    internal static string GetShortPath(string path, Func<string, string?> shortener)
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

        var shortDir = GetShortPathRaw(directory, shortener);
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
    /// Converts an entire path (including filename) to its short (8.3) form via the injected
    /// <paramref name="shortener"/>, then strips the extended-length prefix. Returns the original
    /// path unchanged when the shortener cannot shorten it (returns <c>null</c>) or is unavailable.
    /// </summary>
    private static string GetShortPathRaw(string path, Func<string, string?> shortener)
    {
        // GetShortPathName needs the \\?\ prefix to accept paths > MAX_PATH
        var extendedPath = EnsureExtendedLengthPrefix(path);

        string? shortPath;
        try
        {
            shortPath = shortener(extendedPath);
        }
        catch (DllNotFoundException)
        {
            // GetShortPathName is not available on this platform; return the original path unchanged.
            return path;
        }

        if (shortPath is null)
        {
            return path;
        }

        return StripExtendedPrefix(shortPath);
    }

    /// <summary>
    /// Strips the extended-length prefix that <see cref="EnsureExtendedLengthPrefix"/> may have added.
    /// <c>\\?\UNC\server\share\...</c> must be converted back to <c>\\server\share\...</c> (not to
    /// <c>UNC\server\share\...</c> which would be invalid). Extracted as a pure function for testing.
    /// </summary>
    internal static string StripExtendedPrefix(string shortPath)
    {
        if (shortPath.StartsWith(ExtendedLengthUncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + shortPath[ExtendedLengthUncPrefix.Length..];
        }

        if (shortPath.StartsWith(ExtendedLengthPathPrefix, StringComparison.Ordinal))
        {
            return shortPath[ExtendedLengthPathPrefix.Length..];
        }

        return shortPath;
    }

    /// <summary>
    /// Invokes the Win32 <c>GetShortPathName</c> API. Returns the shortened path, or <c>null</c>
    /// when the API reports failure (buffer size 0). This is the production shortener injected into
    /// <see cref="GetShortPath(string, Func{string, string?})"/>.
    /// </summary>
    private static unsafe string? NativeGetShortPathName(string extendedPath)
    {
        fixed (char* pInput = extendedPath)
        {
            var bufferSize = PInvoke.GetShortPathName(pInput, null, 0);
            if (bufferSize == 0)
            {
                return null;
            }

            Span<char> buffer = stackalloc char[(int)bufferSize];
            fixed (char* pBuffer = buffer)
            {
                var result = PInvoke.GetShortPathName(pInput, new Windows.Win32.Foundation.PWSTR(pBuffer), bufferSize);
                if (result == 0)
                {
                    return null;
                }

                return new string(pBuffer, 0, (int)result);
            }
        }
    }
}
