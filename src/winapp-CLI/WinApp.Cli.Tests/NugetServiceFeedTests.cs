// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class NugetServiceFeedTests : BaseCommandTests
{
    #region Private nuget.config (isolated feed) Tests

    // These tests exercise the NuGet.Client-backed behavior directly against a temporary
    // nuget.config with local folder sources (no network). They cover the private-feed scenarios the
    // migration enables: <packageSourceMapping> source selection and globalPackagesFolder resolution.
    // The temp root lives under %TEMP% (outside the repo) so the repo's own nuget.config is not in
    // its configuration hierarchy; <clear /> removes any inherited machine/user sources.

    private static readonly string[] AlphaAndBeta = ["alpha", "beta"];
    private static readonly string[] AlphaOnly = ["alpha"];
    private static readonly string[] BetaOnly = ["beta"];

    /// <summary>
    /// <see cref="IWinappDirectoryService"/> whose global directory is the real default
    /// (<c>%USERPROFILE%\.winapp</c>), so <see cref="NugetService"/> does NOT treat it as a test
    /// override and instead resolves the global packages folder from the supplied nuget.config
    /// (exercising <c>SettingsUtility.GetGlobalPackagesFolder</c>).
    /// </summary>
    private sealed class DefaultWinappDirectoryService : IWinappDirectoryService
    {
        public DirectoryInfo GetGlobalWinappDirectory() =>
            new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".winapp"));

        public DirectoryInfo GetLocalWinappDirectory(DirectoryInfo? baseDirectory = null) =>
            new(Path.Combine((baseDirectory ?? new DirectoryInfo(Directory.GetCurrentDirectory())).FullName, ".winapp"));

        public void SetCacheDirectoryForTesting(DirectoryInfo? cacheDirectory)
        {
        }
    }

    private static NugetSourceProvider CreateSourceProviderRootedAt(DirectoryInfo root) =>
        new(new CurrentDirectoryProvider(root.FullName));

    private static NugetService CreateServiceRootedAt(DirectoryInfo root)
    {
        var sourceProvider = CreateSourceProviderRootedAt(root);
        return new NugetService(new DefaultWinappDirectoryService(), sourceProvider, new NugetPackageDownloader(sourceProvider));
    }

    private static DirectoryInfo CreateFeedTestDirectory()
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"NugetServiceFeedTests_{Guid.NewGuid():N}"));
        dir.Create();
        return dir;
    }

    private static void WriteNuGetConfig(DirectoryInfo dir, string contents) =>
        File.WriteAllText(Path.Combine(dir.FullName, "nuget.config"), contents);

    private static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            dir.Refresh();
            if (dir.Exists)
            {
                dir.Delete(true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [TestMethod]
    public void GetRepositoriesForPackage_NoPackageSourceMapping_ReturnsAllConfiguredSources()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                    <add key="beta" value="beta-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                  </packageSourceMapping>
                </configuration>
                """);

            var provider = CreateSourceProviderRootedAt(root);

            var sources = provider.GetRepositoriesForPackage("Any.Package")
                .Select(r => r.PackageSource.Name)
                .ToList();

            CollectionAssert.AreEquivalent(
                AlphaAndBeta,
                sources,
                "With no packageSourceMapping in effect, every configured source is eligible.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetRepositoriesForPackage_PackageSourceMapping_SelectsOnlyMappedSource()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                    <add key="beta" value="beta-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="alpha">
                      <package pattern="Contoso.*" />
                    </packageSource>
                    <packageSource key="beta">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var provider = CreateSourceProviderRootedAt(root);

            // Contoso.* is mapped exclusively to 'alpha'.
            var mapped = provider.GetRepositoriesForPackage("Contoso.Widget")
                .Select(r => r.PackageSource.Name)
                .ToList();
            CollectionAssert.AreEqual(AlphaOnly, mapped, "Contoso.* must resolve to the mapped source only.");

            // Everything else falls back to the '*' mapping on 'beta'.
            var fallback = provider.GetRepositoriesForPackage("Fabrikam.Thing")
                .Select(r => r.PackageSource.Name)
                .ToList();
            CollectionAssert.AreEqual(BetaOnly, fallback, "Unmatched packages use the '*' mapping.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetRepositoriesForPackage_PackageSourceMapping_UnmappedPackage_ReturnsEmpty()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // Mapping is enabled but only maps Contoso.* -> alpha; there is no '*' fallback, so a
            // package matching no pattern must resolve to zero sources rather than silently falling
            // back to an unmapped feed (the dependency-confusion behavior the reviewer flagged).
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                    <add key="beta" value="beta-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="alpha">
                      <package pattern="Contoso.*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var provider = CreateSourceProviderRootedAt(root);

            var sources = provider.GetRepositoriesForPackage("Unmapped.Package").ToList();

            Assert.IsEmpty(sources, "A package matching no packageSourceMapping pattern must resolve to no sources.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetNuGetGlobalPackagesDir_HonorsGlobalPackagesFolderFromConfig()
    {
        // GetGlobalPackagesFolder gives NUGET_PACKAGES precedence over the config value; skip if the
        // host has it set so the assertion stays meaningful.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="custom-packages" />
                  </config>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var expected = new DirectoryInfo(Path.Combine(root.FullName, "custom-packages")).FullName;
            var actual = service.GetNuGetGlobalPackagesDir().FullName;

            Assert.AreEqual(
                expected.TrimEnd(Path.DirectorySeparatorChar),
                actual.TrimEnd(Path.DirectorySeparatorChar),
                "globalPackagesFolder from nuget.config should be honored (resolved relative to the config).");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void GetNuGetPackageDir_NormalizesIdAndVersionToOnDiskLayout()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <config>
                    <add key="globalPackagesFolder" value="custom-packages" />
                  </config>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            // "1.0" is a valid NuGet version that NuGet stores under its normalized "1.0.0" folder, and
            // the package-id folder is lowercased. GetNuGetPackageDir must match that on-disk layout so
            // callers find the extracted package regardless of how the version string was expressed.
            var dir = service.GetNuGetPackageDir("Some.Package", "1.0");

            Assert.AreEqual("1.0.0", dir.Name, "Version should be normalized to the on-disk folder name.");
            Assert.AreEqual("some.package", dir.Parent?.Name, "Package id folder should be lowercased.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    // Shorthand numeric versions expand to NuGet's canonical 3-part form so the stored/returned value
    // matches the on-disk global-packages folder layout that downstream cache-path builders concatenate.
    [DataRow("1.0", "1.0.0")]
    [DataRow("2", "2.0.0")]
    [DataRow("1.2.3", "1.2.3")]
    // Trailing-zero revision is dropped by normalization (1.2.3.0 -> 1.2.3), matching NuGet on disk.
    [DataRow("1.2.3.0", "1.2.3")]
    // Prerelease and metadata are preserved (build metadata is stripped by NuGet normalization).
    [DataRow("1.0.0-preview.1", "1.0.0-preview.1")]
    [DataRow("1.0.0+build5", "1.0.0")]
    // Non-parseable input is returned unchanged rather than throwing.
    [DataRow("not-a-version", "not-a-version")]
    public void NormalizeVersion_ReturnsCanonicalOnDiskForm(string input, string expected)
    {
        Assert.AreEqual(expected, NugetService.NormalizeVersion(input), $"NormalizeVersion(\"{input}\")");
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // One eligible source so version enumeration actually enters the per-source loop, where the
            // cancellation token is observed — rather than short-circuiting to "no versions found".
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // Cancellation must surface as OperationCanceledException, not be masked as a "no versions" error.
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                async () => await service.GetLatestVersionAsync("Any.Package", SdkInstallMode.Stable, cts.Token));
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Builds a minimal but valid .nupkg (with an optional dependency group) in memory, so tests can serve
    /// it from a local folder feed or an in-process HTTP feed without network access.
    /// </summary>
    private static byte[] BuildNupkgBytes(string id, string version, params (string Id, string Version)[] dependencies)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Description = $"{id} test package",
        };
        builder.Authors.Add("winapp-tests");

        if (dependencies.Length > 0)
        {
            builder.DependencyGroups.Add(new PackageDependencyGroup(
                NuGetFramework.Parse("net10.0"),
                [.. dependencies.Select(d => new PackageDependency(d.Id, VersionRange.Parse(d.Version)))]));
        }

        // A .nupkg must contain at least one file; add a trivial lib file from a temp source.
        var contentFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(contentFile, "test");
        try
        {
            builder.Files.Add(new PhysicalPackageFile { SourcePath = contentFile, TargetPath = $"lib/net10.0/{id}.txt" });

            using var stream = new MemoryStream();
            builder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            File.Delete(contentFile);
        }
    }

    /// <summary>
    /// Authors a minimal but valid .nupkg (with an optional dependency group) into a flat local feed
    /// folder, so tests can exercise the real download/extract/nuspec/recursive-dependency paths without
    /// network access.
    /// </summary>
    private static void WriteNupkgToFeed(DirectoryInfo feedDir, string id, string version, params (string Id, string Version)[] dependencies) =>
        File.WriteAllBytes(
            Path.Combine(feedDir.FullName, $"{id}.{version}.nupkg"),
            BuildNupkgBytes(id, version, dependencies));

    private static void WriteLocalFeedConfig(DirectoryInfo root, DirectoryInfo feed, DirectoryInfo packages) =>
        WriteNuGetConfig(root, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{packages.FullName}" />
              </config>
              <packageSources>
                <clear />
                <add key="local" value="{feed.FullName}" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="local">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

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
    public async Task GetPackageDependenciesAsync_PackageMatchesNoMappingPattern_ReportsMissingMapping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // Mapping is enabled and only maps Contoso.* -> alpha; the requested package matches no
            // pattern, so no source is eligible. The error must say the package isn't mapped (not that a
            // source is disabled).
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="alpha">
                      <package pattern="Contoso.*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Unmapped.Package", "1.0.0", TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "no <packageSourceMapping> pattern maps 'Unmapped.Package'", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_PackageMappedToDisabledSource_ReportsUnusableMappedSource()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // The package IS mapped, but to a source name that no enabled <packageSources> entry provides
            // (disabled/misspelled/missing). The eligible set is empty for a different reason than "no
            // mapping", so the error must point at fixing the mapped source, not at adding a mapping.
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="phantom">
                      <package pattern="Winapp.*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Winapp.Thing", "1.0.0", TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "mapped to source(s) [phantom]", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "not enabled/configured", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_PackageMatchesNoMappingPattern_ReportsMissingMapping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // The version-resolution path (init/update) must give the same actionable mapping guidance as
            // the download and dependency paths when no source is eligible, instead of a generic
            // "verify package ID / sources / credentials" message.
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="alpha">
                      <package pattern="Contoso.*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetLatestVersionAsync("Unmapped.Package", SdkInstallMode.Stable, TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "no <packageSourceMapping> pattern maps 'Unmapped.Package'", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_InvalidVersion_ReportsActionableError()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // An unparseable version must fail with a message that names the package and the offending
            // value, not a raw NuGetVersion.Parse ArgumentException. The version is validated before any
            // source is contacted, so no feed is required for this to throw.
            WriteNuGetConfig(root, """
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="alpha" value="alpha-feed" />
                  </packageSources>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetPackageDependenciesAsync("Winapp.TestA", "not-a-version", TestContext.CancellationToken));

            StringAssert.Contains(ex.Message, "'not-a-version' is not a valid NuGet version for package 'Winapp.TestA'", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_MultipleEligibleSources_ReturnsHighestAcrossSources()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // Two local feeds, both eligible (each mapped to '*'). The higher version lives ONLY in the
            // second-listed source, so a bug that queried just the first source (or returned only its
            // max) would yield 1.0.0 — this pins the "highest across ALL eligible sources" contract that
            // the single-source live tests never exercise.
            var low = new DirectoryInfo(Path.Combine(root.FullName, "low"));
            low.Create();
            var high = new DirectoryInfo(Path.Combine(root.FullName, "high"));
            high.Create();

            WriteNupkgToFeed(low, "Multi.Pkg", "1.0.0");
            WriteNupkgToFeed(high, "Multi.Pkg", "2.0.0");

            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="low" value="{low.FullName}" />
                    <add key="high" value="{high.FullName}" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="low">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="high">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var latest = await service.GetLatestVersionAsync("Multi.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken);

            Assert.AreEqual(
                "2.0.0",
                latest,
                "Latest must be the highest version merged across every eligible source, not just the first source's.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetLatestVersionAsync_OneEligibleSourceFails_FailsClosed()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // One good local feed plus one unreachable source, both eligible. Because "latest" is a MAX
            // across sources, a source that cannot be queried could hide a newer version, so the resolver
            // must fail closed (throw and name the failed source) rather than return the reachable feed's
            // partial result. The broken source uses the reserved '.invalid' TLD (RFC 6761), which never
            // resolves, keeping this deterministic and offline.
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            WriteNupkgToFeed(feed, "FailClosed.Pkg", "1.0.0");

            WriteNuGetConfig(root, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feed.FullName}" />
                    <add key="broken" value="https://nuget.invalid/v3/index.json" />
                  </packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="local">
                      <package pattern="*" />
                    </packageSource>
                    <packageSource key="broken">
                      <package pattern="*" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.GetLatestVersionAsync("FailClosed.Pkg", SdkInstallMode.Stable, TestContext.CancellationToken));

            // The error must name the unreachable source and explain it could not be queried, proving the
            // resolver did not silently return 1.0.0 from the reachable feed.
            StringAssert.Contains(ex.Message, "could not be queried", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "broken", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

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
