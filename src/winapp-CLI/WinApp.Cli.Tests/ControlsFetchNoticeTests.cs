// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net.Http;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Verifies the "fetching…" notice plumbing: the <c>onFetchStarting</c> callback
/// threaded from the command through <see cref="ControlsSearchService"/> into each
/// <see cref="CachedProviderBase"/> fires only when a provider actually reaches the
/// network (cold/stale cache or forced refresh), never on a warm-cache or memoized load.
/// </summary>
[TestClass]
public class ControlsFetchNoticeTests
{
    /// <summary>A real <see cref="CachedProviderBase"/> backed by a temp cache dir and a
    /// canned payload, so the base class's cache-vs-fetch decision (and thus the notice)
    /// is exercised for real.</summary>
    private sealed class StubProvider : CachedProviderBase
    {
        private readonly ProviderData _data;
        public int FetchCalls { get; private set; }

        public StubProvider(string cacheRoot, string id, string displayName, ProviderData data)
            : base(cacheRoot)
        {
            Id = id;
            DisplayName = displayName;
            _data = data;
        }

        public override string Id { get; }
        public override string DisplayName { get; }

        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCalls++;
            return Task.FromResult(_data);
        }
    }

    private static ProviderData SampleData(string controlId) =>
        new(
            [new Scenario { Id = $"{controlId}-1", ControlId = controlId, ControlName = controlId, HeaderText = "s", Source = controlId }],
            new Dictionary<string, string[]> { [controlId] = [controlId] },
            new());

    private static string NewTempCacheRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winapp-findui-fetch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public async Task Provider_ColdCache_InvokesNotice_ThenWarmCacheDoesNot()
    {
        var root = NewTempCacheRoot();
        try
        {
            var provider = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));

            // Cold cache → must fetch → notice fires once with the display name.
            var cold = new List<string>();
            var data = await provider.LoadAsync(onFetchStarting: cold.Add);
            Assert.AreEqual(1, provider.FetchCalls);
            Assert.AreEqual(1, data.Scenarios.Length);
            CollectionAssert.AreEqual(new[] { "Gallery (WinUI 3)" }, cold);

            // Warm cache (the cold load primed it) → served from disk → no fetch, no notice.
            var warm = new List<string>();
            await provider.LoadAsync(onFetchStarting: warm.Add);
            Assert.AreEqual(1, provider.FetchCalls, "warm cache must not re-fetch");
            Assert.AreEqual(0, warm.Count, "warm cache must not raise the fetching notice");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Provider_ForceRefresh_InvokesNotice()
    {
        var root = NewTempCacheRoot();
        try
        {
            var provider = new StubProvider(root, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            await provider.LoadAsync();                       // prime the cache

            var notices = new List<string>();
            await provider.LoadAsync(forceRefresh: true, onFetchStarting: notices.Add);

            Assert.AreEqual(2, provider.FetchCalls, "forceRefresh must re-fetch even with a warm cache");
            CollectionAssert.AreEqual(new[] { "Gallery (WinUI 3)" }, notices);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Service_ColdProviders_ForwardsNoticeForEach_ThenMemoizedRunIsSilent()
    {
        var rootA = NewTempCacheRoot();
        var rootB = NewTempCacheRoot();
        try
        {
            var gallery = new StubProvider(rootA, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            var toolkit = new StubProvider(rootB, "toolkit", "CommunityToolkit", SampleData("toolkit"));
            var service = new ControlsSearchService([gallery, toolkit]);

            // First (cold) build: both providers fetch → notice fires for each.
            var cold = new List<string>();
            await service.GetEngineAsync(onFetchStarting: cold.Add);
            CollectionAssert.AreEquivalent(new[] { "Gallery (WinUI 3)", "CommunityToolkit" }, cold);

            // Second call: complete corpus was memoized → providers aren't reloaded → silent.
            var second = new List<string>();
            await service.GetEngineAsync(onFetchStarting: second.Add);
            Assert.AreEqual(0, second.Count, "a memoized engine must not raise the fetching notice");
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    [TestMethod]
    public async Task Service_PartialCorpus_NotMemoized_ReraisesNoticeForFetchingProviderNextCall()
    {
        var rootA = NewTempCacheRoot();
        var rootB = NewTempCacheRoot();
        try
        {
            // gallery has data (caches after first fetch); toolkit always comes back empty
            // (its fetch never populates a cache), so the corpus is partial and the service
            // must NOT memoize it — the next call re-attempts both providers.
            var gallery = new StubProvider(rootA, "gallery", "Gallery (WinUI 3)", SampleData("gallery"));
            var toolkit = new StubProvider(rootB, "toolkit", "CommunityToolkit", ProviderData.Empty);
            var service = new ControlsSearchService([gallery, toolkit]);

            var first = new List<string>();
            await service.GetEngineAsync(onFetchStarting: first.Add);
            CollectionAssert.AreEquivalent(new[] { "Gallery (WinUI 3)", "CommunityToolkit" }, first);

            // Not memoized (partial) → second call reloads. gallery is now warm (silent), but
            // the empty toolkit re-fetches → the notice is raised again for it.
            var second = new List<string>();
            await service.GetEngineAsync(onFetchStarting: second.Add);
            CollectionAssert.AreEqual(new[] { "CommunityToolkit" }, second,
                "the still-empty provider must re-fetch and re-raise the notice; the warm one stays silent");
            Assert.AreEqual(1, gallery.FetchCalls, "gallery served from warm cache on the second call");
            Assert.AreEqual(2, toolkit.FetchCalls, "empty toolkit re-fetched on the second call");
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    [TestMethod]
    public async Task Provider_OfflineColdFetchThrows_StillInvokesNotice()
    {
        var root = NewTempCacheRoot();
        try
        {
            // Simulate an offline cold start: FetchAsync throws. The notice fires before the
            // fetch attempt, and LoadAsync swallows the failure into Empty.
            var provider = new ThrowingProvider(root, "gallery", "Gallery (WinUI 3)");
            var notices = new List<string>();

            var data = await provider.LoadAsync(onFetchStarting: notices.Add);

            Assert.AreEqual(0, data.Scenarios.Length, "an offline cold fetch yields no data");
            CollectionAssert.AreEqual(new[] { "Gallery (WinUI 3)" }, notices,
                "the notice fires even when the fetch itself fails");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A provider whose fetch always throws — models an offline cold start.</summary>
    private sealed class ThrowingProvider : CachedProviderBase
    {
        public ThrowingProvider(string cacheRoot, string id, string displayName) : base(cacheRoot)
        {
            Id = id;
            DisplayName = displayName;
        }

        public override string Id { get; }
        public override string DisplayName { get; }

        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
            => throw new HttpRequestException("offline");
    }
}
