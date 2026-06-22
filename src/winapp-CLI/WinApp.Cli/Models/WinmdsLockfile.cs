// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

// WinMD inventory written by restore and consumed by the npm JS-bindings flow.
internal sealed class WinmdsLockfile
{
    // Current schema version. Bump on breaking shape changes.
    public const int CurrentSchema = 3;

    // Schema version this file was produced with.
    public int Schema { get; set; } = CurrentSchema;

    // ISO-8601 UTC timestamp the lockfile was written.
    public string GeneratedAt { get; set; } = string.Empty;

    // NuGet global packages dir at write time. Diagnostic-only.
    public string? NugetCacheDir { get; set; }

    // SHA-256 of the yaml packages: block. The JS bindings step treats the
    // lockfile as stale on mismatch and re-discovers winmds from the cache.
    public string? YamlPackagesHash { get; set; }

    // One entry per resolved package (direct + transitive).
    public List<WinmdsLockfilePackage> Packages { get; set; } = new();
}

// One package entry in WinmdsLockfile.
internal sealed class WinmdsLockfilePackage
{
    // NuGet package ID (original casing).
    public string Name { get; set; } = string.Empty;

    // Resolved version.
    public string Version { get; set; } = string.Empty;

    // Absolute paths of every .winmd found for this package.
    public List<string> Winmds { get; set; } = new();
}

// Source-generated JSON context: snake_case, indented, LF endings.
[JsonSerializable(typeof(WinmdsLockfile))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class WinmdsLockfileJsonContext : JsonSerializerContext;
