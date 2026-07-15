// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed source/configuration and version-selection tests: <c>&lt;packageSourceMapping&gt;</c>
/// source eligibility, <c>globalPackagesFolder</c> resolution, version normalization, and the
/// highest-across-sources / fail-closed version resolution. Download/install/authentication behavior lives
/// in <see cref="NugetServiceDownloadTests"/>; the shared feed-authoring helpers live in
/// <see cref="NugetFeedTestHelpers"/> (imported via <c>using static</c>).
/// </summary>
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
    // A version that is not a real NuGet version must be rejected before it is turned into a cache path.
    // A traversal segment such as ".." would otherwise resolve to an ancestor of the package folder that a
    // DirectoryInfo.Exists() check treats as an installed package (path traversal); an empty/garbage value
    // would point callers at a folder the NuGet writer never created.
    [DataRow("..")]
    [DataRow("../../etc")]
    [DataRow("not-a-version")]
    [DataRow("")]
    public void GetNuGetPackageDir_InvalidVersion_Throws(string version)
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var service = CreateServiceRootedAt(root);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => service.GetNuGetPackageDir("Some.Package", version));

            StringAssert.Contains(ex.Message, "is not a valid NuGet version", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    // A malformed package id must likewise be rejected before path construction so it cannot escape the
    // packages folder or be treated as an installed package by a later Exists() check.
    [DataRow("..")]
    [DataRow("bad/id")]
    [DataRow("bad\\id")]
    public void GetNuGetPackageDir_InvalidPackageId_Throws(string packageId)
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var service = CreateServiceRootedAt(root);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(
                () => service.GetNuGetPackageDir(packageId, "1.0.0"));

            StringAssert.Contains(ex.Message, "is not a valid NuGet package id", StringComparison.Ordinal);
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
    public async Task GetPackageDependenciesAsync_ExclusiveLowerBoundRange_ResolvesLowestListedSatisfyingVersion()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // Root depends on Child with an EXCLUSIVE lower bound: (1.0.0, 2.0.0]. The declared lower bound
            // 1.0.0 does NOT satisfy the range, so reducing the range to MinVersion would resolve to a
            // disallowed version. The lowest LISTED version that satisfies the range is 1.5.0 (1.0.0 is
            // excluded; 2.0.0 is a higher valid match).
            WriteNupkgToFeed(feed, "Range.Root", "1.0.0", ("Range.Child", "(1.0.0, 2.0.0]"));
            WriteNupkgToFeed(feed, "Range.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Range.Child", "1.5.0");
            WriteNupkgToFeed(feed, "Range.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Range.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Range.Child", out var childVersion), "The dependency must be resolved, not skipped.");
            Assert.AreEqual("1.5.0", childVersion, "The exclusive lower bound (1.0.0) must be excluded and the lowest satisfying listed version selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_UpperBoundOnlyRange_ResolvesInsteadOfSkipping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // Root depends on Child with an UPPER-BOUND-ONLY range: (, 2.0.0]. MinVersion is null for such a
            // range, so the old MinVersion-based logic silently DROPPED the dependency. The lowest listed
            // version that satisfies "<= 2.0.0" is 1.0.0 (3.0.0 is excluded).
            WriteNupkgToFeed(feed, "Upper.Root", "1.0.0", ("Upper.Child", "(, 2.0.0]"));
            WriteNupkgToFeed(feed, "Upper.Child", "1.0.0");
            WriteNupkgToFeed(feed, "Upper.Child", "2.0.0");
            WriteNupkgToFeed(feed, "Upper.Child", "3.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Upper.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Upper.Child", out var childVersion), "An upper-bound-only dependency must be resolved, not silently skipped.");
            Assert.AreEqual("1.0.0", childVersion, "The lowest listed version satisfying '<= 2.0.0' must be selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_InclusiveLowerBoundNotListed_ResolvesNextHigherListedVersion()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            // Root depends on Child with a plain inclusive lower bound (1.0.0 == [1.0.0, )). The declared
            // lower bound 1.0.0 is NOT listed on the feed; only 1.1.0 and 2.0.0 are. NuGet's lowest-applicable
            // resolution must select the lowest LISTED satisfying version (1.1.0) — reducing the range to its
            // MinVersion would instead request the missing 1.0.0 and drop the dependency.
            WriteNupkgToFeed(feed, "Low.Root", "1.0.0", ("Low.Child", "1.0.0"));
            WriteNupkgToFeed(feed, "Low.Child", "1.1.0");
            WriteNupkgToFeed(feed, "Low.Child", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var deps = await service.GetPackageDependenciesAsync("Low.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(deps.TryGetValue("Low.Child", out var childVersion), "The dependency must be resolved against listed versions, not skipped.");
            Assert.AreEqual("1.1.0", childVersion, "The unlisted lower bound (1.0.0) must resolve up to the lowest listed satisfying version (1.1.0).");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_SatisfyingSourceUnreachable_SurfacesErrorInsteadOfSkipping()
    {
        var root = CreateFeedTestDirectory();
        try
        {
            // The local feed serves Root (so its dependency group is read) but lists NO versions of Child.
            // A second eligible source that could have satisfied Child is unreachable (reserved '.invalid'
            // TLD, RFC 6761). Turning that source failure into an empty version list would silently drop the
            // dependency; instead the range resolver must surface the error so the graph caller sees it.
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            WriteNupkgToFeed(feed, "Broken.Root", "1.0.0", ("Broken.Child", "[1.0.0, )"));

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
                async () => await service.GetPackageDependenciesAsync("Broken.Root", "1.0.0", TestContext.CancellationToken));

            // The error must name the dependency and the unreachable source, proving the feed/auth failure
            // was not masked as "no satisfying version" (which would silently omit the dependency).
            StringAssert.Contains(ex.Message, "Broken.Child", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "could not be queried", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "broken", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task GetPackageDependenciesAsync_DifferentConfigRoots_DoNotShareCache()
    {
        // Two independent workspaces resolve the SAME package id/version but their private feeds declare
        // DIFFERENT dependencies. The process-wide dependency cache must be scoped to the effective config
        // (feeds/global folder), so the second lookup returns ITS feed's dependency rather than the first
        // one's cached result.
        var rootA = CreateFeedTestDirectory();
        var rootB = CreateFeedTestDirectory();
        try
        {
            var feedA = new DirectoryInfo(Path.Combine(rootA.FullName, "feed"));
            feedA.Create();
            WriteNupkgToFeed(feedA, "Scoped.Root", "1.0.0", ("Scoped.ChildA", "1.0.0"));
            WriteNupkgToFeed(feedA, "Scoped.ChildA", "1.0.0");
            WriteLocalFeedConfig(rootA, feedA, new DirectoryInfo(Path.Combine(rootA.FullName, "packages")));

            var feedB = new DirectoryInfo(Path.Combine(rootB.FullName, "feed"));
            feedB.Create();
            WriteNupkgToFeed(feedB, "Scoped.Root", "1.0.0", ("Scoped.ChildB", "1.0.0"));
            WriteNupkgToFeed(feedB, "Scoped.ChildB", "1.0.0");
            WriteLocalFeedConfig(rootB, feedB, new DirectoryInfo(Path.Combine(rootB.FullName, "packages")));

            var depsA = await CreateServiceRootedAt(rootA).GetPackageDependenciesAsync("Scoped.Root", "1.0.0", TestContext.CancellationToken);
            var depsB = await CreateServiceRootedAt(rootB).GetPackageDependenciesAsync("Scoped.Root", "1.0.0", TestContext.CancellationToken);

            Assert.IsTrue(depsA.ContainsKey("Scoped.ChildA"), "Workspace A must resolve its own feed's dependency.");
            Assert.IsFalse(depsA.ContainsKey("Scoped.ChildB"), "Workspace A must not see workspace B's dependency.");
            Assert.IsTrue(depsB.ContainsKey("Scoped.ChildB"), "Workspace B must resolve its own feed's dependency, not A's cached result.");
            Assert.IsFalse(depsB.ContainsKey("Scoped.ChildA"), "Workspace B must not receive workspace A's cached dependency.");
        }
        finally
        {
            TryDelete(rootA);
            TryDelete(rootB);
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

    #endregion
}
