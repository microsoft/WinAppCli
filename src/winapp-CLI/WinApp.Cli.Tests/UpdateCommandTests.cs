// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Handler-level tests for <see cref="UpdateCommand"/> that pin down the version-decision gate
/// (<c>CompareVersions(latest, current) &gt; 0</c>): only a strictly-greater "latest" rewrites winapp.yaml
/// and triggers a reinstall; a normalized-equal or lower "latest" must leave the persisted version and the
/// installed set untouched. These guard against a normalized-but-equal version spuriously counting as an
/// update, or a lower "latest" silently downgrading the pinned version. Two further cases pin down the
/// failure paths: a latest-version lookup that throws must exit non-zero and preserve the pin rather than
/// report a false "up to date", and a cancellation (Ctrl+C) mid-lookup must abort the whole command instead
/// of being recorded as an ordinary lookup failure that lets the loop keep running.
/// </summary>
[TestClass]
public class UpdateCommandTests : BaseCommandTests
{
    private FakeNugetService _fakeNuget = null!;
    private RecordingPackageInstallationService _installer = null!;
    private StubBuildToolsService _buildTools = null!;
    private StubWorkspaceSetupService _workspace = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeNuget = new FakeNugetService();
        _installer = new RecordingPackageInstallationService();
        _buildTools = new StubBuildToolsService(_tempDirectory);
        _workspace = new StubWorkspaceSetupService();
        return services
            .AddSingleton<INugetService>(_fakeNuget)
            .AddSingleton<IPackageInstallationService>(_installer)
            .AddSingleton<IBuildToolsService>(_buildTools)
            .AddSingleton<IWorkspaceSetupService>(_workspace);
    }

    private const string PackageName = "Test.Pkg";

    private void SaveConfigWith(string pinnedVersion)
    {
        var config = new WinappConfig();
        config.SetVersion(PackageName, pinnedVersion);
        _configService.Save(config);
    }

    private async Task<int> RunUpdateAsync()
    {
        var command = GetRequiredService<UpdateCommand>();
        return await ParseAndInvokeWithCaptureAsync(command, []);
    }

    [TestMethod]
    public async Task Update_LatestIsHigher_RewritesYamlAndReinstalls()
    {
        // Pinned 1.0.0, feed reports 2.0.0 → strictly greater → the persisted version advances and the
        // updated package is reinstalled.
        SaveConfigWith("1.0.0");
        _fakeNuget.DefaultVersion = "2.0.0";

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("2.0.0", persisted, "A strictly-greater latest version must be written to winapp.yaml.");
        CollectionAssert.Contains(_installer.InstalledPackages, PackageName, "An update must reinstall the updated package.");
    }

    [TestMethod]
    public async Task Update_LatestIsNormalizedEqual_LeavesYamlAndSkipsInstall()
    {
        // Pinned 1.0.0, feed reports the normalized-equal "1.0" → CompareVersions == 0 → no update. The
        // persisted version stays exactly as written and nothing is reinstalled.
        SaveConfigWith("1.0.0");
        _fakeNuget.DefaultVersion = "1.0";

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("1.0.0", persisted, "A normalized-equal latest version must not rewrite the pinned version.");
        Assert.IsEmpty(_installer.InstalledPackages, "A no-op update must not reinstall packages.");
    }

    [TestMethod]
    public async Task Update_LatestIsLower_LeavesYamlAndSkipsInstall()
    {
        // Pinned 2.0.0, feed reports a lower 1.5.0 → CompareVersions < 0 → no update, no silent downgrade.
        SaveConfigWith("2.0.0");
        _fakeNuget.DefaultVersion = "1.5.0";

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("2.0.0", persisted, "A lower latest version must never downgrade the pinned version.");
        Assert.IsEmpty(_installer.InstalledPackages, "A lower latest version must not trigger a reinstall.");
    }

    [TestMethod]
    public async Task Update_LatestVersionLookupFails_ExitsNonZeroAndPreservesPin()
    {
        // A latest-version lookup that fails closed (a feed outage or auth failure) must NOT be reported as an
        // authoritative "up to date" result. GetLatestVersionAsync throws; the handler must keep the pinned
        // version in winapp.yaml untouched, skip the reinstall, and exit non-zero so callers (and CI) surface
        // the failure instead of a false success that silently freezes the pin.
        SaveConfigWith("1.0.0");
        _fakeNuget.PackagesToThrow.Add(PackageName);

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(1, exitCode, "A failed latest-version lookup must fail the command, not report success.");
        var persisted = _configService.Load().Packages.Single(p => p.Name == PackageName).Version;
        Assert.AreEqual("1.0.0", persisted, "A failed lookup must leave the pinned version unchanged.");
        Assert.IsEmpty(_installer.InstalledPackages, "A failed lookup must not trigger a reinstall.");
    }

    [TestMethod]
    public async Task Update_CancelledDuringLookup_AbortsWithoutCheckingRemainingPackages()
    {
        // Ctrl+C during a latest-version lookup must abort the whole command — cancellation must NOT be
        // swallowed by the ordinary lookup-failure handler, which would let the loop keep checking the
        // remaining packages and then proceed to install / build-tool work. The fake cancels the flow token
        // while checking the first package and throws; the handler must rethrow that cancellation, so the
        // second package is never queried and nothing is installed. Contrast with the fail-closed test above,
        // where an ordinary lookup failure lets the loop continue.
        var config = new WinappConfig();
        config.SetVersion("First.Pkg", "1.0.0");
        config.SetVersion("Second.Pkg", "1.0.0");
        _configService.Save(config);

        using var cts = new CancellationTokenSource();
        _fakeNuget.CancelOnQuery = cts;
        _fakeNuget.CancelOnQueryPackage = "First.Pkg";

        var command = GetRequiredService<UpdateCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [], cts.Token);

        Assert.AreNotEqual(0, exitCode, "A cancelled update must not report success.");
        CollectionAssert.Contains(_fakeNuget.QueriedPackages, "First.Pkg", "The first package triggers the cancellation.");
        CollectionAssert.DoesNotContain(_fakeNuget.QueriedPackages, "Second.Pkg",
            "Cancellation must abort the loop, so the second package is never queried.");
        Assert.IsEmpty(_installer.InstalledPackages, "A cancelled update must not reinstall packages.");
    }

    [TestMethod]
    public void ShortDescription_SummarizesUpdatingYaml()
    {
        Assert.AreEqual("Update packages in winapp.yaml", new UpdateCommand().ShortDescription);
    }

    [TestMethod]
    public async Task Update_ConfigExistsButHasNoPackages_SkipsPackageWorkAndSucceeds()
    {
        // winapp.yaml is present but pins nothing: the package-update loop is skipped entirely and the
        // command proceeds straight to build-tool / runtime work, succeeding without installing anything.
        _configService.Save(new WinappConfig());
        Assert.IsTrue(_configService.Exists(), "Precondition: an (empty) winapp.yaml must exist.");

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_installer.InstalledPackages, "An empty config must not install any packages.");
        Assert.IsEmpty(_fakeNuget.QueriedPackages, "An empty config must not query any latest versions.");
    }

    [TestMethod]
    public async Task Update_NoConfigFile_SkipsPackageStepAndStillProcessesBuildTools()
    {
        // No winapp.yaml at all: the package step is skipped, but build-tool and runtime work still run and
        // the command succeeds.
        Assert.IsFalse(_configService.Exists(), "Precondition: there must be no winapp.yaml.");

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_installer.InstalledPackages, "With no config, nothing is installed.");
    }

    [TestMethod]
    public async Task Update_BuildToolsUnavailable_ExitsNonZero()
    {
        // EnsureBuildToolsAsync returning null means the build tools could not be installed/updated; the
        // command must fail with a non-zero exit code rather than report success.
        _buildTools.BuildToolsDir = null;

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(1, exitCode, "A null build-tools path must fail the command.");
    }

    [TestMethod]
    public async Task Update_WindowsAppSdkMsixDirectoryFound_InstallsRuntime()
    {
        // When a Windows App SDK MSIX directory is present, the update flow must install the Windows App
        // Runtime from it before completing successfully.
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        _workspace.MsixDir = msixDir;

        var exitCode = await RunUpdateAsync();

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_workspace.RuntimeInstalled, "A found MSIX directory must trigger the Windows App Runtime install.");
        Assert.AreEqual(msixDir.FullName, _workspace.RuntimeInstallDir?.FullName, "The runtime must be installed from the discovered MSIX directory.");
    }

    /// <summary>
    /// Records which packages were passed to <see cref="InstallPackagesAsync"/> so tests can assert whether
    /// an update reinstalled anything. All other members are unreachable in the update flow and throw.
    /// </summary>
    private sealed class RecordingPackageInstallationService : IPackageInstallationService
    {
        public List<string> InstalledPackages { get; } = [];

        public Task<Dictionary<string, string>> InstallPackagesAsync(
            DirectoryInfo rootDirectory,
            IEnumerable<string> packages,
            TaskContext taskContext,
            SdkInstallMode sdkInstallMode = SdkInstallMode.Stable,
            bool ignoreConfig = false,
            CancellationToken cancellationToken = default)
        {
            var names = packages.ToList();
            InstalledPackages.AddRange(names);
            return Task.FromResult(names.ToDictionary(n => n, _ => "1.0.0"));
        }

        public void InitializeWorkspace(DirectoryInfo rootDirectory) { }

        public Task<bool> EnsurePackageAsync(DirectoryInfo rootDirectory, string packageName, TaskContext taskContext, string? version = null, SdkInstallMode sdkInstallMode = SdkInstallMode.Stable, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Returns a configurable directory from <see cref="EnsureBuildToolsAsync"/>: a non-null value makes the
    /// handler treat build tools as available, a null value makes it fail with exit code 1. Other members throw.
    /// </summary>
    private sealed class StubBuildToolsService(DirectoryInfo? buildToolsDir) : IBuildToolsService
    {
        public DirectoryInfo? BuildToolsDir { get; set; } = buildToolsDir;

        public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
            => Task.FromResult(BuildToolsDir);

        public FileInfo? GetBuildToolPath(string toolName) => throw new NotSupportedException();

        public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Reports a configurable Windows App SDK MSIX directory (null by default, so the update flow skips
    /// runtime installation) and records the runtime install when one is triggered.
    /// </summary>
    private sealed class StubWorkspaceSetupService : IWorkspaceSetupService
    {
        public DirectoryInfo? MsixDir { get; set; }

        public bool RuntimeInstalled { get; private set; }

        public DirectoryInfo? RuntimeInstallDir { get; private set; }

        public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null) => MsixDir;

        public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(int InstalledCount, int ErrorCount)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken)
        {
            RuntimeInstalled = true;
            RuntimeInstallDir = msixDir;
            return Task.FromResult((1, 0));
        }
    }
}
