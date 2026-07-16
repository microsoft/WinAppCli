// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ControlsSearchService"/> using fake providers
/// (no GitHub / no disk), exercising memoization, forced refresh, partial-corpus
/// handling, and the all-empty error path.
/// </summary>
[TestClass]
public class ControlsSearchServiceTests
{
    private static ProviderData Data(string source, params string[] controlIds)
    {
        var scenarios = controlIds.Select((cid, i) => new Scenario
        {
            Id = $"{cid}-1",
            ControlId = cid,
            ControlName = cid,
            HeaderText = $"{cid} sample",
            Source = source,
        }).ToArray();
        var tags = controlIds.ToDictionary(cid => cid, cid => new[] { cid });
        return new ProviderData(scenarios, tags, new());
    }

    [TestMethod]
    public async Task GetEngineAsync_MemoizesCompleteCorpus()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var e1 = await sut.GetEngineAsync();
        var e2 = await sut.GetEngineAsync();

        Assert.AreSame(e1, e2, "second call should return the memoized engine");
        Assert.AreEqual(1, gallery.LoadCalls);
        Assert.AreEqual(1, toolkit.LoadCalls);
    }

    [TestMethod]
    public async Task GetEngineAsync_ForceRefresh_RebuildsAndReloadsProviders()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        await sut.GetEngineAsync();
        await sut.GetEngineAsync(forceRefresh: true);

        Assert.AreEqual(2, gallery.LoadCalls, "forceRefresh must re-load providers");
        Assert.AreEqual(2, toolkit.LoadCalls);
    }

    [TestMethod]
    public async Task GetEngineAsync_PartialCorpus_ServesButDoesNotMemoize()
    {
        // toolkit empty (its cold-cache fetch failed); gallery has data.
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", ProviderData.Empty);
        var sut = new ControlsSearchService([gallery, toolkit]);

        var e1 = await sut.GetEngineAsync();
        Assert.IsNotNull(e1, "partial corpus should still serve results");

        // Not memoized → next call re-attempts the missing provider.
        await sut.GetEngineAsync();
        Assert.AreEqual(2, toolkit.LoadCalls, "a degraded engine must not be pinned; re-attempt the empty provider");
    }

    [TestMethod]
    public async Task GetEngineAsync_AllProvidersEmpty_Throws()
    {
        var gallery = new FakeSearchProvider("gallery", ProviderData.Empty);
        var toolkit = new FakeSearchProvider("toolkit", ProviderData.Empty);
        var sut = new ControlsSearchService([gallery, toolkit]);

        await Assert.ThrowsExactlyAsync<ControlsDataUnavailableException>(
            async () => await sut.GetEngineAsync());
    }

    [TestMethod]
    public async Task GetEngineAsync_CollidingControlIdsAcrossSources_BothSurvive()
    {
        // Both providers expose the same bare controlId "colorpicker" (and a
        // same-named tag). Tags are namespaced by provider id ("{id}:{controlId}")
        // so neither the scenarios nor the tag entries overwrite each other —
        // both source-prefixed scenarios must be present in the merged corpus.
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "colorpicker"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "colorpicker"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        var engine = await sut.GetEngineAsync();
        var ids = engine.ListAll().Select(x => x.id).ToList();

        CollectionAssert.Contains(ids, "gallery-colorpicker-1", "gallery colorpicker must survive the merge");
        CollectionAssert.Contains(ids, "toolkit-colorpicker-1", "toolkit colorpicker must survive the merge");
    }

    [TestMethod]
    public void ClearCache_DelegatesToEveryProvider()
    {
        var gallery = new FakeSearchProvider("gallery", Data("gallery", "tabview"));
        var toolkit = new FakeSearchProvider("toolkit", Data("toolkit", "datagrid"));
        var sut = new ControlsSearchService([gallery, toolkit]);

        sut.ClearCache();

        Assert.AreEqual(1, gallery.ClearCalls);
        Assert.AreEqual(1, toolkit.ClearCalls);
    }
}
