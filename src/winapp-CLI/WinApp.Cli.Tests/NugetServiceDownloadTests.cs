// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed download/install/authentication tests: real download + extract from local folder
/// feeds (including transitive dependencies), multi-source download failover, and authenticated private
/// feeds via an in-process HTTP Basic-auth feed (<see cref="BasicAuthNuGetFeed"/>) — including resolving the
/// latest version against a feed that exposes only <c>PackageBaseAddress</c> (no registration resource).
/// Source/configuration and version-selection behavior lives in <see cref="NugetServiceFeedTests"/>; the
/// shared feed-authoring helpers live in <see cref="NugetFeedTestHelpers"/> (imported via <c>using static</c>).
/// </summary>
[TestClass]
public class NugetServiceDownloadTests : BaseCommandTests
{
    #region Download / install Tests

    [TestMethod]
    public async Task InstallPackageAsync_DependencyHasExclusiveLowerBound_InstallsLowestSatisfyingVersion()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // Root's .nuspec declares its dependency with an EXCLUSIVE lower bound (1.0.0, 2.0.0]. Installing
            // the lower bound 1.0.0 would be wrong (the range excludes it); the install must resolve and
            // extract the lowest listed satisfying version, 1.5.0.
            WriteNupkgToFeed(feed, "Install.Root", "1.0.0", ("Install.Child", "(1.0.0, 2.0.0]"));
            WriteNupkgToFeed(feed, "Install.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Install.Child", "1.5.0");
            WriteNupkgToFeed(feed, "Install.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Install.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.TryGetValue("Install.Child", out var childVersion), "The transitive dependency must be resolved and installed.");
            Assert.AreEqual("1.5.0", childVersion, "The exclusive lower bound (1.0.0) must be excluded; the lowest satisfying version (1.5.0) is installed.");
            Assert.IsTrue(service.GetNuGetPackageDir("Install.Child", "1.5.0").Exists, "The resolved dependency version must be extracted on disk.");
            Assert.IsFalse(service.GetNuGetPackageDir("Install.Child", "1.0.0").Exists, "The excluded lower-bound version must NOT be installed.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_LocalFeed_InstallsPackageAndNormalizedTransitiveDependency()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        // If it is set, the install targets the shared cache and could pass on a pre-populated cache
        // without ever exercising the local feed, so skip to keep the assertion meaningful.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // Package A depends on Package B; both live only in the local feed (no network).
            WriteNupkgToFeed(feed, "Winapp.TestA", "1.0.0", ("Winapp.TestB", "1.0.0"));
            WriteNupkgToFeed(feed, "Winapp.TestB", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            // Request with a "1.0" shorthand to also exercise version normalization end-to-end.
            var installed = await service.InstallPackageAsync("Winapp.TestA", "1.0", TestTaskContext, TestContext.CancellationToken);

            // The package and its transitive dependency are both recorded with canonical (normalized) versions.
            Assert.IsTrue(installed.ContainsKey("Winapp.TestA"), "Main package should be recorded as installed.");
            Assert.AreEqual("1.0.0", installed["Winapp.TestA"], "Main package version should be normalized.");
            Assert.IsTrue(installed.ContainsKey("Winapp.TestB"), "Transitive dependency should be resolved and installed.");
            Assert.AreEqual("1.0.0", installed["Winapp.TestB"], "Dependency version should be normalized.");

            // Both are extracted into the configured global packages folder using the normalized layout.
            Assert.IsTrue(service.GetNuGetPackageDir("Winapp.TestA", "1.0.0").Exists, "Main package should be extracted on disk.");
            Assert.IsTrue(service.GetNuGetPackageDir("Winapp.TestB", "1.0.0").Exists, "Dependency should be extracted on disk.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_PackageMissingFromFeed_ThrowsActionableError()
    {
        // NUGET_PACKAGES overrides the config's globalPackagesFolder; skip when it is set so the install
        // is exercised against the isolated local feed rather than the shared cache.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // The feed exists but does not contain the requested package: the content download fails on
            // every eligible source, which must surface as an actionable error rather than silently succeeding.
            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Winapp.DoesNotExist", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "Winapp.DoesNotExist", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_FirstSourcesUnusable_FailsOverToSourceWithPackage()
    {
        // NUGET_PACKAGES overrides the config's globalPackagesFolder; skip when it is set so the install
        // is exercised against the isolated local feeds rather than the shared cache.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feeds would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            // Three eligible sources (all mapped to '*'), queried in listed order:
            //   1. "broken" - an unreachable HTTPS feed whose service index cannot be loaded, so acquiring
            //                 FindPackageByIdResource throws FatalProtocolException (resource-acquisition
            //                 failover).
            //   2. "empty"  - a real local folder feed that does NOT contain the package, so
            //                 CopyNupkgToStreamAsync returns false (CopyNupkg failover).
            //   3. "good"   - a local folder feed that DOES contain the package.
            // A downloader that stopped at the first failing source (instead of continuing the loop) would
            // throw; a successful install proves it fails over past both an unreachable source and a
            // missing one and installs from the third. The broken source uses the reserved '.invalid' TLD
            // (RFC 6761), which never resolves, keeping the test deterministic and offline.
            var empty = new DirectoryInfo(Path.Combine(root.FullName, "empty"));
            empty.Create();
            var good = new DirectoryInfo(Path.Combine(root.FullName, "good"));
            good.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            WriteNupkgToFeed(good, "Failover.Pkg", "1.0.0");

            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="broken" value="https://nuget.invalid/v3/index.json" />
                    <add key="empty" value="{empty.FullName}" />
                    <add key="good" value="{good.FullName}" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="broken">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="empty">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="good">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Failover.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.ContainsKey("Failover.Pkg"), "The package should install after failing over past the unreachable and empty sources.");
            Assert.AreEqual("1.0.0", installed["Failover.Pkg"], "The installed version should match the one served by the good source.");
            Assert.IsTrue(service.GetNuGetPackageDir("Failover.Pkg", "1.0.0").Exists, "The package should be extracted from the failover source on disk.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    #endregion

    #region Authenticated (private) feed Tests

    [TestMethod]
    public async Task InstallPackageAsync_AuthenticatedFeed_WithConfiguredCredentials_InstallsPackage()
    {
        // NUGET_PACKAGES takes precedence over the config's globalPackagesFolder; if it is set the install
        // would target the shared cache and could pass on a pre-populated cache without ever contacting
        // the authenticated feed, so skip to keep the assertion meaningful.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set; it overrides the config's globalPackagesFolder, so the authenticated feed would not be exercised.");
        }

        // Set up the (non-interactive in a test host) credential service exactly as production does.
        NugetSourceProvider.EnsureCredentialService();

        using var feed = new BasicAuthNuGetFeed("winapp-user", "s3cret-token!", ("Auth.Pkg", "1.0.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // The private HTTP feed rejects anonymous requests with 401; the matching credentials live in
            // <packageSourceCredentials>, which is exactly how a user authenticates a company mirror. A
            // regression in credential loading (or in how NugetSourceProvider builds the source) would
            // surface here as a 401 failure instead of a successful install.
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" />
                  </packageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Auth.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.ContainsKey("Auth.Pkg"), "The package served by the authenticated feed should install.");
            Assert.AreEqual("1.0.0", installed["Auth.Pkg"], "The installed version should match the one served by the feed.");
            Assert.IsTrue(service.GetNuGetPackageDir("Auth.Pkg", "1.0.0").Exists, "The package should be extracted from the authenticated feed on disk.");
            Assert.IsTrue(feed.ReceivedAuthenticatedRequest, "The feed should have served at least one request carrying the configured credentials.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_AuthenticatedFeed_WithoutCredentials_FailsNonInteractively()
    {
        NugetSourceProvider.EnsureCredentialService();

        using var feed = new BasicAuthNuGetFeed("winapp-user", "s3cret-token!", ("Auth.Pkg", "1.0.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // Same private feed, but NO <packageSourceCredentials>. The feed answers 401 and, because the
            // credential service is non-interactive (redirected input / CI), NuGet cannot prompt — the
            // install must fail deterministically (surfacing the source failure) rather than hang waiting
            // for console input.
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="{packages.FullName}" />
                  </config>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            // Bound the wait so a hypothetical interactive hang fails the test instead of blocking the run.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Auth.Pkg", "1.0.0", TestTaskContext, cts.Token));

            // The failure must reference the package and the unauthorized source, proving the anonymous
            // request reached the feed and was rejected (not masked as a plain "package not found").
            StringAssert.Contains(ex.Message, "Auth.Pkg", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "private", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_FlatContainerOnlyFeed_ResolvesViaFindPackageById()
    {
        // A private v3 feed can advertise ONLY a PackageBaseAddress (flat container) resource and no
        // registration resource — BasicAuthNuGetFeed is exactly that shape (its service index lists only
        // PackageBaseAddress/3.0.0). Such a feed can still restore packages, so latest-version resolution
        // must work against it too: with no PackageMetadataResource available, NugetService falls back to
        // FindPackageByIdResource.GetAllVersionsAsync (the flat container) instead of skipping the source and
        // reporting "no versions found". This does not download, so no NUGET_PACKAGES guard is needed.
        NugetSourceProvider.EnsureCredentialService();

        // Three versions of one package; the highest stable (2.0.0) must be selected as "latest".
        using var feed = new BasicAuthNuGetFeed(
            "winapp-user",
            "s3cret-token!",
            ("Flat.Pkg", "1.0.0"),
            ("Flat.Pkg", "2.0.0"),
            ("Flat.Pkg", "1.5.0"));
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="private" value="{feed.IndexUrl}" />
                  </packageSources>
                  <packageSourceCredentials>
                    <private>
                      <add key="Username" value="{feed.Username}" />
                      <add key="ClearTextPassword" value="{feed.Password}" />
                    </private>
                  </packageSourceCredentials>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="private">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var latest = await service.GetLatestVersionAsync("Flat.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

            Assert.AreEqual("2.0.0", latest, "Latest resolution against a PackageBaseAddress-only feed must enumerate versions via the flat container and pick the highest.");
            Assert.IsTrue(feed.ReceivedAuthenticatedRequest, "The flat-container feed should have served the versions request carrying the configured credentials.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A minimal in-process NuGet v3 flat-container feed that requires HTTP Basic authentication. It serves
    /// only what <see cref="NugetPackageDownloader"/> needs to download a leaf package — the service index,
    /// the flat-container versions list, and the .nupkg content — answering any unauthenticated request
    /// with <c>401</c> + <c>WWW-Authenticate: Basic</c> so the standard 401→retry-with-credentials flow is
    /// exercised. Bound to <c>127.0.0.1</c> on an ephemeral port; no admin URL ACL is required.
    /// </summary>
    private sealed class BasicAuthNuGetFeed : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveLoop;
        private readonly string _expectedAuthorization;
        private readonly Dictionary<string, byte[]> _nupkgsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> _versionsById = new(StringComparer.OrdinalIgnoreCase);

        public string Username { get; }

        public string Password { get; }

        public string BaseUrl { get; }

        public string IndexUrl => BaseUrl + "v3/index.json";

        // Set once the feed serves a request that carried the expected Basic credentials, so a test can
        // prove authentication actually happened rather than inferring it from a successful install.
        public bool ReceivedAuthenticatedRequest { get; private set; }

        public BasicAuthNuGetFeed(string username, string password, params (string Id, string Version)[] packages)
        {
            Username = username;
            Password = password;
            _expectedAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));

            foreach (var (id, version) in packages)
            {
                var lowerId = id.ToLowerInvariant();
                var lowerVersion = version.ToLowerInvariant();
                _nupkgsByPath[$"{lowerId}/{lowerVersion}"] = BuildNupkgBytes(id, version);
                _versionsById[lowerId] = _versionsById.TryGetValue(lowerId, out var existing)
                    ? [.. existing, version]
                    : [version];
            }

            (_listener, BaseUrl) = StartListener();
            _serveLoop = Task.Run(() => ServeAsync(_cts.Token));
        }

        private static (HttpListener Listener, string BaseUrl) StartListener()
        {
            for (var attempt = 0; ; attempt++)
            {
                // Reserve an ephemeral loopback port, then hand it to HttpListener. The brief gap between
                // closing the probe socket and binding the listener is a benign test-only race; retry a
                // few times if the port is taken in between.
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                var baseUrl = $"http://127.0.0.1:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(baseUrl);
                try
                {
                    listener.Start();
                    return (listener, baseUrl);
                }
                catch (HttpListenerException) when (attempt < 4)
                {
                    listener.Close();
                }
            }
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    // Listener stopped/disposed; end the loop.
                    break;
                }

                try
                {
                    Handle(context);
                }
                catch
                {
                    try
                    {
                        context.Response.Abort();
                    }
                    catch
                    {
                        // Best-effort; the client may have already disconnected.
                    }
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            if (!string.Equals(request.Headers["Authorization"], _expectedAuthorization, StringComparison.Ordinal))
            {
                response.StatusCode = 401;
                response.AddHeader("WWW-Authenticate", "Basic realm=\"winapp-tests\"");
                response.Close();
                return;
            }

            ReceivedAuthenticatedRequest = true;

            var path = request.Url!.AbsolutePath.TrimStart('/');
            var (body, contentType) = Resolve(path);
            if (body is null)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            response.StatusCode = 200;
            response.ContentType = contentType;
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
            response.Close();
        }

        private (byte[]? Body, string ContentType) Resolve(string path)
        {
            if (path == "v3/index.json")
            {
                var json = $$"""{"version":"3.0.0","resources":[{"@id":"{{BaseUrl}}flat/","@type":"PackageBaseAddress/3.0.0"}]}""";
                return (Encoding.UTF8.GetBytes(json), "application/json");
            }

            if (!path.StartsWith("flat/", StringComparison.Ordinal))
            {
                return (null, "application/json");
            }

            var rest = path["flat/".Length..];
            if (rest.EndsWith("/index.json", StringComparison.Ordinal))
            {
                var id = rest[..^"/index.json".Length];
                if (_versionsById.TryGetValue(id, out var versions))
                {
                    var json = "{\"versions\":[" + string.Join(",", versions.Select(v => $"\"{v}\"")) + "]}";
                    return (Encoding.UTF8.GetBytes(json), "application/json");
                }
            }
            else if (rest.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                // flat/{id}/{version}/{id}.{version}.nupkg
                var parts = rest.Split('/');
                if (parts.Length == 3 && _nupkgsByPath.TryGetValue($"{parts[0]}/{parts[1]}", out var bytes))
                {
                    return (bytes, "application/octet-stream");
                }
            }

            return (null, "application/json");
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // Best-effort teardown.
            }

            try
            {
                _serveLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // The loop observes cancellation/disposal; ignore any teardown race.
            }

            _cts.Dispose();
        }
    }

    #endregion
}
