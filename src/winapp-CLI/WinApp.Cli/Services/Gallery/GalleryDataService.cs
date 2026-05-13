// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Gallery;

internal class GalleryDataService(IWinappDirectoryService directoryService) : IGalleryDataService
{
    private SearchEngine? _engine;

    public SearchEngine GetEngine()
    {
        if (_engine != null)
        {
            return _engine;
        }

        var galleryCacheDir = GetGalleryCacheDir();
        var toolkitCacheDir = GetToolkitCacheDir();

        var (galleryScenarios, galleryTags) = GalleryFetcher.Load(galleryCacheDir);
        var (toolkitScenarios, toolkitTags) = ToolkitFetcher.Load(toolkitCacheDir);

        var allScenarios = galleryScenarios.Concat(toolkitScenarios).ToArray();

        // Merge gallery + toolkit tags. Toolkit wins on duplicate keys (matches winui-search behavior).
        var allTags = new Dictionary<string, string[]>(galleryTags);
        foreach (var kv in toolkitTags)
        {
            allTags[kv.Key] = kv.Value;
        }

        _engine = new SearchEngine(allScenarios, DataLoader.LoadCorePatterns(), allTags);
        return _engine;
    }

    public void ClearCache()
    {
        var galleryCacheDir = GetGalleryCacheDir();
        var toolkitCacheDir = GetToolkitCacheDir();
        try { if (Directory.Exists(galleryCacheDir)) { Directory.Delete(galleryCacheDir, recursive: true); } } catch { /* best-effort */ }
        try { if (Directory.Exists(toolkitCacheDir)) { Directory.Delete(toolkitCacheDir, recursive: true); } } catch { /* best-effort */ }
        _engine = null;
    }

    private string GetGalleryCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "gallery", "gallery");

    private string GetToolkitCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "gallery", "toolkit");
}
