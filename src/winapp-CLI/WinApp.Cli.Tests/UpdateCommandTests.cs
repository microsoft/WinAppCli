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
/// update, or a lower "latest" silently downgrading the pinned version.
/// </summary>
[TestClass]
public class UpdateCommandTests : BaseCommandTests
{
    private FakeNugetService _fakeNuget = null!;
    private RecordingPackageInstallationService _installer = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeNuget = new FakeNugetService();
        _installer = new RecordingPackageInstallationService();
        return services
            .AddSingleton<INugetService>(_fakeNuget)
            .AddSingleton<IPackageInstallationService>(_installer)
            .AddSingleton<IBuildToolsService>(new StubBuildToolsService(_tempDirectory))
            .AddSingleton<IWorkspaceSetupService>(new StubWorkspaceSetupService());
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
    /// Returns an existing directory from <see cref="EnsureBuildToolsAsync"/> so the handler treats build
    /// tools as available (a null return makes the handler fail with exit code 1). Other members throw.
    /// </summary>
    private sealed class StubBuildToolsService(DirectoryInfo buildToolsDir) : IBuildToolsService
    {
        public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
            => Task.FromResult<DirectoryInfo?>(buildToolsDir);

        public FileInfo? GetBuildToolPath(string toolName) => throw new NotSupportedException();

        public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Reports no Windows App SDK MSIX directory so the update flow skips runtime installation. The
    /// runtime-install members are unreachable and throw.
    /// </summary>
    private sealed class StubWorkspaceSetupService : IWorkspaceSetupService
    {
        public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null) => null;

        public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(int InstalledCount, int ErrorCount)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
