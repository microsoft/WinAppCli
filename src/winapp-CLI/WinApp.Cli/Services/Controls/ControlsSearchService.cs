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
    /// <paramref name="onFetchStarting"/> is invoked (with a provider display name)
    /// the first time any provider starts a network fetch, so the caller can show
    /// a one-time "fetching…" notice; it is never invoked when everything is served
    /// from cache or the memoized engine.
    /// </summary>
    Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default);

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

    public async Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, Action<string>? onFetchStarting = null, CancellationToken cancellationToken = default)
    {
        if (_engine != null && !forceRefresh)
        {
            return _engine;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine != null && !forceRefresh)
            {
                return _engine;
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
            // missing source instead of being pinned to a degraded engine.
            if (!anyProviderEmpty)
            {
                _engine = engine;
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
