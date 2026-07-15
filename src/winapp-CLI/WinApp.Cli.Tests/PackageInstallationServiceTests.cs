// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using static WinApp.Cli.Tests.NugetFeedTestHelpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end tests for <see cref="PackageInstallationService"/> against a real <see cref="NugetService"/>
/// rooted at a local folder feed. Focused on the "already installed" short-circuits: they must gate on the
/// shared <see cref="INugetService.IsPackageInstalled"/> completion-marker predicate, not the bare directory,
/// so a partial cache entry left by an interrupted extraction is re-downloaded instead of being reported as a
/// complete install with a truncated dependency graph.
/// </summary>
[TestClass]
public class PackageInstallationServiceTests : BaseCommandTests
{
    [TestMethod]
    public async Task InstallPackagesAsync_CacheFolderExistsWithoutCompletionMarker_ReDownloadsInsteadOfSkipping()
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

            WriteNupkgToFeed(feed, "Solo.Pkg", "1.0.0");
            WriteLocalFeedConfig(root, feed, packages);

            var nuget = CreateServiceRootedAt(root);
            var installer = new PackageInstallationService(
                _configService,
                nuget,
                GetRequiredService<ILogger<PackageInstallationService>>());

            var projectDir = new DirectoryInfo(Path.Combine(root.FullName, "project"));
            projectDir.Create();

            // Simulate an interrupted extraction: the version folder exists but the ".nupkg.metadata"
            // completion marker (written last by NuGet) was never produced, so the entry is incomplete. The
            // pre-fix code short-circuited on the bare directory here and reported success without ever
            // downloading the package.
            var packageDir = nuget.GetNuGetPackageDir("Solo.Pkg", "1.0.0");
            packageDir.Create();
            var marker = Path.Combine(packageDir.FullName, ".nupkg.metadata");
            Assert.IsFalse(File.Exists(marker), "Precondition: the incomplete cache folder must have no completion marker.");

            var installed = await installer.InstallPackagesAsync(
                projectDir,
                ["Solo.Pkg"],
                TestTaskContext,
                ignoreConfig: true,
                cancellationToken: TestContext.CancellationToken);

            // The marker-less directory must NOT have short-circuited the install: the package should now be
            // fully extracted, evidenced by both the completion marker and the extracted nuspec being present.
            Assert.IsTrue(installed.ContainsKey("Solo.Pkg"), "The package must be reported installed.");
            Assert.IsTrue(File.Exists(marker), "The completion marker must exist, proving the package was actually (re-)downloaded rather than skipped.");
            Assert.IsTrue(File.Exists(Path.Combine(packageDir.FullName, "Solo.Pkg.nuspec")), "The extracted nuspec must exist, proving real extraction into the previously-empty folder.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private PackageInstallationService CreateInstaller(INugetService nuget) =>
        new(_configService, nuget, GetRequiredService<ILogger<PackageInstallationService>>());

    [TestMethod]
    public void InitializeWorkspace_MissingRootDirectory_IsCreated()
    {
        var installer = CreateInstaller(new ConfigurableNugetService());

        var root = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, $"new-workspace-{Guid.NewGuid():N}"));
        Assert.IsFalse(root.Exists, "Precondition: the workspace directory must not exist yet.");

        installer.InitializeWorkspace(root);

