// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure mapping between a target architecture, a .NET runtime identifier (RID), and the MSBuild
/// <c>Platform</c> value used by WinUI projects. Project mode passes these on the build command line
/// (never written to the csproj). See spec §8.2.
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
    /// Maps a canonical arch to the WinUI MSBuild <c>Platform</c> value (<c>x64</c> / <c>ARM64</c> / <c>x86</c>).
    /// </summary>
    public static string ToPlatform(string architecture) => architecture switch
    {
        "arm64" => "ARM64",
        "x86" => "x86",
        _ => "x64",
    };

    /// <summary>
    /// Extracts the canonical arch from a RID such as <c>win-x64</c> or <c>win10-arm64</c>.
    /// </summary>
    /// <returns>The canonical arch, or <c>null</c> when the RID has no recognized arch suffix.</returns>
    public static string? ArchitectureFromRid(string? rid)
    {
        if (string.IsNullOrWhiteSpace(rid))
        {
            return null;
        }

        var dash = rid.LastIndexOf('-');
        var suffix = dash >= 0 ? rid[(dash + 1)..] : rid;
        return NormalizeArchitecture(suffix);
    }
}
