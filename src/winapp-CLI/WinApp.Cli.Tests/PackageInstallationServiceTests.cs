// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Behavior tests for <see cref="PackageInstallationService"/> driven entirely through a
/// controllable in-memory <see cref="INugetService"/> fake, so no network or real NuGet cache is
/// touched. Covers pinned-vs-latest version resolution, the "already present" transitive top-up
/// paths (install-missing / already-on-disk / higher-version merge / KeyNotFound), and the
/// <see cref="PackageInstallationService.EnsurePackageAsync"/> success/failure paths.
/// </summary>
[TestClass]
public sealed class PackageInstallationServiceTests : BaseCommandTests
{
    private ControllableNugetService _nuget = null!;
    private PackageInstallationService _service = null!;
    private DirectoryInfo _cacheRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _cacheRoot = _tempDirectory.CreateSubdirectory("nugetcache");
        _nuget = new ControllableNugetService(_cacheRoot);
        _service = new PackageInstallationService(
            _configService,
            _nuget,
            GetRequiredService<ILogger<PackageInstallationService>>());
    }

    private void SaveConfig(params (string name, string version)[] pins)
    {
        var cfg = new WinappConfig();
        foreach (var (name, version) in pins)
        {
            cfg.SetVersion(name, version);
        }
        _configService.Save(cfg);
    }

    // ───────────────────────────── InitializeWorkspace ─────────────────────────────

    [TestMethod]
    public void InitializeWorkspace_MissingDirectory_IsCreated()
    {
        var target = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "ws", Guid.NewGuid().ToString("N")));
        Assert.IsFalse(target.Exists);

        _service.InitializeWorkspace(target);

        target.Refresh();
        Assert.IsTrue(target.Exists, "InitializeWorkspace must create the root directory when it is missing.");
    }

    [TestMethod]
    public void InitializeWorkspace_ExistingDirectory_LeftIntact()
    {
        var target = _tempDirectory.CreateSubdirectory("already-here");
        var marker = Path.Combine(target.FullName, "keep.txt");
        File.WriteAllText(marker, "x");

        _service.InitializeWorkspace(target);

        Assert.IsTrue(File.Exists(marker), "An existing workspace directory must not be recreated/cleared.");
    }

    // ───────────────────────────── InstallPackagesAsync: version resolution ─────────────────────────────

    [TestMethod]
    public async Task InstallPackagesAsync_NoConfig_InstallsLatestVersion()
    {
        _nuget.LatestVersions["Contoso.Pkg"] = "3.1.0";

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Contoso.Pkg"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("3.1.0", result["Contoso.Pkg"]);
        CollectionAssert.Contains(_nuget.LatestQueries, "Contoso.Pkg", "Latest version must be queried when no pin exists.");
        Assert.AreEqual(1, _nuget.InstallCalls.Count);
        Assert.AreEqual(("Contoso.Pkg", "3.1.0"), _nuget.InstallCalls[0]);
    }

    [TestMethod]
    public async Task InstallPackagesAsync_PinnedVersion_UsesPinAndSkipsLatestLookup()
    {
        SaveConfig(("Contoso.Pkg", "2.0.5"));
        _nuget.LatestVersions["Contoso.Pkg"] = "9.9.9"; // must NOT be chosen

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Contoso.Pkg"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("2.0.5", result["Contoso.Pkg"], "The pinned version must win over latest.");
        Assert.AreEqual(("Contoso.Pkg", "2.0.5"), _nuget.InstallCalls.Single());
        CollectionAssert.DoesNotContain(_nuget.LatestQueries, "Contoso.Pkg", "A pinned package must not trigger a latest-version lookup.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_ConfigExistsButPackageNotPinned_FallsBackToLatest()
    {
        SaveConfig(("Some.Other", "1.0.0")); // config exists, but not for the requested package
        _nuget.LatestVersions["Contoso.Pkg"] = "4.2.0";

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Contoso.Pkg"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("4.2.0", result["Contoso.Pkg"]);
        CollectionAssert.Contains(_nuget.LatestQueries, "Contoso.Pkg", "An unpinned package in an existing config must still resolve latest.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_IgnoreConfig_SkipsPinnedVersion()
    {
        SaveConfig(("Contoso.Pkg", "2.0.5"));
        _nuget.LatestVersions["Contoso.Pkg"] = "7.0.0";

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Contoso.Pkg"], TestTaskContext, ignoreConfig: true, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("7.0.0", result["Contoso.Pkg"], "ignoreConfig=true must bypass the pinned version and install latest.");
        CollectionAssert.Contains(_nuget.LatestQueries, "Contoso.Pkg");
    }

    // ───────────────────────────── InstallPackagesAsync: fresh install merge ─────────────────────────────

    [TestMethod]
    public async Task InstallPackagesAsync_FreshInstall_MergesHigherTransitiveVersions()
    {
        // Package A brings Shared 1.0.0 and Keep 5.0.0; package B brings Shared 2.0.0 (higher -> wins)
        // and Keep 4.0.0 (lower -> must NOT overwrite the already-recorded 5.0.0).
        _nuget.LatestVersions["A"] = "1.0.0";
        _nuget.LatestVersions["B"] = "1.0.0";
        _nuget.InstallReturns["A/1.0.0"] = new() { ["A"] = "1.0.0", ["Shared"] = "1.0.0", ["Keep"] = "5.0.0" };
        _nuget.InstallReturns["B/1.0.0"] = new() { ["B"] = "1.0.0", ["Shared"] = "2.0.0", ["Keep"] = "4.0.0" };

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["A", "B"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", result["Shared"], "A higher transitive version must overwrite a lower one.");
        Assert.AreEqual("5.0.0", result["Keep"], "A lower transitive version must NOT overwrite a higher recorded one.");
        Assert.AreEqual("1.0.0", result["A"]);
        Assert.AreEqual("1.0.0", result["B"]);
    }

    // ───────────────────────────── InstallPackagesAsync: already-present transitive top-up ─────────────────────────────

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_InstallsMissingTransitiveDependency()
    {
        _nuget.LatestVersions["Root"] = "1.0.0";
        _nuget.MarkPresent("Root", "1.0.0");                       // main package already on disk
        _nuget.DependencyMap["Root/1.0.0"] = new() { ["Dep"] = "2.0.0" };
        // Dep is NOT present on disk -> must be installed.

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Root"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("1.0.0", result["Root"], "The already-present main package must still be recorded.");
        Assert.AreEqual("2.0.0", result["Dep"], "A missing transitive dependency must be installed and recorded.");
        Assert.AreEqual(("Dep", "2.0.0"), _nuget.InstallCalls.Single(), "Only the missing dependency should be installed.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_TransitiveDependencyAlreadyOnDisk_NotReinstalled()
    {
        _nuget.LatestVersions["Root"] = "1.0.0";
        _nuget.MarkPresent("Root", "1.0.0");
        _nuget.DependencyMap["Root/1.0.0"] = new() { ["Dep"] = "2.0.0" };
        _nuget.MarkPresent("Dep", "2.0.0");                        // dependency already on disk

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Root"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", result["Dep"], "A present transitive dependency must be recorded without reinstalling.");
        Assert.IsEmpty(_nuget.InstallCalls, "Nothing should be installed when the dependency is already cached.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_MergesHigherAndKeepsExistingHigherVersions()
    {
        // First package (fresh) records Low=3.0.0 and Mid=1.0.0. Second package is already present and
        // declares on-disk deps Low=2.0.0 (lower -> keep 3.0.0) and Mid=2.0.0 (higher -> upgrade).
        _nuget.LatestVersions["First"] = "1.0.0";
        _nuget.LatestVersions["Second"] = "1.0.0";
        _nuget.InstallReturns["First/1.0.0"] = new() { ["First"] = "1.0.0", ["Low"] = "3.0.0", ["Mid"] = "1.0.0" };
        _nuget.MarkPresent("Second", "1.0.0");
        _nuget.DependencyMap["Second/1.0.0"] = new() { ["Low"] = "2.0.0", ["Mid"] = "2.0.0" };
        _nuget.MarkPresent("Low", "2.0.0");
        _nuget.MarkPresent("Mid", "2.0.0");

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["First", "Second"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("3.0.0", result["Low"], "Existing higher version must be preserved against a lower present dep.");
        Assert.AreEqual("2.0.0", result["Mid"], "A higher present dep version must upgrade the recorded version.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_InstalledDependencyMergesHigherVersion()
    {
        // Fresh First records X=1.0.0 and Y=5.0.0. Present Second must install missing Dep, whose install
        // return also carries X=3.0.0 (higher -> upgrade) and Y=4.0.0 (lower -> keep 5.0.0).
        _nuget.LatestVersions["First"] = "1.0.0";
        _nuget.LatestVersions["Second"] = "1.0.0";
        _nuget.InstallReturns["First/1.0.0"] = new() { ["First"] = "1.0.0", ["X"] = "1.0.0", ["Y"] = "5.0.0" };
        _nuget.MarkPresent("Second", "1.0.0");
        _nuget.DependencyMap["Second/1.0.0"] = new() { ["Dep"] = "2.0.0" };
        // Dep missing -> installed; its transitive closure bumps X and reports a lower Y.
        _nuget.InstallReturns["Dep/2.0.0"] = new() { ["Dep"] = "2.0.0", ["X"] = "3.0.0", ["Y"] = "4.0.0" };

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["First", "Second"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual("2.0.0", result["Dep"]);
        Assert.AreEqual("3.0.0", result["X"], "A newly installed dependency's higher version must win.");
        Assert.AreEqual("5.0.0", result["Y"], "A newly installed dependency's lower version must not overwrite a higher one.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_DependencyWithUnparsableVersion_IsSkipped()
    {
        _nuget.LatestVersions["Root"] = "1.0.0";
        _nuget.MarkPresent("Root", "1.0.0");
        _nuget.DependencyMap["Root/1.0.0"] = new() { ["BadDep"] = "   " }; // ParseMinimumVersion -> empty

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Root"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(1, result, "A dependency whose version cannot be parsed must be skipped.");
        Assert.IsFalse(result.ContainsKey("BadDep"));
        Assert.IsEmpty(_nuget.InstallCalls);
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_DependencyLookupThrowsKeyNotFound_ContinuesWithMainPackage()
    {
        _nuget.LatestVersions["Root"] = "1.0.0";
        _nuget.MarkPresent("Root", "1.0.0");
        _nuget.ThrowKeyNotFoundFor.Add("Root"); // GetPackageDependenciesAsync throws KeyNotFoundException

        var result = await _service.InstallPackagesAsync(
            _tempDirectory, ["Root"], TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.HasCount(1, result, "A KeyNotFound during dependency resolution must be swallowed, leaving the main package.");
        Assert.AreEqual("1.0.0", result["Root"]);
    }

    // ───────────────────────────── EnsurePackageAsync ─────────────────────────────

    [TestMethod]
    public async Task EnsurePackageAsync_NewPackage_CreatesWorkspaceInstallsAndReturnsTrue()
    {
        var root = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "ensure-new", Guid.NewGuid().ToString("N")));
        _nuget.LatestVersions["Contoso.Pkg"] = "5.0.0";

        var ok = await _service.EnsurePackageAsync(
            root, "Contoso.Pkg", TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(ok);
        root.Refresh();
        Assert.IsTrue(root.Exists, "EnsurePackageAsync must initialize the workspace directory.");
        Assert.AreEqual(("Contoso.Pkg", "5.0.0"), _nuget.InstallCalls.Single());
    }

    [TestMethod]
    public async Task EnsurePackageAsync_ExplicitVersionAlreadyPresent_SkipsInstallReturnsTrue()
    {
        _nuget.MarkPresent("Contoso.Pkg", "1.5.0");

        var ok = await _service.EnsurePackageAsync(
            _tempDirectory, "Contoso.Pkg", TestTaskContext, version: "1.5.0", cancellationToken: TestContext.CancellationToken);

        Assert.IsTrue(ok);
        Assert.IsEmpty(_nuget.InstallCalls, "A package already present at the requested version must not be reinstalled.");
        Assert.IsEmpty(_nuget.LatestQueries, "An explicit version must not trigger a latest-version lookup.");
    }

    [TestMethod]
    public async Task EnsurePackageAsync_InstallFailure_ReturnsFalseAndLogsError()
    {
        _nuget.ThrowLatestFor.Add("Broken.Pkg"); // latest lookup throws -> install fails

        var ok = await _service.EnsurePackageAsync(
            _tempDirectory, "Broken.Pkg", TestTaskContext, cancellationToken: TestContext.CancellationToken);

        Assert.IsFalse(ok, "A failure during installation must be reported as false, not thrown.");
        CollectionAssert.Contains(_nuget.LatestQueries, "Broken.Pkg", "The failing operation must have actually been attempted.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "Broken.Pkg", StringComparison.Ordinal);
    }
}

/// <summary>
/// A fully controllable in-memory <see cref="INugetService"/> for driving
/// <see cref="PackageInstallationService"/> branch-by-branch without any network or real cache.
/// Package presence is backed by real directories under a temp cache root so that the
/// <c>DirectoryInfo.Exists</c> checks in the product code behave exactly as in production.
/// </summary>
internal sealed class ControllableNugetService(DirectoryInfo cacheRoot) : INugetService
{
    public Dictionary<string, string> LatestVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string FallbackLatest { get; set; } = "9.9.9";
    public HashSet<string> ThrowLatestFor { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> LatestQueries { get; } = [];
    public List<SdkInstallMode> LatestModes { get; } = [];

    public Dictionary<string, Dictionary<string, string>> DependencyMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ThrowKeyNotFoundFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Dictionary<string, string>> InstallReturns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Id, string Version)> InstallCalls { get; } = [];

    private static string Key(string id, string version) => $"{id}/{version}";

    public Task<string> GetLatestVersionAsync(string packageName, SdkInstallMode sdkInstallMode, CancellationToken cancellationToken = default)
    {
        LatestQueries.Add(packageName);
        LatestModes.Add(sdkInstallMode);
        if (ThrowLatestFor.Contains(packageName))
        {
            throw new InvalidOperationException($"Simulated latest-version failure for {packageName}");
        }
        return Task.FromResult(LatestVersions.TryGetValue(packageName, out var v) ? v : FallbackLatest);
    }

    public Task<Dictionary<string, string>> InstallPackageAsync(string package, string version, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        InstallCalls.Add((package, version));
        MarkPresent(package, version); // installing makes it present on disk, mirroring production
        var result = InstallReturns.TryGetValue(Key(package, version), out var d)
            ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [package] = version };
        return Task.FromResult(result);
    }

    public Task<Dictionary<string, string>> GetPackageDependenciesAsync(string packageName, string version, CancellationToken cancellationToken = default)
    {
        if (ThrowKeyNotFoundFor.Contains(packageName))
        {
            throw new KeyNotFoundException($"{packageName} not found in cache");
        }
        return Task.FromResult(DependencyMap.TryGetValue(Key(packageName, version), out var d)
            ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    public DirectoryInfo GetNuGetGlobalPackagesDir() => cacheRoot;

    public DirectoryInfo GetNuGetPackageDir(string packageName, string version)
        => new(Path.Combine(cacheRoot.FullName, packageName.ToLowerInvariant(), version));

    public void MarkPresent(string packageName, string version)
        => Directory.CreateDirectory(GetNuGetPackageDir(packageName, version).FullName);
}
