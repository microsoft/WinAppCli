// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

// Lockfile written by `winapp restore`, consumed by `node jsbindings add`. Optional.
internal sealed class WinmdsLockfile
{
    // Current schema version. Bump on breaking shape changes.
    public const int CurrentSchema = 2;

    // Schema version this file was produced with.
    public int Schema { get; set; } = CurrentSchema;

    // ISO-8601 UTC timestamp the lockfile was written.
    public string GeneratedAt { get; set; } = string.Empty;

    // NuGet global packages dir at write time. Diagnostic-only.
    public string? NugetCacheDir { get; set; }

    // SHA-256 of the yaml packages: block. node jsbindings add
    // treats the lockfile as stale on mismatch.
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

    // emit / refOnly / skip.
    public string Category { get; set; } = "emit";

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
