// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Test double for <see cref="IControlsSearchService"/> so find-ui command
/// handler tests run without touching GitHub or the on-disk cache.
/// </summary>
internal sealed class FakeControlsSearchService : IControlsSearchService
{
    private readonly SearchEngine? _engine;
    private readonly Exception? _throw;

    public int GetEngineCalls { get; private set; }
    public bool LastForceRefresh { get; private set; }
    public int ClearCacheCalls { get; private set; }

    private FakeControlsSearchService(SearchEngine? engine, Exception? toThrow)
    {
        _engine = engine;
        _throw = toThrow;
    }

    public static FakeControlsSearchService WithEngine(SearchEngine engine) => new(engine, null);

    public static FakeControlsSearchService Unavailable() =>
        new(null, new ControlsDataUnavailableException(
            "No WinUI control data is available. find-ui fetches ... run the command once to populate the local cache."));

    public Task<SearchEngine> GetEngineAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        GetEngineCalls++;
        LastForceRefresh = forceRefresh;
        if (_throw != null) { throw _throw; }
        return Task.FromResult(_engine!);
    }

    public void ClearCache() => ClearCacheCalls++;
}

/// <summary>
/// Test double for <see cref="ISearchProvider"/> — returns preconfigured data
/// and records how many times it was loaded, so <see cref="ControlsSearchService"/>
/// memoization / partial-corpus / refresh behavior can be verified hermetically.
/// </summary>
internal sealed class FakeSearchProvider : ISearchProvider
{
    private readonly ProviderData _data;

    public FakeSearchProvider(string id, ProviderData data)
    {
        Id = id;
        _data = data;
    }

    public string Id { get; }
    public string DisplayName => Id;
    public int LoadCalls { get; private set; }
    public int ClearCalls { get; private set; }

    public Task<ProviderData> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        LoadCalls++;
        return Task.FromResult(_data);
    }

    public void ClearCache() => ClearCalls++;
}
