// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal class ControlsDataService(IWinappDirectoryService directoryService) : IControlsDataService
{
    private SearchEngine? _engine;

    public SearchEngine GetEngine()
    {
        if (_engine != null)
        {
            return _engine;
        }

        var winuiGalleryCacheDir = GetWinUIGalleryCacheDir();
        var toolkitCacheDir = GetToolkitCacheDir();

        var (galleryScenarios, galleryTags) = WinUIGalleryFetcher.Load(winuiGalleryCacheDir);
        var (toolkitScenarios, toolkitTags) = ToolkitFetcher.Load(toolkitCacheDir);

        var allScenarios = galleryScenarios.Concat(toolkitScenarios).ToArray();

        // Merge WinUI Gallery + Toolkit tags. Toolkit wins on duplicate keys.
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
        var winuiGalleryCacheDir = GetWinUIGalleryCacheDir();
        var toolkitCacheDir = GetToolkitCacheDir();
        try { if (Directory.Exists(winuiGalleryCacheDir)) { Directory.Delete(winuiGalleryCacheDir, recursive: true); } } catch { /* best-effort */ }
        try { if (Directory.Exists(toolkitCacheDir)) { Directory.Delete(toolkitCacheDir, recursive: true); } } catch { /* best-effort */ }
        _engine = null;
    }

    private string GetWinUIGalleryCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "controls", "winui-gallery");

    private string GetToolkitCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "controls", "toolkit");
}
