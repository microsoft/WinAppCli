// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text.Json;

/// <summary>
/// Data contributed by a search provider: its scenarios plus the per-control
/// tag and keyword dictionaries. Tag/keyword keys are bare controlIds — the
/// engine namespaces them by provider id (<c>{providerId}:{controlId}</c>).
/// </summary>
internal sealed record ProviderData(
    Scenario[] Scenarios,
    Dictionary<string, string[]> Tags,
    Dictionary<string, string[]> Keywords)
{
    public static ProviderData Empty { get; } =
        new(Array.Empty<Scenario>(), new(), new());
}

/// <summary>
/// A source of WinUI scenarios (WinUI Gallery, Community Toolkit, …). A single
/// stable <see cref="Id"/> ties together everything downstream: it is the
/// <c>Scenario.Source</c> value, the on-disk cache subdirectory, the scenario
/// id prefix (<c>{Id}-…</c>), the <c>--source</c> token, and the composite
/// tag/keyword key namespace. Register a new provider in
/// <see cref="ProviderRegistry.All"/> and the rest of the tool picks it up.
/// </summary>
internal interface ISearchProvider
{
    /// <summary>Lowercase, stable identifier, e.g. <c>"gallery"</c> / <c>"toolkit"</c>.</summary>
    string Id { get; }

    /// <summary>Human-readable heading used by <c>winapp find-ui --list</c>.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Load this provider's data. Serves the per-user cache when fresh; otherwise
    /// fetches from GitHub (network required) and primes the cache. Unlike the
    /// upstream tool, find-ui embeds no scenario snapshot, so a cold cache MUST
    /// reach the network — callers surface a clear "run online once" error when
    /// the fetch yields nothing.
    /// </summary>
    Task<ProviderData> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>Force a GitHub refresh and rewrite the cache.</summary>
    Task RefreshFromGitHubAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Boilerplate shared by every GitHub-backed provider: the on-disk cache
/// protocol (schema-version stamp + 7-day TTL + atomic writes) and the
/// fetch-on-cold-cache flow. Concrete providers only supply their identity and
/// their GitHub fetch.
/// </summary>
internal abstract class CachedProviderBase : ISearchProvider
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    // Per-user cache root. find-ui-specific so it never collides with the
    // upstream winui-search tool's cache.
    private string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winapp", "find-ui", "cache", Id);

    /// <summary>Fully-prepared GitHub fetch (tags already cleaned). Return
    /// <see cref="ProviderData.Empty"/> to leave the cache untouched.</summary>
    protected abstract Task<ProviderData> FetchAsync(CancellationToken cancellationToken);

    /// <summary>Hook to re-normalize tags read back from cache. Defaults to
    /// identity; providers that clean tags on write override this to match.</summary>
    protected virtual Dictionary<string, string[]> NormalizeTagsOnRead(
        Dictionary<string, string[]> tags) => tags;

