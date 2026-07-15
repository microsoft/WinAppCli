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
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

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
    public async Task InstallPackageAsync_DiamondDiffersOnlyInLowerBound_DoesNotFalselyFail()
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

            // Diamond graph whose two branches require DiffMin.C at DIFFERENT LOWER BOUNDS but with no upper
            // bound: A needs [1.0.0, ) and B needs [2.0.0, ). Unlike conflicting exact pins, a version (2.0.0)
            // satisfies BOTH ranges, so the graph is NOT unsatisfiable. winapp keeps whichever version its
            // first-resolved branch selected (a documented lowest-first limitation) and must NOT report this
            // common differing-minimum diamond as a conflict — the round-21 satisfaction check must not fire a
            // false positive here.
            WriteNupkgToFeed(feed, "DiffMin.Root", "1.0.0", ("DiffMin.A", "[1.0.0, )"), ("DiffMin.B", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.A", "1.0.0", ("DiffMin.C", "[1.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.B", "1.0.0", ("DiffMin.C", "[2.0.0, )"));
            WriteNupkgToFeed(feed, "DiffMin.C", "1.0.0");
            WriteNupkgToFeed(feed, "DiffMin.C", "2.0.0");

            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            // Must complete without throwing: a satisfiable differing-minimum diamond is not a conflict.
            var installed = await service.InstallPackageAsync("DiffMin.Root", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            Assert.IsTrue(installed.ContainsKey("DiffMin.C"), "The shared dependency must have been installed.");

            // Whichever branch resolved first fixes the version; both 1.0.0 and 2.0.0 are valid outcomes of the
            // documented first-resolution-wins behavior.
            var resolvedC = installed["DiffMin.C"];
            Assert.IsTrue(resolvedC is "1.0.0" or "2.0.0", $"Expected DiffMin.C to resolve to 1.0.0 or 2.0.0 but was '{resolvedC}'.");
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
            var feed = new DirectoryInfo(Path.Combine(root.FullName, "feed"));
            feed.Create();
            var packages = new DirectoryInfo(Path.Combine(root.FullName, "packages"));

            WriteNupkgToFeed(feed, "Partial.Pkg", "1.0.0");
            WriteLocalFeedConfig(root, feed, packages);

            var service = CreateServiceRootedAt(root);

            // Simulate an interrupted extraction: the version folder exists but the ".nupkg.metadata"
            // completion marker (written last by NuGet) was never produced, so the entry is incomplete.
            var packageDir = service.GetNuGetPackageDir("Partial.Pkg", "1.0.0");
            packageDir.Create();
            var marker = Path.Combine(packageDir.FullName, ".nupkg.metadata");
            Assert.IsFalse(File.Exists(marker), "Precondition: the incomplete cache folder must have no completion marker.");

            var installed = await service.InstallPackageAsync("Partial.Pkg", "1.0.0", TestTaskContext, TestContext.CancellationToken);

            // The bare directory must NOT have short-circuited the install: the package should now be fully
            // extracted, evidenced by both the completion marker and the extracted nuspec being present.
            Assert.IsTrue(installed.ContainsKey("Partial.Pkg"), "The package must be reported installed.");
            Assert.IsTrue(File.Exists(marker), "The completion marker must exist, proving the package was actually (re-)downloaded rather than skipped.");
            Assert.IsTrue(File.Exists(Path.Combine(packageDir.FullName, "Partial.Pkg.nuspec")), "The extracted nuspec must exist, proving real extraction into the previously-empty folder.");
        }
        finally
        {
            TryDelete(root);
        }
    }
}
