// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Minimal <see cref="IWinappDirectoryService"/> stub returning a caller-controlled
/// global directory, so NuGet cache-path resolution can be driven deterministically.
/// </summary>
internal sealed class StubWinappDirectoryService(DirectoryInfo global) : IWinappDirectoryService
{
    public DirectoryInfo GetGlobalWinappDirectory() => global;
    public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null) => global;
    public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory) { }
}

/// <summary>
/// Offline tests for <see cref="NugetService"/> that drive the download / version /
/// dependency flows through the injected <c>Http</c> seam backed by a fake handler,
/// so no real network traffic occurs.
/// </summary>
[TestClass]
public class NugetServiceOfflineTests : BaseCommandTests
{
    private const string FlatIndex = "https://api.nuget.org/v3-flatcontainer";
    private const string RegistrationIndex = "https://api.nuget.org/v3/registration5-semver1";

    private DirectoryInfo NewGlobal() =>
        _tempDirectory.CreateSubdirectory("global-" + Guid.NewGuid().ToString("N"));

    private NugetService NewService(FakeHttpMessageHandler handler, out DirectoryInfo global)
    {
        global = NewGlobal();
        return new NugetService(new StubWinappDirectoryService(global)) { Http = new HttpClient(handler) };
    }

    private static byte[] BuildNupkg(string id, params (string DepId, string DepVersion)[] deps)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"{id.ToLowerInvariant()}.nuspec");
            using var w = new StreamWriter(entry.Open());
            var depXml = string.Concat(deps.Select(d => $"<dependency id=\"{d.DepId}\" version=\"{d.DepVersion}\" />"));
            w.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{id}</id>
                    <version>1.0.0</version>
                    <dependencies>{depXml}</dependencies>
                  </metadata>
                </package>
                """);
        }
        return ms.ToArray();
    }

    private static string NuspecXml(string id, params (string DepId, string DepVersion)[] deps)
    {
        var depXml = string.Concat(deps.Select(d => $"<dependency id=\"{d.DepId}\" version=\"{d.DepVersion}\" />"));
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>1.0.0</version>
                <dependencies>{depXml}</dependencies>
              </metadata>
            </package>
            """;
    }

    private static FakeHttpMessageHandler NupkgHandler(string id, string version, byte[] content)
    {
        var lid = id.ToLowerInvariant();
        var lver = version.ToLowerInvariant();
        return new FakeHttpMessageHandler()
            .WhenUriContains($"/{lid}/{lver}/{lid}.{lver}.nupkg", HttpStatusCode.OK, content);
    }

    private static string RegIndexInline(params (string Version, bool Listed)[] versions)
    {
        var items = string.Join(",", versions.Select(v =>
            $$"""{ "catalogEntry": { "version": "{{v.Version}}", "listed": {{(v.Listed ? "true" : "false")}} } }"""));
        return $$"""{ "items": [ { "items": [ {{items}} ] } ] }""";
    }

    // A registration *page* (the document fetched via a page's "@id") exposes leaf
    // catalog items directly under "items" — one level shallower than the index.
    private static string RegPage(params (string Version, bool Listed)[] versions)
    {
        var items = string.Join(",", versions.Select(v =>
            $$"""{ "catalogEntry": { "version": "{{v.Version}}", "listed": {{(v.Listed ? "true" : "false")}} } }"""));
        return $$"""{ "items": [ {{items}} ] }""";
    }

    // ── cache-path resolution ───────────────────────────────────────────

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_TestOverride_ReturnsPackagesSubdir()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out var global);

        var dir = svc.GetNuGetGlobalPackagesDir();

        Assert.AreEqual(Path.Combine(global.FullName, "packages"), dir.FullName);
        Assert.IsTrue(dir.Exists, "packages subdir should be created");
    }

    [TestMethod]
    public void GetNuGetPackageDir_CombinesLowercasedIdAndVersion()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out var global);

        var dir = svc.GetNuGetPackageDir("My.Package", "2.3.4");

        Assert.AreEqual(Path.Combine(global.FullName, "packages", "my.package", "2.3.4"), dir.FullName);
    }

    // ── InstallPackageAsync ─────────────────────────────────────────────

    [TestMethod]
    public async Task InstallPackageAsync_DownloadsExtractsAndResolvesDependency()
    {
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains("/main/1.0.0/main.1.0.0.nupkg", HttpStatusCode.OK, BuildNupkg("Main", ("Dep", "1.0.0")))
            .WhenUriContains("/dep/1.0.0/dep.1.0.0.nupkg", HttpStatusCode.OK, BuildNupkg("Dep"));
        var svc = NewService(handler, out var global);

        var result = await svc.InstallPackageAsync("Main", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(result.ContainsKey("Main"), "Main should be installed");
        Assert.IsTrue(result.ContainsKey("Dep"), "transitive dependency should be installed");
        Assert.IsTrue(Directory.Exists(Path.Combine(global.FullName, "packages", "main", "1.0.0")));
        Assert.IsTrue(Directory.Exists(Path.Combine(global.FullName, "packages", "dep", "1.0.0")));
    }

    [TestMethod]
    public async Task InstallPackageAsync_DownloadFails_Throws()
    {
        var handler = new FakeHttpMessageHandler { NotMatchedStatus = HttpStatusCode.NotFound };
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.InstallPackageAsync("Missing", "9.9.9", TestTaskContext, TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "Failed to download");
    }

    [TestMethod]
    public async Task InstallPackageAsync_AlreadyOnDisk_SkipsDownloadButResolvesDeps()
    {
        var svc = NewService(new FakeHttpMessageHandler { NotMatchedStatus = HttpStatusCode.InternalServerError }, out var global);
        // Pre-create the package dir with a nuspec that has no resolvable deps.
        var pkgDir = Directory.CreateDirectory(Path.Combine(global.FullName, "packages", "ondisk", "1.0.0"));
        await File.WriteAllTextAsync(Path.Combine(pkgDir.FullName, "ondisk.nuspec"), NuspecXml("OnDisk"));

        // No handler rule matches → any download attempt would 500; success proves no download happened.
        var result = await svc.InstallPackageAsync("OnDisk", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(result.ContainsKey("OnDisk"));
    }

    [TestMethod]
    public async Task InstallPackageAsync_CyclicDependency_TerminatesViaAlreadyInstalledGuard()
    {
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains("/a/1.0.0/a.1.0.0.nupkg", HttpStatusCode.OK, BuildNupkg("A", ("B", "1.0.0")))
            .WhenUriContains("/b/1.0.0/b.1.0.0.nupkg", HttpStatusCode.OK, BuildNupkg("B", ("A", "1.0.0")));
        var svc = NewService(handler, out _);

        var result = await svc.InstallPackageAsync("A", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(result.ContainsKey("A"));
        Assert.IsTrue(result.ContainsKey("B"));
    }

    [TestMethod]
    public async Task InstallPackageAsync_MalformedNuspec_DependencyResolutionSwallowsError()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out var global);
        var pkgDir = Directory.CreateDirectory(Path.Combine(global.FullName, "packages", "broken", "1.0.0"));
        await File.WriteAllTextAsync(Path.Combine(pkgDir.FullName, "broken.nuspec"), "<not-valid-xml");

        // Malformed nuspec makes ReadDependenciesFromNuspec throw; ResolveDependenciesAsync
        // must swallow it and still report the package as installed.
        var result = await svc.InstallPackageAsync("Broken", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(result.ContainsKey("Broken"));
    }

    [TestMethod]
    public async Task InstallPackageAsync_NuspecByFallbackName_ResolvesEmptyDeps()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out var global);
        var pkgDir = Directory.CreateDirectory(Path.Combine(global.FullName, "packages", "fallback", "1.0.0"));
        // nuspec named differently from the package id → exercises the "find any .nuspec" fallback.
        await File.WriteAllTextAsync(Path.Combine(pkgDir.FullName, "renamed.nuspec"), NuspecXml("Fallback"));

        var result = await svc.InstallPackageAsync("Fallback", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(result.ContainsKey("Fallback"));
    }

    [TestMethod]
    public async Task InstallPackageAsync_NoNuspecPresent_ResolvesEmptyDeps()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out var global);
        // Package dir exists but contains no .nuspec at all.
        Directory.CreateDirectory(Path.Combine(global.FullName, "packages", "empty", "1.0.0"));

        var result = await svc.InstallPackageAsync("Empty", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.IsTrue(result.ContainsKey("Empty"));
    }

    // ── GetLatestVersionAsync / GetListedVersionsAsync ──────────────────

    [TestMethod]
    public async Task GetLatestVersionAsync_NoneMode_Throws()
    {
        var svc = NewService(new FakeHttpMessageHandler(), out _);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            svc.GetLatestVersionAsync("AnyPackage", SdkInstallMode.None, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WinappSdk_StableFiltersPrerelease()
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/microsoft.windowsappsdk/index.json", HttpStatusCode.OK,
            RegIndexInline(("1.0.0", true), ("1.1.0", true), ("2.0.0-preview1", true)));
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.1.0", version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WinappSdk_PreviewFilter()
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/microsoft.windowsappsdk/index.json", HttpStatusCode.OK,
            RegIndexInline(("1.0.0", true), ("2.0.0-preview1", true), ("2.0.0-preview2", true)));
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Preview, TestContext.CancellationToken);

        Assert.AreEqual("2.0.0-preview2", version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WinappSdk_ExperimentalFilter()
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/microsoft.windowsappsdk/index.json", HttpStatusCode.OK,
            RegIndexInline(("1.0.0", true), ("3.0.0-experimental1", true)));
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Experimental, TestContext.CancellationToken);

        Assert.AreEqual("3.0.0-experimental1", version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NonSdk_StableFiltersPrerelease()
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/some.package/index.json", HttpStatusCode.OK,
            RegIndexInline(("1.0.0", true), ("2.0.0-beta", true)));
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("Some.Package", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NonSdk_PreviewKeepsPrerelease()
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/some.package/index.json", HttpStatusCode.OK,
            RegIndexInline(("1.0.0", true), ("2.0.0-beta", true)));
        var svc = NewService(handler, out _);

        // For non-SDK packages, non-Stable modes apply no filtering → prerelease wins by version sort.
        var version = await svc.GetLatestVersionAsync("Some.Package", SdkInstallMode.Preview, TestContext.CancellationToken);

        Assert.AreEqual("2.0.0-beta", version);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoVersionsAfterFilter_Throws()
    {
        // Only an unlisted version → GetListedVersions returns empty → throw.
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/empty.package/index.json", HttpStatusCode.OK,
            RegIndexInline(("1.0.0", false)));
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.GetLatestVersionAsync("Empty.Package", SdkInstallMode.Stable, TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "No versions found");
    }

    [TestMethod]
    public async Task GetListedVersionsAsync_PagedItems_FetchesPageById()
    {
        var pageUrl = $"{RegistrationIndex}/paged.package/page/1.json";
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains("/paged.package/index.json", HttpStatusCode.OK,
                $$"""{ "items": [ { "@id": "{{pageUrl}}" } ] }""")
            .WhenUriContains("/paged.package/page/1.json", HttpStatusCode.OK,
                RegPage(("4.0.0", true)));
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("Paged.Package", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("4.0.0", version);
    }

    [TestMethod]
    public async Task GetListedVersionsAsync_NoItemsProperty_Throws()
    {
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/noitems.package/index.json", HttpStatusCode.OK, """{ "count": 0 }""");
        var svc = NewService(handler, out _);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.GetLatestVersionAsync("NoItems.Package", SdkInstallMode.Stable, TestContext.CancellationToken));
        StringAssert.Contains(ex.Message, "No versions found");
    }

    [TestMethod]
    public async Task GetListedVersionsAsync_PageWithoutItemsOrId_IsSkipped()
    {
        // First page has neither inline "items" nor an "@id" → must be skipped (continue).
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/skip.package/index.json", HttpStatusCode.OK,
            """{ "items": [ { "junk": 1 }, { "items": [ { "catalogEntry": { "version": "1.0.0", "listed": true } } ] } ] }""");
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("Skip.Package", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", version);
    }

    [TestMethod]
    public async Task GetListedVersionsAsync_PageWithEmptyId_IsSkipped()
    {
        // First page has an empty "@id" → must be skipped (continue).
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/emptyid.package/index.json", HttpStatusCode.OK,
            """{ "items": [ { "@id": "" }, { "items": [ { "catalogEntry": { "version": "2.0.0", "listed": true } } ] } ] }""");
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("EmptyId.Package", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", version);
    }

    [TestMethod]
    public async Task GetListedVersionsAsync_FetchedPageWithoutItems_IsSkipped()
    {
        var pageUrl = $"{RegistrationIndex}/fetchedpage.package/p/1.json";
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains("/fetchedpage.package/index.json", HttpStatusCode.OK,
                $$"""{ "items": [ { "@id": "{{pageUrl}}" }, { "items": [ { "catalogEntry": { "version": "5.0.0", "listed": true } } ] } ] }""")
            // The fetched page document lacks an "items" array → that page contributes nothing.
            .WhenUriContains("/fetchedpage.package/p/1.json", HttpStatusCode.OK, """{ "count": 0 }""");
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("FetchedPage.Package", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("5.0.0", version);
    }

    [TestMethod]
    public async Task GetListedVersionsAsync_LeafWithoutCatalogEntry_IsSkipped()
    {
        // One leaf item has no "catalogEntry" → skipped; the valid one is still returned.
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            "/noleaf.package/index.json", HttpStatusCode.OK,
            """{ "items": [ { "items": [ { "junk": 1 }, { "catalogEntry": { "version": "3.0.0", "listed": true } } ] } ] }""");
        var svc = NewService(handler, out _);

        var version = await svc.GetLatestVersionAsync("NoLeaf.Package", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("3.0.0", version);
    }

    // ── GetPackageDependenciesAsync / FetchDirectDependenciesAsync ──────

    [TestMethod]
    public async Task GetPackageDependenciesAsync_FetchesTransitiveAndCaches()
    {
        var id = "Root." + Guid.NewGuid().ToString("N");
        var mid = "Mid." + Guid.NewGuid().ToString("N");
        var handler = new FakeHttpMessageHandler()
            .WhenUriContains($"/{id.ToLowerInvariant()}/1.0.0/{id.ToLowerInvariant()}.nuspec", HttpStatusCode.OK, NuspecXml(id, (mid, "2.0.0")))
            .WhenUriContains($"/{mid.ToLowerInvariant()}/2.0.0/{mid.ToLowerInvariant()}.nuspec", HttpStatusCode.OK, NuspecXml(mid));
        var svc = NewService(handler, out _);

        var deps = await svc.GetPackageDependenciesAsync(id, "1.0.0", TestContext.CancellationToken);
        Assert.IsTrue(deps.ContainsKey(mid), "direct dependency should be present");

        // Second call returns from cache (no new requests needed).
        var requestsAfterFirst = handler.Requests.Count;
        var again = await svc.GetPackageDependenciesAsync(id, "1.0.0", TestContext.CancellationToken);
        Assert.IsTrue(again.ContainsKey(mid));
        Assert.AreEqual(requestsAfterFirst, handler.Requests.Count, "cached call should not issue new HTTP requests");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_NuspecFetchFails_ReturnsEmpty()
    {
        var handler = new FakeHttpMessageHandler { NotMatchedStatus = HttpStatusCode.NotFound };
        var svc = NewService(handler, out _);

        var deps = await svc.GetPackageDependenciesAsync("Nope." + Guid.NewGuid().ToString("N"), "1.0.0", TestContext.CancellationToken);

        Assert.IsEmpty(deps);
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_IgnoredPrefixDependency_IsFiltered()
    {
        var id = "Filtered." + Guid.NewGuid().ToString("N");
        var handler = new FakeHttpMessageHandler().WhenUriContains(
            $"/{id.ToLowerInvariant()}/1.0.0/{id.ToLowerInvariant()}.nuspec", HttpStatusCode.OK,
            NuspecXml(id, ("NETStandard.Library", "[2.0.3]"), ("Real.Dep", "[1.2.3]")));
        // Real.Dep nuspec 404s → transitive resolution returns empty for it (non-fatal).
        var svc = NewService(handler, out _);

        var deps = await svc.GetPackageDependenciesAsync(id, "1.0.0", TestContext.CancellationToken);

        Assert.IsFalse(deps.ContainsKey("NETStandard.Library"), "ignored-prefix dependency should be filtered out");
        Assert.IsTrue(deps.ContainsKey("Real.Dep"), "non-ignored dependency should be kept");
        Assert.AreEqual("1.2.3", deps["Real.Dep"], "version brackets should be stripped");
    }
}

/// <summary>
/// Serialized tests for the NUGET_PACKAGES / default cache-path branches, which require
/// <c>IsTestOverride == false</c>. A stub whose global directory equals the real
/// <c>~/.winapp</c> disables the override without touching WINAPP_CLI_CACHE_DIRECTORY
/// (which other tests rely on being empty). Only this class reads/writes NUGET_PACKAGES,
/// so serializing these two tests avoids any cross-test env-var races.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NugetServiceEnvTests : BaseCommandTests
{
    private static DirectoryInfo UserWinappDir() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp"));

    private static void RunWithEnv(Action body, params (string Key, string? Value)[] vars)
    {
        var saved = vars.Select(v => (v.Key, Old: Environment.GetEnvironmentVariable(v.Key))).ToArray();
        try
        {
            foreach (var (key, value) in vars)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            body();
        }
        finally
        {
            foreach (var (key, old) in saved)
            {
                Environment.SetEnvironmentVariable(key, old);
            }
        }
    }

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_NugetPackagesEnvSet_ReturnsAndCreatesIt()
    {
        var svc = new NugetService(new StubWinappDirectoryService(UserWinappDir()));
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "nugetenv-" + Guid.NewGuid().ToString("N")));

        RunWithEnv(() =>
        {
            var dir = svc.GetNuGetGlobalPackagesDir();
            Assert.AreEqual(target.FullName, dir.FullName);
            Assert.IsTrue(Directory.Exists(dir.FullName), "the NUGET_PACKAGES directory should be created if missing");
        }, ("NUGET_PACKAGES", target.FullName));
    }

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_NoEnv_ReturnsUserProfileNugetPackages()
    {
        var svc = new NugetService(new StubWinappDirectoryService(UserWinappDir()));
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        RunWithEnv(() =>
        {
            var dir = svc.GetNuGetGlobalPackagesDir();
            Assert.AreEqual(expected, dir.FullName);
        }, ("NUGET_PACKAGES", null));
    }
}
