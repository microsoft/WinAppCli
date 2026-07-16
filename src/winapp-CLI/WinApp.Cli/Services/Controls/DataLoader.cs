// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Reflection;
using System.Text.Json;

/// <summary>
/// Loads the small, hand-curated data baked into the binary. Per the find-ui
/// design the large Gallery/Toolkit scenario corpora are NOT embedded — they are
/// fetched from GitHub on first use and cached per-user. Only lightweight,
/// endpoint-less enrichment ships in the exe:
///   • core-patterns.json  — curated foundational WinUI patterns (no upstream).
///   • gallery-tags.json / toolkit-tags.json / toolkit-keywords.json — curated
///     tag/keyword enrichment merged into fetched scenarios for BM25 scoring.
/// </summary>
internal static class DataLoader
{
    public static CorePattern[] LoadCorePatterns()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("core-patterns.json")!;
        return JsonSerializer.Deserialize(stream, ControlsJsonContext.Default.CorePatternArray)!;
    }

    public static Dictionary<string, string[]> LoadGalleryTags()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("gallery-tags.json")!;
        return JsonSerializer.Deserialize(stream, ControlsJsonContext.Default.DictionaryStringStringArray)!;
    }

    public static Dictionary<string, string[]> LoadToolkitTags()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("toolkit-tags.json")!;
        return JsonSerializer.Deserialize(stream, ControlsJsonContext.Default.DictionaryStringStringArray)!;
    }

    /// <summary>Author-curated keywords from toolkit md frontmatter — short
    /// list of high-quality intent terms scored at higher BM25 weight than
    /// auto-extracted tags. Empty/missing → no extra signal.</summary>
    public static Dictionary<string, string[]> LoadToolkitKeywords()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("toolkit-keywords.json");
        if (stream == null) return new();
        using (stream)
        {
            return JsonSerializer.Deserialize(stream, ControlsJsonContext.Default.DictionaryStringStringArray) ?? new();
        }
    }
}
