// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;

namespace WinApp.Cli.Services.Gallery;


internal static class DataLoader
{
    public static Scenario[] LoadGalleryScenarios()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("gallery-scenarios.json")!;
        return JsonSerializer.Deserialize(stream, GalleryJsonContext.Default.ScenarioArray)!;
    }

    public static Scenario[] LoadToolkitScenarios()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("toolkit-scenarios.json")!;
        return JsonSerializer.Deserialize(stream, GalleryJsonContext.Default.ScenarioArray)!;
    }

    public static CorePattern[] LoadCorePatterns()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("core-patterns.json")!;
        return JsonSerializer.Deserialize(stream, GalleryJsonContext.Default.CorePatternArray)!;
    }

    public static Dictionary<string, string[]> LoadGalleryTags()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("gallery-tags.json")!;
        return JsonSerializer.Deserialize(stream, GalleryJsonContext.Default.DictionaryStringStringArray)!;
    }

    public static Dictionary<string, string[]> LoadToolkitTags()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("toolkit-tags.json")!;
        return JsonSerializer.Deserialize(stream, GalleryJsonContext.Default.DictionaryStringStringArray)!;
    }
}
