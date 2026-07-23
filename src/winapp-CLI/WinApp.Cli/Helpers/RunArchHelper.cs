// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure mapping between a target architecture and a .NET runtime identifier (RID). Project mode passes
/// the RID on the build command line (never written to the csproj) and conveys the target architecture
/// through it alone — it does not force an MSBuild <c>Platform</c>.
/// </summary>
internal static class RunArchHelper
{
    /// <summary>The architectures winapp accepts for <c>--arch</c>.</summary>
    public static readonly IReadOnlyList<string> SupportedArchitectures = ["x64", "arm64", "x86"];

    /// <summary>
    /// The default target architecture: the current process arch (mirrors folder-mode behavior and
    /// what a developer expects when running locally). Canonicalized to <c>x64</c> / <c>arm64</c> /
    /// <c>x86</c>, falling back to <c>x64</c> for any unrecognized process architecture.
    /// </summary>
    public static string DefaultArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64", // Default fallback
    };

    /// <summary>
    /// Normalizes user input (<c>--arch</c> value or the arch segment of a RID) to canonical
    /// <c>x64</c> / <c>arm64</c> / <c>x86</c>.
    /// </summary>
    /// <returns>The canonical arch, or <c>null</c> when <paramref name="value"/> is not recognized.</returns>
    public static string? NormalizeArchitecture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "x64" or "amd64" or "x86-64" or "x86_64" => "x64",
            "arm64" or "aarch64" => "arm64",
            "x86" or "win32" or "ia32" => "x86",
            _ => null,
        };
    }

    /// <summary>Maps a canonical arch to its .NET RID (e.g. <c>x64</c> → <c>win-x64</c>).</summary>
    public static string ToRuntimeIdentifier(string architecture) => $"win-{architecture}";

    /// <summary>
    /// Extracts the canonical arch from a Windows RID such as <c>win-x64</c> or <c>win10-arm64</c>.
    /// Project mode conveys only the architecture and always rebuilds the canonical <c>win-&lt;arch&gt;</c>
    /// RID, so a non-Windows RID (<c>linux-x64</c>, <c>osx-arm64</c>) is rejected rather than silently
    /// reduced to a Windows target the user never asked for.
    /// </summary>
    /// <returns>
    /// The canonical arch, or <c>null</c> when the RID has no recognized arch suffix or carries a
    /// non-Windows OS prefix. A bare architecture (no OS prefix, e.g. <c>x64</c>) is still accepted.
    /// </returns>
    public static string? ArchitectureFromRid(string? rid)
    {
        if (string.IsNullOrWhiteSpace(rid))
        {
            return null;
        }

        var trimmed = rid.Trim();
        var dash = trimmed.LastIndexOf('-');
        var arch = NormalizeArchitecture(dash >= 0 ? trimmed[(dash + 1)..] : trimmed);
        if (arch == null)
        {
            return null;
        }

        // When an OS portion is present it must be a Windows RID; only the architecture is honored, but a
        // foreign OS (linux/osx/…) signals the user meant a different runtime target we can't produce.
        if (dash >= 0 && !trimmed[..dash].StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return arch;
    }
}
