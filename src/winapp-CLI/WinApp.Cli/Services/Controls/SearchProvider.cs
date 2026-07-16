// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.Json;
using WinApp.Cli.Helpers;

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
/// <see cref="ProviderRegistry"/> and the rest of the tool picks it up.
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

    /// <summary>Delete this provider's cache directory so the next load re-fetches.</summary>
    void ClearCache();
}

/// <summary>
/// Boilerplate shared by every GitHub-backed provider: the on-disk cache
/// protocol (schema-version stamp + 7-day TTL + atomic writes) and the
/// fetch-on-cold-cache flow. Concrete providers only supply their identity and
/// their GitHub fetch. The cache root is injected so it can live under the
/// managed <c>.winapp</c> directory (and be redirected in tests).
/// </summary>
internal abstract class CachedProviderBase : ISearchProvider
{
    private readonly string _cacheRoot;

    protected CachedProviderBase(string cacheRoot)
    {
        _cacheRoot = cacheRoot;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private string CacheDir => Path.Combine(_cacheRoot, Id);

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
        // from GitHub. Treat a thrown transport/parse failure the same as an
        // empty result so cold-cache offline surfaces the friendly "run online
        // once" error and a forced refresh can fall back to the existing cache.
        ProviderData fetched;
        try
        {
            fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            fetched = ProviderData.Empty;
        }

        if (fetched.Scenarios.Length > 0)
        {
            await TryWriteCacheAsync(fetched, cancellationToken).ConfigureAwait(false);
            return fetched;
        }

        // Fetch failed (offline / upstream error). For a forced refresh, fall
        // back to any existing cache rather than dropping the provider entirely
        // — a stale corpus beats no corpus. On a cold cache there is nothing to
        // fall back to, so return Empty and let the caller surface the error.
        if (forceRefresh)
        {
            var cached = TryReadCache();
            if (cached != null) return cached;
        }
        return ProviderData.Empty;
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

    private async Task TryWriteCacheAsync(ProviderData data, CancellationToken cancellationToken)
    {
        try
        {
            var scenariosPath = Path.Combine(CacheDir, "scenarios.json");
            var tagsPath = Path.Combine(CacheDir, "tags.json");
            var keywordsPath = Path.Combine(CacheDir, "keywords.json");
            var timestampPath = Path.Combine(CacheDir, "last-updated.txt");
            var versionPath = Path.Combine(CacheDir, "schema-version.txt");

            Directory.CreateDirectory(CacheDir);

            // Invalidate the freshness marker BEFORE mutating any data file. On a
            // refresh of an already-fresh cache the old timestamp would otherwise
            // still be valid, so a crash after rewriting some (but not all) data
            // files would pair mismatched generations under a "fresh" stamp for up
            // to the TTL. Removing it first means any mid-write crash leaves no
            // valid timestamp ⇒ next read misses ⇒ clean re-fetch.
            if (File.Exists(timestampPath))
            {
                File.Delete(timestampPath);
            }

            // Atomic per-file writes (temp + rename via the shared PathSafety
            // helper). Order: data first, version next, timestamp LAST, so a
            // partially-written set is detected as still-stale on the next read
            // (no fresh timestamp ⇒ cache miss ⇒ re-fetch).
            await PathSafety.AtomicWriteAllTextAsync(scenariosPath,
                JsonSerializer.Serialize(data.Scenarios, ControlsJsonContext.Default.ScenarioArray), Utf8NoBom, cancellationToken).ConfigureAwait(false);
            await PathSafety.AtomicWriteAllTextAsync(tagsPath,
                JsonSerializer.Serialize(data.Tags, ControlsJsonContext.Default.DictionaryStringStringArray), Utf8NoBom, cancellationToken).ConfigureAwait(false);
            if (data.Keywords.Count > 0)
            {
                await PathSafety.AtomicWriteAllTextAsync(keywordsPath,
                    JsonSerializer.Serialize(data.Keywords, ControlsJsonContext.Default.DictionaryStringStringArray), Utf8NoBom, cancellationToken).ConfigureAwait(false);
            }
            else if (File.Exists(keywordsPath))
            {
                // A refresh with no keywords must not leave a stale keywords.json behind.
                File.Delete(keywordsPath);
            }
            await PathSafety.AtomicWriteAllTextAsync(versionPath, CacheVersion.Current, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            await PathSafety.AtomicWriteAllTextAsync(timestampPath, DateTime.UtcNow.ToString("o"), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* cache write is best-effort */ }
    }

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

/// <summary>Lightweight, instance-free descriptor of a provider — its id and
/// display name — used for <c>--source</c> validation and id-prefix mapping
/// without constructing (network-backed) provider instances.</summary>
internal sealed record ProviderDescriptor(string Id, string DisplayName);

/// <summary>
/// The single registry of scenario providers. <see cref="Descriptors"/> lists
/// them without side effects (for validation), and <see cref="CreateProviders"/>
/// builds live instances rooted at a caller-supplied cache directory. This is
/// the one place to register a new source.
/// </summary>
internal static class ProviderRegistry
{
    /// <summary>Provider descriptors in display order (gallery first).</summary>
    public static readonly ProviderDescriptor[] Descriptors =
    {
        new("gallery", "Gallery (WinUI 3)"),
        new("toolkit", "CommunityToolkit"),
    };

    /// <summary>Build live provider instances whose caches live under
    /// <paramref name="cacheRoot"/> (each in its own <c>{Id}</c> subfolder).</summary>
    public static ISearchProvider[] CreateProviders(string cacheRoot) =>
    [
        new GalleryProvider(cacheRoot),
        new ToolkitProvider(cacheRoot),
    ];

    /// <summary>Provider ids plus the pseudo-source <c>"core"</c> — the valid
    /// values for <c>--source</c>.</summary>
    public static IEnumerable<string> SourceFilterValues =>
        Descriptors.Select(d => d.Id).Append("core");

    public static bool IsValidSourceFilter(string source) =>
        SourceFilterValues.Any(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>Descriptor whose <c>{Id}-</c> prefix matches <paramref name="scenarioId"/>, if any.</summary>
    public static ProviderDescriptor? ForScenarioId(string scenarioId) =>
        Descriptors.FirstOrDefault(d => scenarioId.StartsWith($"{d.Id}-", StringComparison.Ordinal));
}
