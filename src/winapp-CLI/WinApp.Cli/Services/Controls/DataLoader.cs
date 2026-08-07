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
///   • core-patterns.json — curated foundational WinUI patterns (no upstream).
///   • gallery-tags.json  — curated tag enrichment merged into fetched Gallery
///     scenarios for BM25 scoring. Gallery needs this because it can only derive
///     tags from Title + Subtitle; the Toolkit derives richer tags AND keywords
///     from its own md frontmatter, so it ships no embedded enrichment.
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
}