        root.Refresh();
        Assert.IsTrue(root.Exists, "InitializeWorkspace must create the missing root directory.");
    }

    [TestMethod]
    public async Task EnsurePackageAsync_AlreadyInstalled_SkipsInstallAndReturnsTrue()
    {
        // Latest resolves to 1.6.0 and that version is already marked installed, so the install must be
        // short-circuited (no InstallPackageAsync call) while still reporting success.
        var nuget = new ConfigurableNugetService { LatestVersion = "1.6.0" };
        nuget.InstalledMarkers.Add("Some.Pkg/1.6.0");
        var installer = CreateInstaller(nuget);

        var ok = await installer.EnsurePackageAsync(_tempDirectory, "Some.Pkg", TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(ok, "An already-installed package must be reported as ensured.");
        CollectionAssert.Contains(nuget.LatestQueried, "Some.Pkg", "The latest version must still be resolved when no version is pinned.");
        Assert.AreEqual(0, nuget.InstallCalls.Count, "A fully-cached package must not be re-installed.");
    }

    [TestMethod]
    public async Task EnsurePackageAsync_WhenInstallThrows_LogsAndReturnsFalse()
    {
        // A pinned version skips the latest lookup; the install itself fails, which EnsurePackageAsync must
        // swallow into a false result (logged) rather than letting the exception escape.
        var nuget = new ConfigurableNugetService { ThrowOnInstall = true };
        var installer = CreateInstaller(nuget);

        var ok = await installer.EnsurePackageAsync(_tempDirectory, "Broken.Pkg", TestTaskContext, version: "2.0.0", cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(ok, "A failed install must be reported as false, not thrown.");
        Assert.AreEqual(1, nuget.InstallCalls.Count, "The install must have been attempted before failing.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_PinnedVersionInConfig_IsUsedWithoutQueryingLatest()
    {
        _configService.Save(new WinappConfig { Packages = [new PackagePin { Name = "Pinned.Pkg", Version = "3.1.4" }] });
        Assert.IsTrue(_configService.Exists(), "Precondition: the pinned config must exist.");

        var nuget = new ConfigurableNugetService { LatestVersion = "9.9.9" };
        var installer = CreateInstaller(nuget);

        var installed = await installer.InstallPackagesAsync(
            _tempDirectory,
            ["Pinned.Pkg"],
            TestTaskContext,
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("3.1.4", installed["Pinned.Pkg"], "The pinned version must win over the latest.");
        Assert.AreEqual(0, nuget.LatestQueried.Count, "A pinned package must not trigger a latest-version query.");
        CollectionAssert.Contains(nuget.InstallCalls.Select(c => c.Version).ToList(), "3.1.4");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_ConfigExistsButPackageNotPinned_FallsBackToLatest()
    {
        // The config exists but pins a DIFFERENT package, so the requested one has no pin and must fall
        // through to the latest-version lookup.
        _configService.Save(new WinappConfig { Packages = [new PackagePin { Name = "Other.Pkg", Version = "1.0.0" }] });

        var nuget = new ConfigurableNugetService { LatestVersion = "7.0.0" };
        var installer = CreateInstaller(nuget);

        var installed = await installer.InstallPackagesAsync(
            _tempDirectory,
            ["Unpinned.Pkg"],
            TestTaskContext,
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("7.0.0", installed["Unpinned.Pkg"], "An unpinned package must fall back to the latest version.");
        CollectionAssert.Contains(nuget.LatestQueried, "Unpinned.Pkg");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_SharedTransitiveDependency_KeepsHighestVersionAcrossRoots()
    {
        // Three roots each pull in a shared transitive id at a different version. The flattened result must
        // keep the HIGHEST seen version: a later-but-higher wins (upgrade), a later-but-lower is ignored.
        var nuget = new ConfigurableNugetService();
        nuget.InstallGraph["Root.A"] = new() { ["Root.A"] = "1.0.0", ["Shared.Dep"] = "1.0.0" };
        nuget.InstallGraph["Root.B"] = new() { ["Root.B"] = "1.0.0", ["Shared.Dep"] = "2.0.0" };
        nuget.InstallGraph["Root.C"] = new() { ["Root.C"] = "1.0.0", ["Shared.Dep"] = "1.5.0" };
        var installer = CreateInstaller(nuget);

        var installed = await installer.InstallPackagesAsync(
            _tempDirectory,
            ["Root.A", "Root.B", "Root.C"],
            TestTaskContext,
            ignoreConfig: true,
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", installed["Shared.Dep"], "The highest version of a shared dependency must be kept (B's 2.0.0 over A's 1.0.0; C's later 1.5.0 must not downgrade it).");
        Assert.AreEqual("1.0.0", installed["Root.A"]);
        Assert.AreEqual("1.0.0", installed["Root.B"]);
        Assert.AreEqual("1.0.0", installed["Root.C"]);
    }

    /// <summary>
    /// A fully in-memory <see cref="INugetService"/> that lets a test script latest-version resolution,
    /// per-package "already installed" state, the flattened install graph a package reports, and an install
    /// failure — so the config/pinning/version-merge/error branches of
    /// <see cref="PackageInstallationService"/> can be exercised deterministically without a feed.
    /// </summary>
    private sealed class ConfigurableNugetService : INugetService
    {
        public string LatestVersion { get; set; } = "1.0.0";

        public List<string> LatestQueried { get; } = [];

        public List<(string Package, string Version)> InstallCalls { get; } = [];

        public bool ThrowOnInstall { get; set; }

        /// <summary>"{id}/{version}" entries reported as fully installed by <see cref="IsPackageInstalled"/>.</summary>
        public HashSet<string> InstalledMarkers { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Optional flattened install result per root package id; defaults to just the root itself.</summary>
        public Dictionary<string, Dictionary<string, string>> InstallGraph { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
        {
            LatestQueried.Add(packageName);
            return Task.FromResult(LatestVersion);
        }

        public Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
        {
            InstallCalls.Add((package, version));
            if (ThrowOnInstall)
            {
                throw new InvalidOperationException($"Simulated install failure for {package} {version}");
            }

            return Task.FromResult(InstallGraph.TryGetValue(package, out var graph)
                ? new Dictionary<string, string>(graph, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [package] = version });
        }

        public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
            => Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public DirectoryInfo GetNuGetGlobalPackagesDir() => throw new NotSupportedException();

        public DirectoryInfo GetNuGetPackageDir(string packageName, string version) => throw new NotSupportedException();

        public bool IsPackageInstalled(string package, string version)
            => InstalledMarkers.Contains($"{package}/{version}");
    }
}
