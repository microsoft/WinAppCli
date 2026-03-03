// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Provides CLI version information derived from the assembly metadata.
/// </summary>
internal static class VersionHelper
{
    /// <summary>
    /// Gets the CLI version string from the assembly.
    /// Prefers AssemblyInformationalVersion (without git hash suffix),
    /// falls back to AssemblyVersion.
    /// </summary>
    internal static string GetVersionString()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Try to get informational version first (includes git info if available)
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVersion))
        {
            // Remove git hash suffix if present (e.g., "0.1.8+abc123" -> "0.1.8")
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
        }

        // Fall back to assembly version
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }
}
