// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Shared helper for locating appxmanifest files.
/// </summary>
internal static class ManifestHelper
{
    private static readonly string[] ManifestNames = ["Package.appxmanifest", "AppxManifest.xml", "appxmanifest.xml"];

    /// <summary>
    /// Finds an appxmanifest file in the specified directory.
    /// Checks for <c>Package.appxmanifest</c> first, then <c>AppxManifest.xml</c>,
    /// then <c>appxmanifest.xml</c>. The two .xml spellings are equivalent on
    /// case-insensitive filesystems (NTFS) but differ on case-sensitive ones
    /// (POSIX-style); both spellings are listed in our NuGet targets, so this
    /// helper accepts both as well.
    /// </summary>
    /// <returns>A <see cref="FileInfo"/> for the manifest. Check <see cref="FileInfo.Exists"/> before using.</returns>
    public static FileInfo FindManifest(string directory)
    {
        foreach (var name in ManifestNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return new FileInfo(path);
            }
        }

        // Return a non-existent FileInfo for the primary name so callers can check .Exists
        return new FileInfo(Path.Combine(directory, ManifestNames[0]));
    }
}
