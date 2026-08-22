// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Net;
using System.Text;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Offline, deterministic tests for <see cref="NugetService"/> that redirect the flat-container
/// and registration HTTP GETs through the <see cref="NugetService.HttpGetAsync"/> seam to canned
/// in-memory responses (a purpose-built <c>.nupkg</c>, <c>.nuspec</c>, and registration
/// <c>index.json</c>). This exercises the package download/extraction, transitive dependency
/// resolution, version-listing/paging/filtering, and cache-path logic without any network I/O.
/// Only the seam's default delegate performs real network I/O.
/// <para>
/// Marked <c>[DoNotParallelize]</c> because it swaps the process-wide <see cref="NugetService.HttpGetAsync"/>
/// seam and, for the cache-path tests, temporarily sets the <c>WINAPP_CLI_CACHE_DIRECTORY</c> /
/// <c>NUGET_PACKAGES</c> environment variables; both are restored in cleanup.
/// </para>
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NugetServiceOfflineTests : BaseCommandTests
{
    private const string NuspecNs = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";

    private DirectoryInfo _nugetGlobal = null!;
    private NugetService _service = null!;
    private Func<string, CancellationToken, Task<HttpResponseMessage>> _originalGet = null!;
    private Func<string> _originalUserProfile = null!;
    private string? _originalNugetPackages;
    private string? _originalCacheDir;
    private string? _originalFlatContainer;
    private string? _originalRegistration;
    private string? _originalAuthPrefix;
    private string? _originalAccessToken;

    [TestInitialize]
    public void SetupOffline()
    {
        _originalGet = NugetService.HttpGetAsync;
        _originalUserProfile = NugetService.GetUserProfileDirectory;
        _originalNugetPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        _originalCacheDir = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        _originalFlatContainer = Environment.GetEnvironmentVariable(NugetService.FlatContainerEnvironmentVariable);
        _originalRegistration = Environment.GetEnvironmentVariable(NugetService.RegistrationEnvironmentVariable);
        _originalAuthPrefix = Environment.GetEnvironmentVariable(NugetService.AuthPrefixEnvironmentVariable);
        _originalAccessToken = Environment.GetEnvironmentVariable(NugetService.AzureArtifactsTokenEnvironmentVariable);

        Environment.SetEnvironmentVariable(NugetService.FlatContainerEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(NugetService.RegistrationEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(NugetService.AuthPrefixEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(NugetService.AzureArtifactsTokenEnvironmentVariable, null);

        // A throwaway global dir (not %USERPROFILE%\.winapp) so IsTestOverride() routes the real
        // NugetService cache to <global>\packages, fully isolated from the developer's NuGet cache.
        _nugetGlobal = _tempDirectory.CreateSubdirectory("nugetglobal");
        _service = new NugetService(new StubWinappDirectoryService(_nugetGlobal));
    }

    [TestCleanup]
    public void CleanupOffline()
    {
        NugetService.HttpGetAsync = _originalGet;
        NugetService.GetUserProfileDirectory = _originalUserProfile;
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", _originalNugetPackages);
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _originalCacheDir);
        Environment.SetEnvironmentVariable(NugetService.FlatContainerEnvironmentVariable, _originalFlatContainer);
        Environment.SetEnvironmentVariable(NugetService.RegistrationEnvironmentVariable, _originalRegistration);
        Environment.SetEnvironmentVariable(NugetService.AuthPrefixEnvironmentVariable, _originalAuthPrefix);
        Environment.SetEnvironmentVariable(NugetService.AzureArtifactsTokenEnvironmentVariable, _originalAccessToken);
    }

    // ─────────────────────────────── InstallPackageAsync ───────────────────────────────

    [TestMethod]
    public async Task InstallPackageAsync_DownloadsExtractsAndResolvesTransitiveDependency()
    {
        // Root depends on Dep; Dep has no dependencies. Both should end up installed on disk.
        var rootNupkg = BuildNupkg("root.pkg", NuspecXml("Root.Pkg", ("Dep.Pkg", "[2.0.0, )")));
        var depNupkg = BuildNupkg("dep.pkg", NuspecXml("Dep.Pkg"));

        StubNupkgFeed(("root.pkg/1.0.0", rootNupkg), ("dep.pkg/2.0.0", depNupkg));

        var installed = await _service.InstallPackageAsync("Root.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", installed["Root.Pkg"], "Main package should be recorded at the requested version.");
        Assert.AreEqual("2.0.0", installed["Dep.Pkg"], "Transitive dependency should be resolved to its minimum version.");
        Assert.IsTrue(File.Exists(Path.Combine(PackageDir("root.pkg", "1.0.0"), "root.pkg.nuspec")),
            "The root package's nuspec should have been extracted to the cache.");
        Assert.IsTrue(File.Exists(Path.Combine(PackageDir("dep.pkg", "2.0.0"), "dep.pkg.nuspec")),
            "The dependency package should also have been downloaded and extracted.");
    }

    [TestMethod]
    public async Task InstallPackageAsync_PackageAlreadyOnDisk_SkipsDownloadButStillInstallsMissingDependency()
    {
        // Pre-materialize Root on disk (with a nuspec that declares Dep) so the download path is
        // skipped for Root, but Dep is missing and must still be fetched.
        MaterializePackageOnDisk("root.pkg", "1.0.0", NuspecXml("Root.Pkg", ("Dep.Pkg", "[2.0.0, )")));
        var depNupkg = BuildNupkg("dep.pkg", NuspecXml("Dep.Pkg"));

        // If Root were (incorrectly) re-downloaded the seam would fail the test by throwing.
        NugetService.HttpGetAsync = (url, _) =>
        {
            if (url.Contains("dep.pkg/2.0.0", StringComparison.Ordinal))
            {
                return Task.FromResult(BytesResponse(depNupkg));
            }
            Assert.Fail($"Unexpected download of an already-present package: {url}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var installed = await _service.InstallPackageAsync("Root.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", installed["Root.Pkg"]);
        Assert.AreEqual("2.0.0", installed["Dep.Pkg"], "A missing dependency of an already-present package must still be installed.");
    }

    [TestMethod]
    public async Task InstallPackageAsync_DownloadFails_ThrowsHttpRequestWithStatusCode()
    {
        NugetService.HttpGetAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => _service.InstallPackageAsync("Missing.Pkg", "9.9.9", TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Missing.Pkg", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "NotFound", StringComparison.Ordinal);
        Assert.AreEqual(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [TestMethod]
    public async Task InstallPackageAsync_NotFoundRedactsFeedCredentialsAndQuery()
    {
        var credentialedFeed = new UriBuilder("https", "packages.example.test")
        {
            UserName = "feed-user",
            Password = "feed-password",
            Path = "v3/flat2",
            Query = "token=download-secret",
        }.Uri.AbsoluteUri;
        Environment.SetEnvironmentVariable(NugetService.FlatContainerEnvironmentVariable, credentialedFeed);
        NugetService.HttpGetAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => _service.InstallPackageAsync("Missing.Pkg", "9.9.9", TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "URL: https://packages.example.test/v3/flat2");
        StringAssert.Contains(ex.Message, "Source: https://packages.example.test");
        StringAssert.Contains(ex.Message, "HTTP status: 404 NotFound");
        Assert.IsFalse(ex.Message.Contains("feed-user", StringComparison.Ordinal));
        Assert.IsFalse(ex.Message.Contains("feed-password", StringComparison.Ordinal));
        Assert.IsFalse(ex.Message.Contains("download-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstallPackageAsync_TransportFailureIncludesSourceAndTlsDetails()
    {
        NugetService.HttpGetAsync = (_, _) => throw new HttpRequestException(
            "The SSL connection could not be established.",
            new InvalidOperationException("The remote certificate is invalid."));

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => _service.InstallPackageAsync("Missing.Pkg", "9.9.9", TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "URL: https://api.nuget.org/v3-flatcontainer/missing.pkg/9.9.9/missing.pkg.9.9.9.nupkg");
        StringAssert.Contains(ex.Message, "Source: https://api.nuget.org");
        StringAssert.Contains(ex.Message, "HttpRequestException: The SSL connection could not be established.");
        StringAssert.Contains(ex.Message, "InvalidOperationException: The remote certificate is invalid.");
        StringAssert.Contains(ex.Message, NugetService.FlatContainerEnvironmentVariable);
        StringAssert.Contains(ex.Message, NugetService.RegistrationEnvironmentVariable);
    }

    [TestMethod]
    public async Task InstallPackageAsync_UserCancellationIsNotWrappedAsFeedFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        NugetService.HttpGetAsync = (_, token) => Task.FromCanceled<HttpResponseMessage>(token);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => _service.InstallPackageAsync("Missing.Pkg", "9.9.9", TestTaskContext, cancellation.Token));
    }

    [TestMethod]
    public async Task InstallPackageAsync_TransportFailureRedactsFeedCredentialsAndQuery()
    {
        Environment.SetEnvironmentVariable(
            NugetService.FlatContainerEnvironmentVariable,
            "https://feed-user:feed-password@packages.example.test/v3/flat2?token=secret");
        NugetService.HttpGetAsync = (url, _) => throw new HttpRequestException($"TLS failure for {url}");

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => _service.InstallPackageAsync("Missing.Pkg", "9.9.9", TestTaskContext, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "https://packages.example.test/");
        Assert.IsFalse(ex.Message.Contains("feed-user", StringComparison.Ordinal));
        Assert.IsFalse(ex.Message.Contains("feed-password", StringComparison.Ordinal));
        Assert.IsFalse(ex.Message.Contains("token=secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstallPackageAsync_MalformedNuspec_StillInstallsMainPackage()
    {
        // A nuspec that is not well-formed XML makes dependency resolution throw; that failure is
        // non-fatal and the main package must remain installed.
        const string malformed = "<package><metadata><id>Broken.Pkg</id><version>1.0.0"; // unterminated
        var nupkg = BuildNupkg("broken.pkg", malformed);
        StubNupkgFeed(("broken.pkg/1.0.0", nupkg));

        var installed = await _service.InstallPackageAsync("Broken.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", installed["Broken.Pkg"]);
        Assert.HasCount(1, installed, "A malformed nuspec should yield no dependencies but must not fail the install.");
    }

    [TestMethod]
    public async Task InstallPackageAsync_NuspecNotNamedForPackage_FallsBackToAnyNuspec()
    {
        // The nuspec inside the archive is NOT named "<id>.nuspec"; the reader must fall back to the
        // first *.nuspec found and still resolve its declared dependency.
        var rootNupkg = BuildNupkgWithNamedNuspec("differently-named.nuspec", NuspecXml("Root.Pkg", ("Dep.Pkg", "2.0.0")));
        var depNupkg = BuildNupkg("dep.pkg", NuspecXml("Dep.Pkg"));
        StubNupkgFeed(("root.pkg/1.0.0", rootNupkg), ("dep.pkg/2.0.0", depNupkg));

        var installed = await _service.InstallPackageAsync("Root.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", installed["Dep.Pkg"], "The fallback *.nuspec lookup should still surface dependencies.");
    }

    [TestMethod]
    public async Task InstallPackageAsync_NoNuspecInPackage_InstallsWithoutDependencies()
    {
        // Archive contains no nuspec at all -> no dependencies, but the package still installs.
        var nupkg = BuildNupkgRaw(zip => AddEntry(zip, "lib/net8.0/thing.dll", "binary"));
        StubNupkgFeed(("bare.pkg/1.0.0", nupkg));

        var installed = await _service.InstallPackageAsync("Bare.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, installed);
        Assert.AreEqual("1.0.0", installed["Bare.Pkg"]);
    }

    [TestMethod]
    public async Task InstallPackageAsync_NuspecWithoutNamespace_ResolvesDependencies()
    {
        var rootNuspec = """
            <?xml version="1.0" encoding="utf-8"?>
            <package>
              <metadata>
                <id>Root.Pkg</id>
                <version>1.0.0</version>
                <dependencies>
                  <dependency id="Dep.Pkg" version="2.0.0" />
                </dependencies>
              </metadata>
            </package>
            """;
        var rootNupkg = BuildNupkg("root.pkg", rootNuspec);
        var depNupkg = BuildNupkg("dep.pkg", NuspecXml("Dep.Pkg"));
        StubNupkgFeed(("root.pkg/1.0.0", rootNupkg), ("dep.pkg/2.0.0", depNupkg));

        var installed = await _service.InstallPackageAsync("Root.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", installed["Dep.Pkg"], "Dependencies in a namespace-less nuspec must still be parsed.");
    }

    [TestMethod]
    public async Task InstallPackageAsync_DependencyMissingVersionAttribute_IsSkipped()
    {
        // The dependency without a version attribute is ignored; the package still installs cleanly.
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="{NuspecNs}">
              <metadata>
                <id>Root.Pkg</id>
                <version>1.0.0</version>
                <dependencies>
                  <group targetFramework="net8.0">
                    <dependency id="NoVersion.Pkg" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;
        var nupkg = BuildNupkg("root.pkg", nuspec);
        StubNupkgFeed(("root.pkg/1.0.0", nupkg));

        var installed = await _service.InstallPackageAsync("Root.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(1, installed, "A dependency without a version must be skipped.");
        Assert.IsFalse(installed.ContainsKey("NoVersion.Pkg"));
    }

    [TestMethod]
    public async Task InstallPackageAsync_DiamondDependency_InstallsSharedDependencyOnce()
    {
        // Root -> A and B; both A and B depend on the same C. C must be installed once and the
        // second encounter must hit the "already installed" short-circuit during resolution.
        var root = BuildNupkg("diamond.root", NuspecXml("Diamond.Root", ("Diamond.A", "1.0.0"), ("Diamond.B", "1.0.0")));
        var a = BuildNupkg("diamond.a", NuspecXml("Diamond.A", ("Diamond.C", "1.0.0")));
        var b = BuildNupkg("diamond.b", NuspecXml("Diamond.B", ("Diamond.C", "1.0.0")));
        var c = BuildNupkg("diamond.c", NuspecXml("Diamond.C"));
        StubNupkgFeed(
            ("diamond.root/1.0.0", root),
            ("diamond.a/1.0.0", a),
            ("diamond.b/1.0.0", b),
            ("diamond.c/1.0.0", c));

        var installed = await _service.InstallPackageAsync("Diamond.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        Assert.HasCount(4, installed, "Root + A + B + shared C must each appear exactly once.");
        Assert.AreEqual("1.0.0", installed["Diamond.C"], "The shared transitive dependency must be present.");
    }

    // ─────────────────────────── GetLatestVersionAsync / listing ───────────────────────────

    [TestMethod]
    public async Task GetLatestVersionAsync_InlineItems_ReturnsHighestListedStableVersion()
    {
        StubRegistration("some.pkg", InlinePage(
            ("1.0.0", true),
            ("2.0.0", true),
            ("1.5.0", true),
            ("3.0.0", false))); // unlisted -> excluded despite being highest

        var latest = await _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", latest, "Should pick the highest *listed* stable version.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_PagedItems_FetchesPageByIdUrl()
    {
        // The registration index references a page by @id instead of inlining items.
        const string pageUrl = "https://example.test/registration/some.pkg/page1.json";
        var index = "{\"items\":[{\"@id\":\"" + pageUrl + "\"}]}";
        var page = InlinePage(("1.0.0", true), ("4.2.0", true));

        NugetService.HttpGetAsync = (url, _) => Task.FromResult(
            url.Contains("page1.json", StringComparison.Ordinal) ? JsonResponse(page) : JsonResponse(index));

        var latest = await _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("4.2.0", latest, "Versions from a separately-fetched registration page must be considered.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_UnauthorizedPageRedactsCredentialsAndQuery()
    {
        var credentialedPageUrl = new UriBuilder("https", "example.test")
        {
            UserName = "page-user",
            Password = "page-password",
            Path = "registration/page.json",
            Query = "token=page-secret",
        }.Uri.AbsoluteUri;
        var index = "{\"items\":[{\"@id\":\"" + credentialedPageUrl + "\"}]}";
        NugetService.HttpGetAsync = (url, _) => Task.FromResult(
            url.Contains("page.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(index));

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "URL: https://example.test/registration/page.json");
        StringAssert.Contains(ex.Message, "Source: https://example.test");
        StringAssert.Contains(ex.Message, "HTTP status: 401 Unauthorized");
        Assert.AreEqual(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.IsFalse(ex.Message.Contains("page-user", StringComparison.Ordinal));
        Assert.IsFalse(ex.Message.Contains("page-password", StringComparison.Ordinal));
        Assert.IsFalse(ex.Message.Contains("page-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_PageWithoutIdOrItems_IsSkipped()
    {
        // First page has neither inline items nor an @id (skipped); second page carries the versions.
        const string pageUrl = "https://example.test/registration/some.pkg/page2.json";
        var index = "{\"items\":[{\"count\":0},{\"@id\":\"" + pageUrl + "\"}]}";
        var page = InlinePage(("1.1.0", true));

        NugetService.HttpGetAsync = (url, _) => Task.FromResult(
            url.Contains("page2.json", StringComparison.Ordinal) ? JsonResponse(page) : JsonResponse(index));

        var latest = await _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.1.0", latest);
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_PageWithEmptyIdString_IsSkipped()
    {
        // A page carries an @id that is an empty string; it must be skipped, not fetched.
        var index = "{\"items\":[{\"@id\":\"\"},{" + "\"items\":" + InlineLeaves(("1.2.0", true)) + "}]}";

        StubRegistrationRaw("some.pkg", index);

        var latest = await _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.2.0", latest, "The page with a blank @id must be skipped and the inline page used.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_FetchedPageWithoutItems_IsSkipped()
    {
        // The referenced page is fetched but has no "items" array; it is skipped and the inline page wins.
        const string pageUrl = "https://example.test/registration/some.pkg/empty-page.json";
        var index = "{\"items\":[{\"@id\":\"" + pageUrl + "\"},{" + "\"items\":" + InlineLeaves(("1.3.0", true)) + "}]}";

        NugetService.HttpGetAsync = (url, _) => Task.FromResult(
            url.Contains("empty-page.json", StringComparison.Ordinal)
                ? JsonResponse("{\"count\":0}")
                : JsonResponse(index));

        var latest = await _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.3.0", latest, "A fetched page without items must be skipped, leaving the inline version.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_LeavesWithoutCatalogEntryOrVersion_AreSkipped()
    {
        // A page whose leaves are missing catalogEntry / version / are blank, plus one good entry.
        var page = "{\"items\":[{\"items\":[" +
            "{\"foo\":\"bar\"}," +
            "{\"catalogEntry\":{\"listed\":true}}," +
            "{\"catalogEntry\":{\"version\":\"\",\"listed\":true}}," +
            "{\"catalogEntry\":{\"version\":\"2.3.4\",\"listed\":true}}" +
            "]}]}";
        StubRegistrationRaw("some.pkg", page);

        var latest = await _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("2.3.4", latest, "Malformed leaves must be skipped, leaving the one valid version.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoItemsProperty_Throws()
    {
        StubRegistrationRaw("some.pkg", "{\"count\":0}");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoListedStableVersions_Throws()
    {
        // Only a prerelease exists; Stable mode filters it out, leaving an empty candidate set.
        StubRegistration("some.pkg", InlinePage(("1.0.0-beta", true)));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WindowsAppSdk_StableMode_ExcludesPrerelease()
    {
        StubRegistration("microsoft.windowsappsdk", InlinePage(
            ("1.5.0", true),
            ("1.6.0-preview1", true),
            ("1.6.0-experimental1", true)));

        var latest = await _service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.AreEqual("1.5.0", latest, "Stable mode must exclude preview/experimental SDK builds.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WindowsAppSdk_PreviewMode_ReturnsPreviewOnly()
    {
        StubRegistration("microsoft.windowsappsdk", InlinePage(
            ("1.5.0", true),
            ("1.6.0-preview1", true),
            ("1.6.0-experimental1", true)));

        var latest = await _service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Preview, TestContext.CancellationToken);

        Assert.AreEqual("1.6.0-preview1", latest, "Preview mode must keep only -preview SDK builds.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WindowsAppSdk_ExperimentalMode_ReturnsExperimentalOnly()
    {
        StubRegistration("microsoft.windowsappsdk", InlinePage(
            ("1.5.0", true),
            ("1.6.0-preview1", true),
            ("1.6.0-experimental1", true)));

        var latest = await _service.GetLatestVersionAsync("Microsoft.WindowsAppSDK", SdkInstallMode.Experimental, TestContext.CancellationToken);

        Assert.AreEqual("1.6.0-experimental1", latest, "Experimental mode must keep only -experimental SDK builds.");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_NoneMode_ThrowsArgumentException()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => _service.GetLatestVersionAsync("Some.Pkg", SdkInstallMode.None, TestContext.CancellationToken));
    }

    // ───────────────────────── GetPackageDependenciesAsync (offline) ─────────────────────────

    [TestMethod]
    public async Task GetPackageDependenciesAsync_ReturnsDirectAndTransitive_ExcludesIgnoredPrefixes()
    {
        // A -> B (+ ignored System.Text.Json); B -> C. B is declared as the range "[2.0.0, )" so this
        // also proves the transitive walk normalizes the range to its minimum version before fetching
        // B's nuspec (otherwise the malformed URL would 404 and C would be silently dropped). Result
        // should contain B and C but not the framework/System.* dependency.
        StubNuspecFeed(
            ("a.pkg/1.0.0", NuspecXml("A.Pkg", ("B.Pkg", "[2.0.0, )"), ("System.Text.Json", "8.0.0"))),
            ("b.pkg/2.0.0", NuspecXml("B.Pkg", ("C.Pkg", "3.0.0"))),
            ("c.pkg/3.0.0", NuspecXml("C.Pkg")));

        var deps = await _service.GetPackageDependenciesAsync("A.Pkg", "1.0.0", TestContext.CancellationToken);

        Assert.IsTrue(deps.ContainsKey("B.Pkg"), "Direct dependency B must be present.");
        Assert.IsTrue(deps.ContainsKey("C.Pkg"), "Transitive dependency C must be present.");
        Assert.IsFalse(deps.ContainsKey("System.Text.Json"), "System.* dependencies are filtered out.");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_OpenLowerBoundRange_KeepsDirectDep_AndSkipsMalformedTransitiveFetch()
    {
        // RangeRoot -> RangeChild declared with an open-lower-bound range "(,2.0.0]" (no concrete minimum
        // version). ParseMinimumVersion yields empty for that range, so the transitive walk must SKIP
        // RangeChild rather than requesting a malformed ".../rangechild.pkg//rangechild.pkg.nuspec". The
        // direct dependency itself is still returned. A unique package id avoids the static DependencyCache
        // colliding with other tests.
        var requestedUrls = new List<string>();
        NugetService.HttpGetAsync = (url, _) =>
        {
            requestedUrls.Add(url);
            if (url.Contains("/rangeroot.pkg/1.0.0/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(NuspecXml("RangeRoot.Pkg", ("RangeChild.Pkg", "(,2.0.0]"))),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };

        var deps = await _service.GetPackageDependenciesAsync("RangeRoot.Pkg", "1.0.0", TestContext.CancellationToken);

        Assert.IsTrue(deps.ContainsKey("RangeChild.Pkg"), "The direct dependency must still be returned.");
        Assert.IsFalse(
            requestedUrls.Any(u => u.Contains("rangechild", StringComparison.OrdinalIgnoreCase)),
            "An open-lower-bound range has no minimum version, so no (malformed) transitive nuspec fetch should be issued for it.");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_StripsVersionRangeBrackets()
    {
        StubNuspecFeed(
            ("brk.pkg/1.0.0", NuspecXml("Brk.Pkg", ("Ranged.Pkg", "[1.2.3, )"))),
            ("ranged.pkg/1.2.3", NuspecXml("Ranged.Pkg")));

        var deps = await _service.GetPackageDependenciesAsync("Brk.Pkg", "1.0.0", TestContext.CancellationToken);

        Assert.AreEqual("1.2.3, ", deps["Ranged.Pkg"], "Brackets/parentheses are stripped, the comma-form is preserved.");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_CachesResult_SecondCallDoesNotHitFeed()
    {
        StubNuspecFeed(
            ("cache.pkg/1.0.0", NuspecXml("Cache.Pkg", ("Only.Dep", "1.0.0"))),
            ("only.dep/1.0.0", NuspecXml("Only.Dep")));

        var first = await _service.GetPackageDependenciesAsync("Cache.Pkg", "1.0.0", TestContext.CancellationToken);
        Assert.IsTrue(first.ContainsKey("Only.Dep"));

        // Any subsequent HTTP call now throws; a cache hit must avoid it entirely.
        NugetService.HttpGetAsync = (_, _) => throw new InvalidOperationException("feed must not be hit on a cache hit");

        var second = await _service.GetPackageDependenciesAsync("Cache.Pkg", "1.0.0", TestContext.CancellationToken);
        Assert.IsTrue(second.ContainsKey("Only.Dep"), "The cached dependency set should be returned without a network call.");
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_NuspecNotFound_ReturnsEmpty()
    {
        NugetService.HttpGetAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        var deps = await _service.GetPackageDependenciesAsync("Ghost.Pkg", "1.0.0", TestContext.CancellationToken);

        Assert.IsEmpty(deps, "A missing nuspec yields an empty dependency set, not an error.");
    }

    // ───────────────────────────── cache path resolution ─────────────────────────────

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_TestOverride_UsesPackagesSubdirectory()
    {
        // WINAPP_CLI_CACHE_DIRECTORY unset + a non-default global dir => test-override path.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", null);
        var dir = _service.GetNuGetGlobalPackagesDir();

        Assert.AreEqual(Path.Combine(_nugetGlobal.FullName, "packages"), dir.FullName);
        Assert.IsTrue(dir.Exists, "The override packages directory should be created on demand.");
    }

    [TestMethod]
    public void GetNuGetPackageDir_ComposesLowercasedIdAndVersionUnderCache()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", null);
        var dir = _service.GetNuGetPackageDir("Contoso.Widgets", "1.2.3");

        var expected = Path.Combine(_nugetGlobal.FullName, "packages", "contoso.widgets", "1.2.3");
        Assert.AreEqual(expected, dir.FullName);
    }

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_NugetPackagesEnv_TakesPriority()
    {
        // Setting WINAPP_CLI_CACHE_DIRECTORY makes IsTestOverride() false, so the NUGET_PACKAGES
        // environment variable becomes the source of truth.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _tempDirectory.FullName);
        var envTarget = Path.Combine(_tempDirectory.FullName, "nuget-packages-env");
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", envTarget);

        var dir = _service.GetNuGetGlobalPackagesDir();

        Assert.AreEqual(envTarget, dir.FullName);
        Assert.IsTrue(dir.Exists, "The NUGET_PACKAGES directory should be created if missing.");
    }

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_NoOverrides_FallsBackToUserProfileNugetAndCreatesIt()
    {
        // IsTestOverride() false (WINAPP_CLI_CACHE_DIRECTORY set) and NUGET_PACKAGES cleared =>
        // the default %USERPROFILE%\.nuget\packages location is returned. The user-profile lookup is
        // redirected to a temp directory so the on-demand create-branch runs hermetically, without
        // mutating the developer's (or CI runner's) real user profile.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _tempDirectory.FullName);
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", null);
        var fakeProfile = _tempDirectory.CreateSubdirectory("fakeprofile");
        NugetService.GetUserProfileDirectory = () => fakeProfile.FullName;

        var dir = _service.GetNuGetGlobalPackagesDir();

        var expected = Path.Combine(fakeProfile.FullName, ".nuget", "packages");
        Assert.AreEqual(expected, dir.FullName);
        Assert.IsTrue(dir.Exists, "The default .nuget/packages directory should be created on demand.");
    }

    [TestMethod]
    public void GetUserProfileDirectory_Default_ResolvesToWindowsUserProfile()
    {
        // Pins the seam's production default: with no override it must resolve to the real
        // %USERPROFILE% so the computed NuGet global-packages path stays byte-for-byte the
        // same as the pre-seam behavior. CleanupOffline restores the default before each test.
        Assert.AreEqual(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            NugetService.GetUserProfileDirectory());
    }

    [TestMethod]
    public async Task FeedEndpoints_DefaultToNuGetOrg()
    {
        string? downloadUrl = null;
        NugetService.HttpGetAsync = (url, _) =>
        {
            downloadUrl = url;
            return Task.FromResult(BytesResponse(BuildNupkg("test.pkg", NuspecXml("Test.Pkg"))));
        };

        await _service.InstallPackageAsync("Test.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

        StringAssert.StartsWith(downloadUrl, "https://api.nuget.org/v3-flatcontainer/", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task FeedEndpoints_UseConfiguredAzureArtifactsEndpoints()
    {
        const string flatContainer = "https://pkgs.example.test/feed/flat2/";
        const string registration = "https://pkgs.example.test/feed/registrations2-semver2/";
        Environment.SetEnvironmentVariable(NugetService.FlatContainerEnvironmentVariable, flatContainer);
        Environment.SetEnvironmentVariable(NugetService.RegistrationEnvironmentVariable, registration);
        var requestedUrls = new List<string>();
        NugetService.HttpGetAsync = (url, _) =>
        {
            requestedUrls.Add(url);
            return Task.FromResult(url.Contains("registrations2-semver2", StringComparison.Ordinal)
                ? JsonResponse("{\"items\":[" + InlinePage(("1.0.0", true)) + "]}")
                : BytesResponse(BuildNupkg("test.pkg", NuspecXml("Test.Pkg"))));
        };

        await _service.InstallPackageAsync("Test.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);
        await _service.GetLatestVersionAsync("Test.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

        Assert.IsTrue(requestedUrls.Any(url => url.StartsWith(flatContainer, StringComparison.Ordinal)));
        Assert.IsTrue(requestedUrls.Any(url => url.StartsWith(registration, StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CreateHttpRequest_ScopesAzureArtifactsTokenToConfiguredPrefix()
    {
        const string authPrefix = "https://pkgs.example.test/feed/";
        Environment.SetEnvironmentVariable(NugetService.AuthPrefixEnvironmentVariable, authPrefix);
        Environment.SetEnvironmentVariable(NugetService.AzureArtifactsTokenEnvironmentVariable, "test-token");

        using var internalRequest = NugetService.CreateHttpRequest(authPrefix + "flat2/test.pkg/index.json");
        using var publicRequest = NugetService.CreateHttpRequest("https://api.nuget.org/v3-flatcontainer/test.pkg/index.json");

        Assert.AreEqual("Basic", internalRequest.Headers.Authorization?.Scheme);
        Assert.AreEqual(
            "VssSessionToken:test-token",
            Encoding.UTF8.GetString(Convert.FromBase64String(internalRequest.Headers.Authorization!.Parameter!)));
        Assert.IsNull(publicRequest.Headers.Authorization);
    }

    // ───────────────────────────────────── helpers ─────────────────────────────────────

    private string PackageDir(string lowerId, string version)
        => Path.Combine(_nugetGlobal.FullName, "packages", lowerId, version);

    private void MaterializePackageOnDisk(string lowerId, string version, string nuspec)
    {
        var dir = PackageDir(lowerId, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{lowerId}.nuspec"), nuspec);
    }

    /// <summary>
    /// Serves each requested .nupkg by matching the slash-delimited <c>/&lt;id&gt;/&lt;version&gt;/</c>
    /// path segment in the flat-container URL. Matching the surrounding slashes (rather than a bare
    /// substring) means a malformed request such as <c>/pkg/1.2.3, /</c> — which a raw range that was
    /// never normalized would produce — correctly misses and 404s instead of being silently served.
    /// </summary>
    private static void StubNupkgFeed(params (string idVersionSegment, byte[] bytes)[] packages)
    {
        NugetService.HttpGetAsync = (url, _) =>
        {
            foreach (var (segment, bytes) in packages)
            {
                if (url.Contains($"/{segment}/", StringComparison.Ordinal))
                {
                    return Task.FromResult(BytesResponse(bytes));
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };
    }

    /// <summary>
    /// Serves each requested .nuspec by matching the slash-delimited <c>/&lt;id&gt;/&lt;version&gt;/</c>
    /// path segment in the flat-container URL (see <see cref="StubNupkgFeed"/> for why the surrounding
    /// slashes matter — an unnormalized range would build a malformed URL that must not match).
    /// </summary>
    private static void StubNuspecFeed(params (string idVersionSegment, string nuspec)[] nuspecs)
    {
        NugetService.HttpGetAsync = (url, _) =>
        {
            foreach (var (segment, nuspec) in nuspecs)
            {
                if (url.Contains($"/{segment}/", StringComparison.Ordinal))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(nuspec, Encoding.UTF8, "application/xml"),
                    });
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };
    }

    private static void StubRegistration(string lowerId, string page)
        => StubRegistrationRaw(lowerId, "{\"items\":[" + page + "]}");

    private static void StubRegistrationRaw(string lowerId, string json)
    {
        NugetService.HttpGetAsync = (url, _) => Task.FromResult(
            url.Contains(lowerId, StringComparison.Ordinal) ? JsonResponse(json) : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Builds the JSON array of leaf entries: [ {catalogEntry...}, ... ].</summary>
    private static string InlineLeaves(params (string version, bool listed)[] versions)
        => "[" + string.Join(",", versions.Select(v =>
            "{\"catalogEntry\":{\"version\":\"" + v.version + "\",\"listed\":" + (v.listed ? "true" : "false") + "}}")) + "]";

    /// <summary>Builds one inline registration page object (the "{...}" that goes inside items[]).</summary>
    private static string InlinePage(params (string version, bool listed)[] versions)
        => "{\"items\":" + InlineLeaves(versions) + "}";

    private static string NuspecXml(string id, params (string depId, string depVersion)[] deps)
    {
        var depXml = string.Join("\n", deps.Select(d => $"        <dependency id=\"{d.depId}\" version=\"{d.depVersion}\" />"));
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            $"<package xmlns=\"{NuspecNs}\">\n" +
            "  <metadata>\n" +
            $"    <id>{id}</id>\n" +
            "    <version>1.0.0</version>\n" +
            "    <dependencies>\n" +
            "      <group targetFramework=\"net8.0\">\n" +
            depXml + "\n" +
            "      </group>\n" +
            "    </dependencies>\n" +
            "  </metadata>\n" +
            "</package>\n";
    }

    private static byte[] BuildNupkg(string lowerId, string nuspec)
        => BuildNupkgWithNamedNuspec($"{lowerId}.nuspec", nuspec);

    private static byte[] BuildNupkgWithNamedNuspec(string nuspecEntryName, string nuspec)
        => BuildNupkgRaw(zip =>
        {
            AddEntry(zip, nuspecEntryName, nuspec);
            AddEntry(zip, "lib/net8.0/placeholder.dll", "binary");
        });

    private static byte[] BuildNupkgRaw(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            fill(zip);
        }
        return ms.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesResponse(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}
