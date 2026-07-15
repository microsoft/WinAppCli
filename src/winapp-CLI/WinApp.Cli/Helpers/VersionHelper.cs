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

        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = assembly.GetName().Version;
        return FormatVersion(infoVersion, version);
    }

    /// <summary>
    /// Formats a version string from the informational version (preferred, with any git-hash
    /// suffix stripped) or falls back to the numeric assembly version, then to <c>"0.0.0"</c>.
    /// Extracted as a pure function so the fallback branches can be unit tested without the
    /// runtime assembly's fixed attributes.
    /// </summary>
    internal static string FormatVersion(string? infoVersion, Version? asmVersion)
    {
        // Try to get informational version first (includes git info if available)
        if (!string.IsNullOrEmpty(infoVersion))
        {
            // Remove git hash suffix if present (e.g., "0.1.8+abc123" -> "0.1.8")
            var plusIndex = infoVersion.IndexOf('+');
            return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
        }

        // Fall back to assembly version
        return asmVersion != null ? $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}" : "0.0.0";
    }
}
