// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net.Http;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests the shared cache protocol in <see cref="CachedProviderBase"/> — cold
/// fetch-and-write, fresh-cache read (no re-fetch), TTL/future-timestamp misses,
/// empty-fetch handling, and forced-refresh cache fallback — using a temp cache
/// dir and a stubbed fetch (no network).
/// </summary>
[TestClass]
public class CachedProviderTests
{
    private DirectoryInfo _cacheRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheRoot = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"finduicache_{Guid.NewGuid():N}"));
        _cacheRoot.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (_cacheRoot.Exists) { _cacheRoot.Delete(recursive: true); } }
        catch { /* best-effort */ }
    }

    [TestMethod]
    public async Task Refresh_WithNoKeywords_DeletesStaleKeywordsFile()
    {
        bool withKeywords = true;
        var provider = new StubProvider(_cacheRoot.FullName,
            () => withKeywords ? SampleDataWithKeywords() : SampleData());
        await provider.LoadAsync();   // primes cache with keywords.json
        var keywordsPath = Path.Combine(_cacheRoot.FullName, "stub", "keywords.json");
        Assert.IsTrue(File.Exists(keywordsPath), "first refresh should write keywords.json");

        withKeywords = false;
        await provider.LoadAsync(forceRefresh: true);   // refresh returns no keywords

        Assert.IsFalse(File.Exists(keywordsPath), "a keyword-less refresh must not leave a stale keywords.json");
    }

    private sealed class StubProvider : CachedProviderBase
    {
        private readonly Func<ProviderData> _fetch;
        public int FetchCalls { get; private set; }

        public StubProvider(string cacheRoot, Func<ProviderData> fetch) : base(cacheRoot) => _fetch = fetch;

        public override string Id => "stub";
        public override string DisplayName => "Stub";

        protected override Task<ProviderData> FetchAsync(CancellationToken cancellationToken)
        {
            FetchCalls++;
            return Task.FromResult(_fetch());
        }
    }

    private static ProviderData SampleData(string controlId = "tabview")
    {
        var s = new Scenario { Id = $"{controlId}-1", ControlId = controlId, ControlName = controlId, HeaderText = "sample", Source = "stub" };
        return new ProviderData([s], new Dictionary<string, string[]> { [controlId] = [controlId] }, new());
    }

    private static ProviderData SampleDataWithKeywords(string controlId = "tabview")
    {
        var s = new Scenario { Id = $"{controlId}-1", ControlId = controlId, ControlName = controlId, HeaderText = "sample", Source = "stub" };
        return new ProviderData(
            [s],
            new Dictionary<string, string[]> { [controlId] = [controlId] },
            new Dictionary<string, string[]> { [controlId] = ["kw"] });
    }

    private string TimestampPath => Path.Combine(_cacheRoot.FullName, "stub", "last-updated.txt");

    [TestMethod]
    public async Task ColdCache_Fetches_WritesCache_AndReturnsData()
    {
        var provider = new StubProvider(_cacheRoot.FullName, () => SampleData());

        var data = await provider.LoadAsync();

        Assert.AreEqual(1, data.Scenarios.Length);
        Assert.AreEqual(1, provider.FetchCalls);
        Assert.IsTrue(File.Exists(Path.Combine(_cacheRoot.FullName, "stub", "scenarios.json")), "cold fetch should prime the cache");
        Assert.IsTrue(File.Exists(TimestampPath));
    }

    [TestMethod]
    public async Task FreshCache_ReadFromDisk_WithoutRefetch()
    {
        var provider = new StubProvider(_cacheRoot.FullName, () => SampleData());
        await provider.LoadAsync();                 // primes cache (fetch #1)
        var again = await provider.LoadAsync();      // should hit cache

        Assert.AreEqual(1, provider.FetchCalls, "fresh cache must be served without a second fetch");
        Assert.AreEqual(1, again.Scenarios.Length);
    }

    [TestMethod]
    public async Task ForceRefresh_BypassesCache_AndRefetches()
    {
        var provider = new StubProvider(_cacheRoot.FullName, () => SampleData());
        await provider.LoadAsync();
        await provider.LoadAsync(forceRefresh: true);

        Assert.AreEqual(2, provider.FetchCalls);
    }

    [TestMethod]
    public async Task FutureTimestamp_TreatedAsMiss_Refetches()
    {
        var provider = new StubProvider(_cacheRoot.FullName, () => SampleData());
        await provider.LoadAsync();                 // primes cache
        // Poison the timestamp with a future date — a clock reset must not pin stale data.
        File.WriteAllText(TimestampPath, DateTime.UtcNow.AddDays(2).ToString("o"));

        await provider.LoadAsync();
        Assert.AreEqual(2, provider.FetchCalls, "future-dated cache should be rejected and re-fetched");
    }

    [TestMethod]
    public async Task ColdCache_EmptyFetch_ReturnsEmpty_NoCacheWritten()
    {
        var provider = new StubProvider(_cacheRoot.FullName, () => ProviderData.Empty);

        var data = await provider.LoadAsync();

        Assert.AreEqual(0, data.Scenarios.Length);
        Assert.IsFalse(File.Exists(Path.Combine(_cacheRoot.FullName, "stub", "scenarios.json")), "an empty fetch must not write a cache");
    }

    [TestMethod]
    public async Task ForceRefresh_FetchThrows_FallsBackToExistingCache()
    {
        bool throwNow = false;
        var provider = new StubProvider(_cacheRoot.FullName,
            () => throwNow ? throw new HttpRequestException("network down") : SampleData());
        await provider.LoadAsync();   // seed good cache

        throwNow = true;              // now the refresh throws
        var data = await provider.LoadAsync(forceRefresh: true);

        Assert.AreEqual(1, data.Scenarios.Length, "a thrown forced-refresh failure should fall back to the existing cache");
    }

    [TestMethod]
    public async Task ColdCache_FetchThrows_ReturnsEmpty()
    {
        var provider = new StubProvider(_cacheRoot.FullName, () => throw new HttpRequestException("offline"));

        var data = await provider.LoadAsync();

        Assert.AreEqual(0, data.Scenarios.Length, "a cold-cache fetch failure should degrade to Empty, not throw");
    }
}
