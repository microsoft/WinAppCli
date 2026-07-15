// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="PackageInstallationService"/>. A controllable in-memory NuGet fake (backed by a
/// real temp cache directory so the "already present" checks behave realistically) and a fake config
/// service drive the version-resolution, transitive-dependency and version-merge logic without any network.
/// </summary>
[TestClass]
public class PackageInstallationServiceTests
{
    private DirectoryInfo _tempDir = null!;
    private DirectoryInfo _cacheDir = null!;
    private DirectoryInfo _rootDir = null!;
    private FakeConfigService _config = null!;
    private FakeNugetService _nuget = null!;
    private PackageInstallationService _service = null!;
    private TaskContext _taskContext = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"PkgInstTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
        _cacheDir = _tempDir.CreateSubdirectory("cache");
        _rootDir = new DirectoryInfo(Path.Combine(_tempDir.FullName, "root"));
        _config = new FakeConfigService();
        _nuget = new FakeNugetService { CacheDirectory = _cacheDir };
        _service = new PackageInstallationService(_config, _nuget, NullLogger<PackageInstallationService>.Instance);

        var task = new GroupableTask("test", null);
        _taskContext = new TaskContext(task, null, new TestConsole(), NullLogger<PackageInstallationServiceTests>.Instance, new Lock());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    /// <summary>Creates the on-disk package directory so <c>packageDir.Exists</c> is true (simulates a cached package).</summary>
    private void MarkPresent(string name, string version) => _nuget.GetNuGetPackageDir(name, version).Create();

    #region InitializeWorkspace

    [TestMethod]
    public void InitializeWorkspace_CreatesMissingDirectory()
    {
        Assert.IsFalse(_rootDir.Exists);

        _service.InitializeWorkspace(_rootDir);

        _rootDir.Refresh();
        Assert.IsTrue(_rootDir.Exists);
    }

    [TestMethod]
    public void InitializeWorkspace_ExistingDirectory_NoThrow()
    {
        _rootDir.Create();

        _service.InitializeWorkspace(_rootDir);

        _rootDir.Refresh();
        Assert.IsTrue(_rootDir.Exists);
    }

    #endregion

    #region EnsurePackageAsync

