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

        // Reset the in-memory engine first so a partial failure below still forces
        // a re-fetch on the next call.
        _engine = null;

        var failures = new List<Exception>();
        TryDelete(winuiGalleryCacheDir, failures);
        TryDelete(toolkitCacheDir, failures);

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"Failed to clear one or more controls cache directories. " +
                $"This usually means a file in the cache is locked by another process.",
                failures);
        }
    }

    private static void TryDelete(string path, List<Exception> failures)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            failures.Add(new IOException($"Could not delete '{path}': {ex.Message}", ex));
        }
    }

    private string GetWinUIGalleryCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "controls", "winui-gallery");

    private string GetToolkitCacheDir() =>
        Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "controls", "toolkit");
}
