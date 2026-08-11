// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Reads <c>templatecache.json</c> files from the template engine's per-user cache root
/// (<c>~/.templateengine/dotnetcli/&lt;sdk-version&gt;/templatecache.json</c>). All IO failures are
/// swallowed into an empty result so template-metadata lookups degrade to a heuristic rather than
/// failing the command.
/// </summary>
internal sealed class TemplateCacheReader : ITemplateCacheReader
{
    public IReadOnlyList<string> ReadTemplateCacheDocuments()
    {
        var documents = new List<string>();
        string root;
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var templateEngineRoot = Path.Combine(userProfile, ".templateengine");
            root = Path.Combine(templateEngineRoot, "dotnetcli");
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return documents;
        }

        if (!Directory.Exists(root))
        {
            return documents;
        }

        IEnumerable<string> caches;
        try
        {
            // One templatecache.json per SDK version folder. The TFM choices come from the template
            // pack (not the SDK), so any cache containing the pack's template yields the same answer.
            caches = Directory.EnumerateFiles(root, "templatecache.json", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return documents;
        }

        foreach (var file in caches)
        {
            try
            {
                documents.Add(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip an unreadable cache file; another may still contain the template.
            }
        }

        return documents;
    }
}