    [TestMethod]
    public async Task EnsurePackageAsync_Success_ReturnsTrue_AndCreatesWorkspace()
    {
        _nuget.DefaultVersion = "1.6.0";

        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.X", _taskContext);

        Assert.IsTrue(ok);
        _rootDir.Refresh();
        Assert.IsTrue(_rootDir.Exists);
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "1.6.0"));
    }

    [TestMethod]
    public async Task EnsurePackageAsync_ExplicitVersion_DoesNotQueryLatest()
    {
        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.X", _taskContext, version: "2.0.0");

        Assert.IsTrue(ok);
        CollectionAssert.DoesNotContain(_nuget.QueriedPackages, "Pkg.X");
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "2.0.0"));
    }

    [TestMethod]
    public async Task EnsurePackageAsync_AlreadyPresent_SkipsInstall()
    {
        MarkPresent("Pkg.X", "3.0.0");

        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.X", _taskContext, version: "3.0.0");

        Assert.IsTrue(ok);
        Assert.AreEqual(0, _nuget.InstalledPackages.Count, "Package already present; no install should occur.");
    }

    [TestMethod]
    public async Task EnsurePackageAsync_NugetThrows_ReturnsFalse()
    {
        _nuget.PackagesToThrow.Add("Pkg.Bad");

        var ok = await _service.EnsurePackageAsync(_rootDir, "Pkg.Bad", _taskContext);

        Assert.IsFalse(ok);
    }

    #endregion

    #region InstallPackagesAsync — version resolution

    [TestMethod]
    public async Task InstallPackagesAsync_NoConfig_UsesLatestVersion()
    {
        _nuget.DefaultVersion = "1.6.0";

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext);

        Assert.AreEqual("1.6.0", result["Pkg.X"]);
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Pkg.X", "1.6.0"));
    }

    [TestMethod]
    public async Task InstallPackagesAsync_PinnedConfigVersion_UsedInsteadOfLatest()
    {
        _config.ExistsResult = true;
        _config.Config.SetVersion("Pkg.X", "1.2.3");

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext);

        Assert.AreEqual("1.2.3", result["Pkg.X"]);
        CollectionAssert.DoesNotContain(_nuget.QueriedPackages, "Pkg.X");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_ConfigWithoutPinForPackage_FallsBackToLatest()
    {
        _config.ExistsResult = true;
        _config.Config.SetVersion("Some.Other.Package", "9.9.9");
        _nuget.DefaultVersion = "1.6.0";

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext);

        Assert.AreEqual("1.6.0", result["Pkg.X"]);
        CollectionAssert.Contains(_nuget.QueriedPackages, "Pkg.X");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_IgnoreConfig_UsesLatestEvenWhenPinned()
    {
        _config.ExistsResult = true;
        _config.Config.SetVersion("Pkg.X", "1.2.3");
        _nuget.DefaultVersion = "1.6.0";

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.X"], _taskContext, ignoreConfig: true);

        Assert.AreEqual("1.6.0", result["Pkg.X"]);
        CollectionAssert.Contains(_nuget.QueriedPackages, "Pkg.X");
    }

    #endregion

    #region InstallPackagesAsync — merge / transitive dependencies

    [TestMethod]
    public async Task InstallPackagesAsync_MergesInstalledVersions_HigherWins()
    {
        // Two fresh packages whose installs both surface a shared transitive package at different versions.
        _nuget.InstallReturns["Pkg.A"] = new() { ["Pkg.A"] = "1.6.0", ["Shared"] = "1.0.0" };
        _nuget.InstallReturns["Pkg.B"] = new() { ["Pkg.B"] = "1.6.0", ["Shared"] = "2.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.A", "Pkg.B"], _taskContext);

        Assert.AreEqual("2.0.0", result["Shared"], "Higher shared version should win the merge.");
        Assert.AreEqual("1.6.0", result["Pkg.A"]);
        Assert.AreEqual("1.6.0", result["Pkg.B"]);
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_InstallsMissingTransitiveDependency()
    {
        MarkPresent("Pkg.Main", "1.6.0");
        _nuget.Dependencies["Pkg.Main"] = new() { ["Common"] = "[1.0.0, )" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.Main"], _taskContext);

        Assert.AreEqual("1.6.0", result["Pkg.Main"]);
        Assert.AreEqual("1.0.0", result["Common"]);
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Common", "1.0.0"));
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_TransitiveDependencyOnDisk_NotReinstalled()
    {
        MarkPresent("Pkg.Main", "1.6.0");
        MarkPresent("Common", "1.0.0");
        _nuget.Dependencies["Pkg.Main"] = new() { ["Common"] = "1.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.Main"], _taskContext);

        Assert.AreEqual("1.0.0", result["Common"]);
        CollectionAssert.DoesNotContain(_nuget.InstalledPackages, ("Common", "1.0.0"));
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_TransitiveDependenciesOnDisk_HigherVersionWins()
    {
        // Two present packages both depend on an on-disk 'Shared' at different versions.
        MarkPresent("Pkg.A", "1.6.0");
        MarkPresent("Pkg.B", "1.6.0");
        MarkPresent("Shared", "1.0.0");
        MarkPresent("Shared", "2.0.0");
        _nuget.Dependencies["Pkg.A"] = new() { ["Shared"] = "1.0.0" };
        _nuget.Dependencies["Pkg.B"] = new() { ["Shared"] = "2.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.A", "Pkg.B"], _taskContext);

        Assert.AreEqual("2.0.0", result["Shared"], "The higher on-disk transitive version should win.");
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_EmptyDependencyRange_Ignored()
    {
        MarkPresent("Pkg.Main", "1.6.0");
        // Whitespace/empty range parses to empty and must be skipped without error.
        _nuget.Dependencies["Pkg.Main"] = new() { ["Weird"] = "   " };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.Main"], _taskContext);

        Assert.IsFalse(result.ContainsKey("Weird"));
        Assert.AreEqual("1.6.0", result["Pkg.Main"]);
    }

    [TestMethod]
    public async Task InstallPackagesAsync_AlreadyPresent_DependencyLookupThrowsKeyNotFound_Ignored()
    {
        MarkPresent("Pkg.Main", "1.6.0");
        _nuget.ThrowKeyNotFoundFor.Add("Pkg.Main");

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.Main"], _taskContext);

        // The main package still resolves even though dependency resolution failed.
        Assert.AreEqual("1.6.0", result["Pkg.Main"]);
    }

    [TestMethod]
    public async Task InstallPackagesAsync_MissingTransitiveInstall_SurfacesHigherVersionOfTracked_Upgrades()
    {
        // Pkg.Main is already on disk (tracked at 1.6.0). Its transitive dependency 'Common' is missing,
        // so Common gets installed — and that install surfaces a NEWER Pkg.Main (2.0.0). The higher-wins
        // merge over the freshly-installed dependency set must upgrade the tracked Pkg.Main version.
        MarkPresent("Pkg.Main", "1.6.0");
        _nuget.Dependencies["Pkg.Main"] = new() { ["Common"] = "1.0.0" };
        _nuget.InstallReturns["Common"] = new() { ["Common"] = "1.0.0", ["Pkg.Main"] = "2.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.Main"], _taskContext);

        Assert.AreEqual("2.0.0", result["Pkg.Main"], "Newer version surfaced by the transitive install should win.");
        Assert.AreEqual("1.0.0", result["Common"]);
        CollectionAssert.Contains(_nuget.InstalledPackages, ("Common", "1.0.0"));
    }

    [TestMethod]
    public async Task InstallPackagesAsync_MissingTransitiveInstall_SurfacesLowerVersionOfTracked_KeepsExisting()
    {
        // Same shape, but the transitive install surfaces an OLDER Pkg.Main (1.0.0) than the tracked 1.6.0.
        // The merge must keep the existing higher version (exercises the compare-not-greater branch).
        MarkPresent("Pkg.Main", "1.6.0");
        _nuget.Dependencies["Pkg.Main"] = new() { ["Common"] = "1.0.0" };
        _nuget.InstallReturns["Common"] = new() { ["Common"] = "1.0.0", ["Pkg.Main"] = "1.0.0" };

        var result = await _service.InstallPackagesAsync(_rootDir, ["Pkg.Main"], _taskContext);

        Assert.AreEqual("1.6.0", result["Pkg.Main"], "Existing higher version must be kept over the lower surfaced one.");
    }

    #endregion

    private sealed class FakeConfigService : IConfigService
    {
        public FileInfo ConfigPath { get; set; } = new(Path.Combine(Path.GetTempPath(), "winapp.yaml"));
        public bool ExistsResult { get; set; }
        public WinappConfig Config { get; set; } = new();

        public bool Exists() => ExistsResult;
        public WinappConfig Load() => Config;
        public void Save(WinappConfig cfg) => Config = cfg;
    }
}
