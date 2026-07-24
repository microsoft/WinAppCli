// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using WinApp.Cli.Services;

/// <summary>
/// Builds and memoizes the <see cref="SearchEngine"/> that backs
/// <c>winapp find-ui</c>. Scenario data comes from the registered
/// <see cref="ISearchProvider"/>s (WinUI Gallery + Community Toolkit + Reactor),
/// which serve a fresh per-user cache or fetch from GitHub on a cold/stale cache.
/// The curated core patterns are baked in (they have no upstream endpoint).
///
/// find-ui ships no embedded scenario snapshot, so the very first run (or any
/// run with a cold cache) requires network access. When every provider comes
/// back empty — the signature of an offline cold start — a
/// <see cref="ControlsDataUnavailableException"/> is thrown so the command can
/// emit a clear "run online once" message instead of silently returning no hits.
/// </summary>
internal interface IControlsSearchService
{
    /// <summary>
    /// Returns a configured engine, building it lazily on first use and memoizing
    /// it thereafter. Pass <paramref name="forceRefresh"/> to bypass both the
    /// in-memory engine and the on-disk cache and re-fetch from GitHub.
    /// When <paramref name="allowCoreOnly"/> is true and no network corpus can be
    /// loaded (offline cold cache), a core-only engine is returned instead of
    /// throwing — the embedded core patterns are always available, so
    /// <c>--list</c>, <c>--source core</c>, and <c>--id &lt;core-id&gt;</c> keep
    /// working offline. <paramref name="includeReactor"/> gates the opt-in Reactor
    /// source: when false (the default — e.g. a plain search or <c>--list</c>) the
    /// Reactor provider is neither loaded nor fetched, so its C#-only samples never
    /// pollute results for standard XAML apps; the command sets it true only for a
    /// <c>--source reactor</c> search or a <c>reactor-*</c> <c>--id</c> fetch. The
    /// with- and without-Reactor engines are memoized separately.
    /// <paramref name="onFetchStarting"/> is invoked (with a
    /// provider display name) the first time any provider starts a network fetch,
    /// so the caller can show a one-time "fetching…" notice; it is never invoked
    /// when everything is served from cache or the memoized engine.
    /// </summary>
    Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, bool allowCoreOnly = false, bool includeReactor = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default);

    /// <summary>Delete every provider's per-user cache so the next load re-fetches.</summary>
    void ClearCache();
}

/// <summary>Thrown when no scenario data could be loaded — typically a cold cache with no network.</summary>
internal sealed class ControlsDataUnavailableException : Exception
{
    public ControlsDataUnavailableException(string message) : base(message) { }
}

internal sealed class ControlsSearchService : IControlsSearchService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISearchProvider[] _providers;
    private SearchEngine? _engine;
    // Reactor is opt-in, so the corpus differs by whether it's included. Memoize
    // the two shapes separately: a default (Gallery + Toolkit + core) engine and a
    // with-Reactor engine, so switching between a normal search and a
    // `--source reactor` search within one process doesn't clobber either cache.
    private SearchEngine? _engineWithReactor;

    /// <summary>Production constructor: providers are rooted at the managed
    /// global <c>.winapp</c> cache directory so environment/test path overrides
    /// (<c>WINAPP_CLI_CACHE_DIRECTORY</c>) and repo-wide path policy apply.</summary>
    public ControlsSearchService(IWinappDirectoryService directoryService)
        : this(ProviderRegistry.CreateProviders(
            Path.Combine(directoryService.GetGlobalWinappDirectory().FullName, "cache", "find-ui")))
    {
    }

    /// <summary>Test seam: inject providers directly (e.g. fakes with a temp cache).</summary>
    internal ControlsSearchService(ISearchProvider[] providers)
    {
        _providers = providers;
    }

    public async Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, bool allowCoreOnly = false, bool includeReactor = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default)
    {
        var memoized = includeReactor ? _engineWithReactor : _engine;
        if (memoized != null && !forceRefresh)
        {
            return memoized;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            memoized = includeReactor ? _engineWithReactor : _engine;
            if (memoized != null && !forceRefresh)
            {
                return memoized;
            }

            // A forced refresh re-fetches the corpus from GitHub and rewrites the
            // on-disk cache for every provider it loads. Invalidate BOTH memoized
            // engines up front so the OTHER variant (which this call won't rebuild)
            // can't keep serving pre-refresh, in-memory scenario data on a later
            // call — a `--refresh` must not leave one engine shape stale.
            if (forceRefresh)
            {
                _engine = null;
                _engineWithReactor = null;
            }

            var allScenarios = new List<Scenario>();
            // Tags/keywords are namespaced by provider id ("{providerId}:{controlId}")
            // so colliding bare controlIds across sources (gallery + toolkit both
            // expose "colorpicker") don't overwrite each other.
            var allTags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var allKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            bool anyProviderEmpty = false;

            foreach (var provider in _providers)
            {
                // Reactor is opt-in. Unless this invocation explicitly needs it
                // (a --source reactor search or a reactor-* --id fetch), skip the
                // provider entirely — no load, no network fetch — so Reactor's
                // C#-only samples never compete in a default search for a standard
                // XAML app.
                if (!includeReactor && ProviderRegistry.IsReactorSource(provider.Id))
                {
                    continue;
                }

                var data = await provider.LoadAsync(forceRefresh, onFetchStarting, cancellationToken).ConfigureAwait(false);
                if (data.Scenarios.Length == 0)
                {
                    anyProviderEmpty = true;
                    continue;
                }
                allScenarios.AddRange(data.Scenarios);
                foreach (var kv in data.Tags) allTags[$"{provider.Id}:{kv.Key}"] = kv.Value;
                foreach (var kv in data.Keywords) allKeywords[$"{provider.Id}:{kv.Key}"] = kv.Value;
            }

            if (allScenarios.Count == 0)
            {
                // No network corpus. The embedded core patterns are always available,
                // so for requests that a core-only corpus can satisfy (--list,
                // --source core, --id <core-id>) return a core-only engine rather
                // than failing. Not memoized: the network corpus may load on a later
                // call, and this degraded engine must not be pinned.
                if (allowCoreOnly)
                {
                    return new SearchEngine(
                        Array.Empty<Scenario>(),
                        DataLoader.LoadCorePatterns(),
                        new(),
                        new());
                }

                throw new ControlsDataUnavailableException(
                    "No WinUI control data is available. find-ui fetches the WinUI Gallery, " +
                    "Community Toolkit, and Reactor corpora from GitHub on first use — connect to the " +
                    "internet and run the command once to populate the local cache.");
            }

            var engine = new SearchEngine(
                allScenarios.ToArray(),
                DataLoader.LoadCorePatterns(),
                allTags,
                allKeywords);

            // Only memoize a COMPLETE corpus. If a provider came back empty (its
            // cold-cache fetch failed), serve the partial result for this call but
            // don't cache it in-memory, so a later invocation re-attempts the
            // missing source instead of being pinned to a degraded engine. The
            // with- and without-Reactor corpora memoize into separate slots.
            if (!anyProviderEmpty)
            {
                if (includeReactor)
                {
                    _engineWithReactor = engine;
                }
                else
                {
                    _engine = engine;
                }
            }
            return engine;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ClearCache()
    {
        _engine = null;
        _engineWithReactor = null;

        var failures = new List<Exception>();
        foreach (var provider in _providers)
        {
            try { provider.ClearCache(); }
            catch (Exception ex) { failures.Add(ex); }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Failed to clear one or more find-ui cache directories. This usually means a " +
                "cache file is locked by another process.",
                failures);
        }
    }

    public void Dispose() => _gate.Dispose();
}
