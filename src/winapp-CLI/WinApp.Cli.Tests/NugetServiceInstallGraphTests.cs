// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// NuGet.Client-backed install-graph integrity tests for <see cref="NugetService.InstallPackageAsync"/>:
/// failing (rather than silently reporting success) when a diamond dependency graph pins the same package to
/// conflicting versions, and re-downloading a package whose global-cache folder exists but is incomplete (no
/// <c>.nupkg.metadata</c> completion marker) instead of trusting the bare directory. Version selection and
/// transitive-download happy paths live in <see cref="NugetServiceDownloadTests"/>; the shared feed-authoring
/// helpers live in <see cref="NugetFeedTestHelpers"/> (imported via <c>using static</c>).
/// </summary>
[TestClass]
public class NugetServiceInstallGraphTests : BaseCommandTests
{
    [TestMethod]
    public async Task InstallPackageAsync_DiamondPinsConflictingVersions_FailsInsteadOfReportingSuccess()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // Diamond graph: Root depends on both A and B; A pins Diamond.C to EXACTLY 1.0.0 while B pins it
            // to EXACTLY 2.0.0. Whichever branch installs first fixes Diamond.C's version; the other branch's
            // exact pin can then never be satisfied. A package-id-only "already installed" short-circuit would
            // skip the second constraint and report a complete install with an invalid graph, so the install
            // must instead fail and name the conflicting package.
            WriteNupkgToFeed(feed, "Diamond.Root", "1.0.0", ("Diamond.A", "[1.0.0, )"), ("Diamond.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Diamond.A", "1.0.0", ("Diamond.C", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Diamond.B", "1.0.0", ("Diamond.C", "[2.0.0]"));
            WriteNupkgToFeed(feed, "Diamond.C", "1.0.0");
            WriteNupkgToFeed(feed, "Diamond.C", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await service.InstallPackageAsync("Diamond.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken));

            // The failure must name the conflicting package and describe the unsatisfiable constraint rather
            // than being swallowed as a successful install.
            StringAssert.Contains(ex.Message, "Diamond.C", StringComparison.Ordinal);
            StringAssert.Contains(ex.Message, "cannot be resolved to a single version", StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_DiamondWithHigherLowerBound_NeverSucceedsWithUnsatisfyingVersion()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            // A needs DiffMin.C [1.0.0, ), B needs [2.0.0, ), and both 1.0.0 and 2.0.0 exist — so only 2.0.0
            // satisfies BOTH branches. winapp resolves as it installs, so whichever branch it reaches first
            // fixes the version; the invariant under test is that it must never report SUCCESS having selected
            // a version that does not satisfy every branch. It previously did exactly that: an intersection
            // check saw that *some* version could satisfy both and kept 1.0.0, leaving the consumer without
            // the APIs B declared it needed.
            WriteNupkgToFeed(feed, "DiffMin.Root", "1.0.0", ("DiffMin.A", "[1.0.0, )"), ("DiffMin.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.A", "1.0.0", ("DiffMin.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.B", "1.0.0", ("DiffMin.C", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.C", "1.0.0");
            WriteNupkgToFeed(feed, "DiffMin.C", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            string? resolvedC = null;
            InvalidOperationException? failure = null;
            try
            {
                var installed = await service.InstallPackageAsync("DiffMin.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
                resolvedC = installed.TryGetValue("DiffMin.C", out var c) ? c : null;
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            // Asserted independently of which branch resolves first, so the test cannot pass for the wrong
            // reason if traversal order changes.
            if (failure is not null)
            {
                StringAssert.Contains(failure.Message, "DiffMin.C", StringComparison.Ordinal);
                StringAssert.Contains(failure.Message, "cannot be resolved to a single version", StringComparison.Ordinal);
            }
            else
            {
                Assert.AreEqual(
                    "2.0.0",
                    resolvedC,
                    "Reporting success is only valid when the selected version satisfies every branch; 2.0.0 is the only such version here.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// Upgrading a shared dependency must retract the requirements of the version it replaced. This graph is
    /// satisfiable (C 2.0.0 with D 2.0.0), but resolution reaches C through the `>= 1.0.0` branch first, so C
    /// 1.0.0 and its pinned D [1.0.0] are selected before the `>= 2.0.0` branch forces C up to 2.0.0. C 1.0.0
    /// is then no longer part of the graph, so its D [1.0.0] pin must stop counting. While constraints were
    /// accumulated in an append-only list, that stale pin was combined with C 2.0.0's D [2.0.0] and the install
    /// failed with a conflict that does not exist in the resolved graph.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_UpgradeChangesPinnedTransitive_DoesNotFailOnReplacedVersionsConstraint()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            WriteNupkgToFeed(feed, "Retract.Root", "1.0.0", ("Retract.A", "[1.0.0, )"), ("Retract.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Retract.A", "1.0.0", ("Retract.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Retract.B", "1.0.0", ("Retract.C", "[2.0.0, )"));
            // Each version of C pins a DIFFERENT exact version of D, so keeping the replaced version's pin
            // makes the two mutually unsatisfiable.
            WriteNupkgToFeed(feed, "Retract.C", "1.0.0", ("Retract.D", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Retract.C", "2.0.0", ("Retract.D", "[2.0.0]"));
            WriteNupkgToFeed(feed, "Retract.D", "1.0.0");
            WriteNupkgToFeed(feed, "Retract.D", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            Dictionary<string, string> installed;
            try
            {
                installed = await service.InstallPackageAsync("Retract.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                // Asserted independently of which branch resolves first: this graph has a valid solution, so
                // failing is wrong no matter what order the walk happens to take.
                Assert.Fail($"Graph is satisfiable (C 2.0.0 + D 2.0.0) but install reported a conflict: {ex.Message}");
                return;
            }

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Retract.C"), "C must end up at the only version satisfying both branches.");
            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Retract.D"), "D must follow the pin declared by the version of C that is actually selected.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    /// <summary>
    /// A package pulled in only by a version that a later upgrade replaced must not survive in the resolved
    /// graph. `WorkspaceSetupService` copies headers, libs, WinMDs and runtimes for every entry returned by
    /// the install, so an orphan left behind here publishes assets from a package the resolution rejected.
    /// Here C 1.0.0 requires Orphan.Removed, C 2.0.0 requires nothing, and the `>= 2.0.0` branch forces C up.
    /// </summary>
    [TestMethod]
    public async Task InstallPackageAsync_UpgradeDropsDependency_DoesNotReturnTheOrphanedDescendant()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            WriteNupkgToFeed(feed, "Orphan.Root", "1.0.0", ("Orphan.A", "[1.0.0, )"), ("Orphan.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Orphan.A", "1.0.0", ("Orphan.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "Orphan.B", "1.0.0", ("Orphan.C", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "Orphan.C", "1.0.0", ("Orphan.Removed", "[1.0.0]"));
            WriteNupkgToFeed(feed, "Orphan.C", "2.0.0");
            WriteNupkgToFeed(feed, "Orphan.Removed", "1.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            var installed = await service.InstallPackageAsync("Orphan.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.AreEqual("2.0.0", installed.GetValueOrDefault("Orphan.C"), "C must end up at the only version satisfying both branches.");
            // Packages still reachable from the root must be untouched by pruning.
            Assert.AreEqual("1.0.0", installed.GetValueOrDefault("Orphan.A"));
            Assert.AreEqual("1.0.0", installed.GetValueOrDefault("Orphan.B"));
            Assert.IsFalse(
                installed.ContainsKey("Orphan.Removed"),
                "Orphan.Removed is required only by the replaced C 1.0.0, so it is not part of the resolved graph and its assets must not be copied.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task InstallPackageAsync_CacheFolderExistsWithoutCompletionMarker_ReDownloadsInsteadOfTrustingIt()
    {
        // NUGET_PACKAGES takes precedence over the globalPackagesFolder written by WriteLocalFeedConfig.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        {
            Assert.Inconclusive("NUGET_PACKAGES is set in the environment; it overrides the config's globalPackagesFolder, so the local feed would not be exercised.");
        }

        var root = CreateFeedTestDirectory();
        try
        {
            var feed = new DirectoryInfo(Path.Join(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Join(root.FullName, "packages"));

            WriteNupkgToFeed(feed, "Partial.Pkg", "1.0.0");
            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            // Simulate an interrupted extraction: the version folder exists but the ".nupkg.metadata"
            // completion marker (written last by NuGet) was never produced, so the entry is incomplete.
            var packageDir = service.GetNuGetPackageDir("Partial.Pkg", "1.0.0");
            packageDir.Create();
            var marker = Path.Join(packageDir.FullName, ".nupkg.metadata");
            Assert.IsFalse(File.Exists(marker), "Precondition: the incomplete cache folder must have no completion marker.");

            var installed = await service.InstallPackageAsync("Partial.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            // The bare directory must NOT have short-circuited the install: the package should now be fully
            // extracted, evidenced by both the completion marker and the extracted nuspec being present.
            Assert.IsTrue(installed.ContainsKey("Partial.Pkg"), "The package must be reported installed.");
            Assert.IsTrue(File.Exists(marker), "The completion marker must exist, proving the package was actually (re-)downloaded rather than skipped.");
            Assert.IsTrue(File.Exists(Path.Join(packageDir.FullName, "Partial.Pkg.nuspec")), "The extracted nuspec must exist, proving real extraction into the previously-empty folder.");
        }
        finally
        {
            TryDelete(root);
        }
    }
}
