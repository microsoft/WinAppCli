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
/// Tests for <see cref="UpdateCommand"/>. All external effects (NuGet queries, package
/// installation, build-tool acquisition, runtime install) are faked so the command's control
/// flow — config handling, per-package update decisions, build-tools gating, and the runtime
/// step — is exercised deterministically without network or machine state.
/// </summary>
[TestClass]
public class UpdateCommandTests : BaseCommandTests
{
    private FakeNugetService _fakeNuget = null!;
    private FakePackageInstallationService _fakeInstall = null!;
    private FakeWorkspaceSetupService _fakeWorkspace = null!;
    private FakeUpdateBuildToolsService _fakeBuildTools = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeNuget = new FakeNugetService();
        _fakeInstall = new FakePackageInstallationService();
        _fakeWorkspace = new FakeWorkspaceSetupService();
        _fakeBuildTools = new FakeUpdateBuildToolsService { BuildToolsDirectory = _ => CreateBuildToolsDir() };

        return services
            .AddSingleton<INugetService>(_fakeNuget)
            .AddSingleton<IPackageInstallationService>(_fakeInstall)
            .AddSingleton<IWorkspaceSetupService>(_fakeWorkspace)
            .AddSingleton<IBuildToolsService>(_fakeBuildTools);
    }

    private DirectoryInfo CreateBuildToolsDir()
        => _tempDirectory.CreateSubdirectory("buildtools");

    private void WriteConfig(params (string Name, string Version)[] packages)
    {
        var config = new WinappConfig();
        foreach (var (name, version) in packages)
        {
            config.SetVersion(name, version);
        }
        _configService.Save(config);
    }

    // ── No / empty config ───────────────────────────────────────────────

    [TestMethod]
    public async Task Update_NoWinappYaml_InstallsBuildToolsOnly()
    {
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls, "No config means no package install");
        Assert.HasCount(1, _fakeBuildTools.EnsureCalls);
        Assert.IsTrue(_fakeBuildTools.EnsureCalls[0], "Update forces the latest build tools");
    }

    [TestMethod]
    public async Task Update_ConfigWithNoPackages_SkipsPackageUpdate()
    {
        WriteConfig(); // empty packages list
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls);
        Assert.IsEmpty(_fakeNuget.QueriedPackages);
    }

    // ── Package update decisions ────────────────────────────────────────

    [TestMethod]
    public async Task Update_AllPackagesUpToDate_DoesNotReinstall()
    {
        _fakeNuget.DefaultVersion = "1.6.0";
        WriteConfig(("Microsoft.WindowsAppSDK", "1.6.0")); // already latest
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeNuget.QueriedPackages);
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls, "Up-to-date packages are not reinstalled");
    }

    [TestMethod]
    public async Task Update_OutOfDatePackages_SavesConfigAndReinstalls()
    {
        _fakeNuget.DefaultVersion = "1.6.0";
        WriteConfig(("Microsoft.WindowsAppSDK", "1.0.0")); // stale
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeInstall.InstallPackagesCalls);
        var call = _fakeInstall.InstallPackagesCalls[0];
        CollectionAssert.Contains(call.Packages, "Microsoft.WindowsAppSDK");
        Assert.IsFalse(call.IgnoreConfig, "Update installs using the updated config");

        // winapp.yaml was rewritten with the new version
        var reloaded = _configService.Load();
        Assert.AreEqual("1.6.0", reloaded.GetVersion("Microsoft.WindowsAppSDK"));
    }

    [TestMethod]
    public async Task Update_NuGetFailsForPackage_KeepsCurrentVersionAndSkipsInstall()
    {
        _fakeNuget.DefaultVersion = "1.6.0";
        _fakeNuget.PackagesToThrow.Add("Broken.Package");
        WriteConfig(("Broken.Package", "1.0.0"));
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode, "A transient NuGet failure is not fatal");
        CollectionAssert.Contains(_fakeNuget.QueriedPackages, "Broken.Package");
        Assert.IsEmpty(_fakeInstall.InstallPackagesCalls, "No update detected, so nothing is reinstalled");

        var reloaded = _configService.Load();
        Assert.AreEqual("1.0.0", reloaded.GetVersion("Broken.Package"), "Current version is preserved on error");
    }

    // ── Build tools gating ──────────────────────────────────────────────

    [TestMethod]
    public async Task Update_BuildToolsInstallFails_ReturnsError()
    {
        _fakeBuildTools.BuildToolsDirectory = _ => null; // acquisition failed
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Failed to install/update build tools");
    }

    // ── Runtime install step ────────────────────────────────────────────

    [TestMethod]
    public async Task Update_MsixDirectoryFound_InstallsRuntime()
    {
        _fakeWorkspace.MsixDirectory = _tempDirectory.CreateSubdirectory("msix");
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeWorkspace.InstallRuntimeCalls);
    }

    [TestMethod]
    public async Task Update_NoMsixDirectory_SkipsRuntimeInstall()
    {
        _fakeWorkspace.MsixDirectory = null;
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(0, exitCode);
        Assert.IsEmpty(_fakeWorkspace.InstallRuntimeCalls);
    }

    // ── Failure / options ───────────────────────────────────────────────

    [TestMethod]
    public async Task Update_UnexpectedException_ReturnsError()
    {
        _fakeBuildTools.BuildToolsDirectory = _ => throw new InvalidOperationException("boom");
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Update command failed");
    }

    [TestMethod]
    public async Task Update_PreviewSdks_ReinstallsWithPreviewMode()
    {
        _fakeNuget.DefaultVersion = "2.0.0-preview";
        WriteConfig(("Microsoft.WindowsAppSDK", "1.0.0"));
        var command = GetRequiredService<UpdateCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--setup-sdks", "preview"]);

        Assert.AreEqual(0, exitCode);
        Assert.HasCount(1, _fakeInstall.InstallPackagesCalls);
    }

    /// <summary>
    /// Configurable build-tools fake. Only <see cref="EnsureBuildToolsAsync"/> is used by the
    /// update flow; the factory lets each test return a directory, null, or throw.
    /// </summary>
    private sealed class FakeUpdateBuildToolsService : IBuildToolsService
    {
        public List<bool> EnsureCalls { get; } = [];
        public Func<TaskContext, DirectoryInfo?> BuildToolsDirectory { get; set; } = _ => null;

        public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
        {
            EnsureCalls.Add(forceLatest);
            return Task.FromResult(BuildToolsDirectory(taskContext));
        }

        public FileInfo? GetBuildToolPath(string toolName) => throw new NotSupportedException();

        public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