    public async Task<ProviderData> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh)
        {
            var cached = TryReadCache();
            if (cached != null) return cached;
        }

        // Cold/stale cache (or forced): no embedded snapshot exists, so fetch
        // from GitHub. On success prime the cache; on failure return Empty so the
        // caller can emit a network-required message.
        var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (fetched.Scenarios.Length > 0)
        {
            TryWriteCache(fetched);
            return fetched;
        }
        return ProviderData.Empty;
    }

    public async Task RefreshFromGitHubAsync(CancellationToken cancellationToken = default)
    {
        var data = await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (data.Scenarios.Length > 0) TryWriteCache(data);
    }

    private ProviderData? TryReadCache()
    {
        var scenariosPath = Path.Combine(CacheDir, "scenarios.json");
        var tagsPath = Path.Combine(CacheDir, "tags.json");
        var keywordsPath = Path.Combine(CacheDir, "keywords.json");
        var timestampPath = Path.Combine(CacheDir, "last-updated.txt");
        var versionPath = Path.Combine(CacheDir, "schema-version.txt");

        if (!File.Exists(scenariosPath) || !File.Exists(tagsPath)
            || !File.Exists(timestampPath) || !File.Exists(versionPath))
            return null;

        try
        {
            if (File.ReadAllText(versionPath).Trim() != CacheVersion.Current) return null;
            var lastUpdated = ControlsCacheIo.ReadTimestamp(timestampPath);
            // Reject future-dated timestamps so a clock reset can't pin stale data.
            if (!lastUpdated.HasValue || lastUpdated.Value > DateTime.UtcNow
                || DateTime.UtcNow - lastUpdated.Value >= CacheTtl)
                return null;

            var scenarios = JsonSerializer.Deserialize(
                File.ReadAllText(scenariosPath), ControlsJsonContext.Default.ScenarioArray);
            var tags = JsonSerializer.Deserialize(
                File.ReadAllText(tagsPath), ControlsJsonContext.Default.DictionaryStringStringArray);
            if (scenarios == null || scenarios.Length == 0 || tags == null) return null;

            Dictionary<string, string[]>? keywords = null;
            if (File.Exists(keywordsPath))
            {
                try
                {
                    keywords = JsonSerializer.Deserialize(
                        File.ReadAllText(keywordsPath), ControlsJsonContext.Default.DictionaryStringStringArray);
                }
                catch { keywords = null; }
            }

            return new ProviderData(scenarios, NormalizeTagsOnRead(tags), keywords ?? new());
        }
        catch { return null; }
    }

    private void TryWriteCache(ProviderData data)
    {
        try
        {
            var scenariosPath = Path.Combine(CacheDir, "scenarios.json");
            var tagsPath = Path.Combine(CacheDir, "tags.json");
            var keywordsPath = Path.Combine(CacheDir, "keywords.json");
            var timestampPath = Path.Combine(CacheDir, "last-updated.txt");
            var versionPath = Path.Combine(CacheDir, "schema-version.txt");

            // Atomic per-file writes. Order: data first, version next, timestamp
            // LAST, so a partially-written set is detected as still-stale on the
            // next read (no fresh timestamp ⇒ cache miss ⇒ re-fetch).
            ControlsCacheIo.AtomicWriteAllText(scenariosPath,
                JsonSerializer.Serialize(data.Scenarios, ControlsJsonContext.Default.ScenarioArray));
            ControlsCacheIo.AtomicWriteAllText(tagsPath,
                JsonSerializer.Serialize(data.Tags, ControlsJsonContext.Default.DictionaryStringStringArray));
            if (data.Keywords.Count > 0)
                ControlsCacheIo.AtomicWriteAllText(keywordsPath,
                    JsonSerializer.Serialize(data.Keywords, ControlsJsonContext.Default.DictionaryStringStringArray));
            ControlsCacheIo.AtomicWriteAllText(versionPath, CacheVersion.Current);
            ControlsCacheIo.AtomicWriteAllText(timestampPath, DateTime.UtcNow.ToString("o"));
        }
        catch { /* cache write is best-effort */ }
    }

    /// <summary>Delete this provider's cache directory so the next load re-fetches.</summary>
    public void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException($"Could not clear find-ui cache '{CacheDir}': {ex.Message}", ex);
        }
    }
}

/// <summary>
/// The ordered set of scenario providers. This is the single place to register
/// a new source; the search service and <see cref="SearchEngine"/> are driven
/// entirely off this list (plus the special-cased curated core patterns).
/// </summary>
internal static class ProviderRegistry
{
    /// <summary>All scenario providers, in display order (gallery first).</summary>
    public static readonly ISearchProvider[] All =
    {
        new GalleryProvider(),
        new ToolkitProvider(),
    };

    /// <summary>Provider ids plus the pseudo-source <c>"core"</c> — the valid
    /// values for <c>--source</c>.</summary>
    public static IEnumerable<string> SourceFilterValues =>
        All.Select(p => p.Id).Append("core");

    public static bool IsValidSourceFilter(string source) =>
        SourceFilterValues.Any(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>Provider whose <c>{Id}-</c> prefix matches <paramref name="scenarioId"/>, if any.</summary>
    public static ISearchProvider? ForScenarioId(string scenarioId) =>
        All.FirstOrDefault(p => scenarioId.StartsWith($"{p.Id}-", StringComparison.Ordinal));
}
