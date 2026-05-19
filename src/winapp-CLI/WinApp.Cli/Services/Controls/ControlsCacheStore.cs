// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WinApp.Cli.Services.Controls;

/// <summary>
/// Shared on-disk cache plumbing for the controls fetchers. Centralizes the
/// scenarios.json / tags.json / last-updated.txt / schema-version.txt layout
/// so <see cref="WinUIGalleryFetcher"/> and <see cref="ToolkitFetcher"/> don't
/// each ship their own copy of the same freshness + schema-version checks.
///
/// Freshness rules:
///   • The cached schema version must equal <paramref name="schemaVersion"/>.
///   • The cached timestamp must parse, must be in the past (no clock-skew bypass),
///     and must be within <paramref name="ttl"/> of <see cref="DateTime.UtcNow"/>.
/// </summary>
internal static class ControlsCacheStore
{
    /// <summary>Try to load a fresh, schema-matching cache snapshot. Returns null on any miss.</summary>
    public static (Scenario[] scenarios, Dictionary<string, string[]> tags)? TryLoad(
        string cacheDir,
        string schemaVersion,
        TimeSpan ttl)
    {
        var (scenarioPath, tagPath, timestampPath, versionPath) = GetPaths(cacheDir);

        if (!File.Exists(scenarioPath) || !File.Exists(tagPath) ||
            !File.Exists(timestampPath) || !File.Exists(versionPath))
        {
            return null;
        }

        var cachedVersion = File.ReadAllText(versionPath).Trim();
        if (cachedVersion != schemaVersion)
        {
            return null;
        }

        if (!DateTime.TryParse(File.ReadAllText(timestampPath).Trim(), out var lastUpdated))
        {
            return null;
        }

        // Reject future-dated timestamps so a clock-skew or system-clock reset
        // can't keep stale data alive indefinitely (L2 in PR review).
        if (lastUpdated > DateTime.UtcNow || DateTime.UtcNow - lastUpdated >= ttl)
        {
            return null;
        }

        try
        {
            var s = JsonSerializer.Deserialize(File.ReadAllText(scenarioPath), ControlsJsonContext.Default.ScenarioArray);
            var t = JsonSerializer.Deserialize(File.ReadAllText(tagPath), ControlsJsonContext.Default.DictionaryStringStringArray);
            if (s != null && s.Length > 0 && t != null)
            {
                return (s, t);
            }
        }
        catch
        {
            // Corrupted cache — treat as a miss.
        }
        return null;
    }

    /// <summary>Persist a fresh snapshot to disk, creating <paramref name="cacheDir"/> if needed.</summary>
    public static void Save(
        string cacheDir,
        string schemaVersion,
        Scenario[] scenarios,
        Dictionary<string, string[]> tags)
    {
        var (scenarioPath, tagPath, timestampPath, versionPath) = GetPaths(cacheDir);

        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(scenarioPath, JsonSerializer.Serialize(scenarios, ControlsJsonContext.Default.ScenarioArray));
        File.WriteAllText(tagPath, JsonSerializer.Serialize(tags, ControlsJsonContext.Default.DictionaryStringStringArray));
        File.WriteAllText(timestampPath, DateTime.UtcNow.ToString("o"));
        File.WriteAllText(versionPath, schemaVersion);
    }

    private static (string scenarioPath, string tagPath, string timestampPath, string versionPath) GetPaths(string cacheDir) =>
        (Path.Combine(cacheDir, "scenarios.json"),
         Path.Combine(cacheDir, "tags.json"),
         Path.Combine(cacheDir, "last-updated.txt"),
         Path.Combine(cacheDir, "schema-version.txt"));
}
