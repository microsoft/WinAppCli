// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Path helpers shared between the cache builder (write side) and the query
/// engine (read side) so the two agree on file names, and so untrusted metadata
/// (namespaces from a <c>.winmd</c>, package Id/Version from
/// <c>project.assets.json</c>) can never be used to escape the cache directory.
/// </summary>
internal static class ApiCachePaths
{
    /// <summary>
    /// File name of the machine-wide "SDK scope" manifest, written as a sibling of
    /// the <c>projects</c> directory (never inside it) so it can never collide with
    /// a real project manifest or show up in <c>find-api projects</c>.
    /// </summary>
    internal const string SdkManifestFileName = "sdk.json";

    /// <summary>Display name used for the SDK scope wherever a project name is shown.</summary>
    internal const string SdkScopeName = "Windows SDK";

    /// <summary>Full path of the SDK-scope manifest under a find-api cache directory.</summary>
    internal static string SdkManifestPath(string cacheDir) => Path.Combine(cacheDir, SdkManifestFileName);

    /// <summary>
    /// Maps a namespace (or the <c>_GlobalNamespace</c> sentinel) to its
    /// per-namespace types file name. Dots become underscores; any remaining
    /// path-significant character is neutralized so a crafted namespace such as
    /// <c>..\..\evil</c> can't traverse out of the <c>types</c> directory.
    /// Legitimate .NET namespaces (identifier characters and dots) are
    /// unaffected, so the write and read sides still agree.
    /// </summary>
    internal static string NamespaceFileName(string ns)
    {
        string name = ns.Replace('.', '_');
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name + ".json";
    }

    /// <summary>
    /// Combines <paramref name="segments"/> under <paramref name="root"/> and
    /// verifies the fully-resolved result stays within <paramref name="root"/>.
    /// Returns <see langword="false"/> (and an empty path) when the segments
    /// would escape the root, guarding against traversal via untrusted package
    /// Id/Version values used as directory names.
    /// </summary>
    internal static bool TryCombineContained(string root, string[] segments, out string combined)
    {
        string rootFull = Path.GetFullPath(root);
        var all = new string[segments.Length + 1];
        all[0] = rootFull;
        Array.Copy(segments, 0, all, 1, segments.Length);
        combined = Path.GetFullPath(Path.Combine(all));

        string prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (combined.Equals(rootFull, StringComparison.OrdinalIgnoreCase) ||
            combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        combined = string.Empty;
        return false;
    }
}
