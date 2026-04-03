// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Shared helper for locating appxmanifest.xml files.
/// </summary>
internal static class ManifestHelper
{
    private static readonly string[] ManifestNames = ["appxmanifest.xml", "Package.appxmanifest"];

    /// <summary>
    /// Finds an appxmanifest file in the specified directory.
    /// Checks for <c>appxmanifest.xml</c> first, then <c>Package.appxmanifest</c>.
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
