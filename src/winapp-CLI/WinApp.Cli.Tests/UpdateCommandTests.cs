// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Behavior tests for the <c>winapp update</c> command (<see cref="UpdateCommand.Handler"/>).
/// The real <see cref="IStatusService"/> executes the handler body, while NuGet, package
/// installation, build tools, and workspace setup are replaced with controllable fakes so the
/// version-check / save / install / build-tools / runtime paths run without any network or disk SDK.
/// </summary>
[TestClass]
public sealed class UpdateCommandTests : BaseCommandTests
{
    private ControllableNugetService _nuget = null!;
    private FakePackageInstallationService _pkg = null!;
    private FakeBuildToolsService _buildTools = null!;
    private UpdateWorkspaceFake _workspace = null!;

    private static readonly string[] PkgAB = ["Pkg.A", "Pkg.B"];

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new ControllableNugetService(new DirectoryInfo(Path.GetTempPath()));
        _pkg = new FakePackageInstallationService();
        _buildTools = new FakeBuildToolsService();
        _workspace = new UpdateWorkspaceFake();
        return services
            .AddSingleton<INugetService>(_nuget)
            .AddSingleton<IPackageInstallationService>(_pkg)
            .AddSingleton<IBuildToolsService>(_buildTools)
            .AddSingleton<IWindowsAppRuntimeService>(_workspace);
    }

    [TestInitialize]
    public void Setup()
    {
        // Default to the happy path: build tools resolve successfully and no runtime MSIX is found.
        _buildTools.BuildToolsResult = _tempDirectory;
        _workspace.MsixDirectory = null;
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

    private UpdateCommand GetCommand() => GetRequiredService<UpdateCommand>();

    // ───────────────────────────── command metadata ─────────────────────────────

    [TestMethod]
    public void UpdateCommand_ExposesNameShortDescriptionAndSetupSdksOption()
    {
        var command = GetCommand();

        Assert.AreEqual("update", command.Name);
        Assert.AreEqual("Update packages in winapp.yaml", command.ShortDescription);
        Assert.Contains(InitCommand.SetupSdksOption, command.Options, "update must expose the --setup-sdks option.");
    }

    // ───────────────────────────── config discovery ─────────────────────────────

    [TestMethod]
    public async Task Update_NoConfig_SkipsPackageWork_ButEnsuresBuildTools()
    {
        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);
        Assert.IsEmpty(_pkg.InstallPackagesCalls, "No winapp.yaml means no package installation.");
        Assert.IsEmpty(_nuget.LatestQueries, "No config means no version checks.");
        Assert.HasCount(1, _buildTools.EnsureBuildToolsForceLatest);
        Assert.IsTrue(_buildTools.EnsureBuildToolsForceLatest[0], "update must force-latest build tools.");
    }

    [TestMethod]
    public async Task Update_ConfigWithNoPackages_SkipsPackageWork()
    {
        SaveConfig(); // writes an empty "packages:" section

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);
        Assert.IsEmpty(_pkg.InstallPackagesCalls);
        Assert.IsEmpty(_nuget.LatestQueries, "An empty package list must not trigger version checks.");
    }

    // ───────────────────────────── version check / update ─────────────────────────────

    [TestMethod]
    public async Task Update_AllPackagesUpToDate_DoesNotSaveOrInstall()
    {
        SaveConfig(("Pkg.A", "1.0.0"), ("Pkg.B", "2.0.0"));
        _nuget.LatestVersions["Pkg.A"] = "1.0.0";
        _nuget.LatestVersions["Pkg.B"] = "2.0.0";

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);
        Assert.IsEmpty(_pkg.InstallPackagesCalls, "Nothing to install when everything is current.");
        CollectionAssert.AreEquivalent(PkgAB, _nuget.LatestQueries, "Both packages must be checked.");

        // The on-disk config must be unchanged.
        var reloaded = _configService.Load();
        Assert.AreEqual("1.0.0", reloaded.GetVersion("Pkg.A"));
        Assert.AreEqual("2.0.0", reloaded.GetVersion("Pkg.B"));
    }

    [TestMethod]
    public async Task Update_NewerVersionAvailable_SavesYamlAndInstalls()
    {
        SaveConfig(("Pkg.A", "1.0.0"), ("Pkg.B", "2.0.0"));
        _nuget.LatestVersions["Pkg.A"] = "1.5.0"; // newer
        _nuget.LatestVersions["Pkg.B"] = "2.0.0"; // same

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);

        // winapp.yaml must be rewritten with the upgraded version.
        var reloaded = _configService.Load();
        Assert.AreEqual("1.5.0", reloaded.GetVersion("Pkg.A"), "The upgraded version must be persisted.");
        Assert.AreEqual("2.0.0", reloaded.GetVersion("Pkg.B"));

        // The updated set of packages must be installed with ignoreConfig=false.
        Assert.HasCount(1, _pkg.InstallPackagesCalls);
        var call = _pkg.InstallPackagesCalls[0];
        CollectionAssert.AreEquivalent(PkgAB, call.Packages);
        Assert.IsFalse(call.IgnoreConfig, "update installs against the freshly-written config, so ignoreConfig must be false.");
    }

    [TestMethod]
    public async Task Update_VersionCheckThrowsForOnePackage_KeepsItsCurrentVersion_AndUpgradesOthers()
    {
        SaveConfig(("Pkg.A", "1.0.0"), ("Pkg.B", "2.0.0"));
        _nuget.ThrowLatestFor.Add("Pkg.A");        // transient failure for A -> keep current
        _nuget.LatestVersions["Pkg.B"] = "2.5.0";  // B upgrades -> triggers save+install

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);

        var reloaded = _configService.Load();
        Assert.AreEqual("1.0.0", reloaded.GetVersion("Pkg.A"), "A failed check must retain the current pinned version.");
        Assert.AreEqual("2.5.0", reloaded.GetVersion("Pkg.B"), "B must be upgraded despite A's failure.");
        Assert.HasCount(1, _pkg.InstallPackagesCalls, "The upgrade to B must still drive an install.");
    }

    [TestMethod]
    public async Task Update_SetupSdksPreview_PropagatesPreviewModeToVersionChecks()
    {
        SaveConfig(("Pkg.A", "1.0.0"));
        _nuget.LatestVersions["Pkg.A"] = "1.6.0-preview1";

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), ["--setup-sdks", "preview"]);

        Assert.AreEqual(0, exit);
        Assert.IsNotEmpty(_nuget.LatestModes);
        Assert.IsTrue(_nuget.LatestModes.TrueForAll(m => m == SdkInstallMode.Preview),
            "The --setup-sdks preview option must flow through to the version lookup mode.");
    }

    // ───────────────────────────── build tools ─────────────────────────────

    [TestMethod]
    public async Task Update_BuildToolsFail_ReturnsOneAndSkipsRuntime()
    {
        _buildTools.BuildToolsResult = null; // EnsureBuildToolsAsync returns null => failure

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(1, exit, "A build-tools failure must surface as a non-zero exit code.");
        Assert.IsEmpty(_workspace.InstallRuntimeCalls, "Runtime installation must be skipped when build tools fail.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "build tools", StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── runtime install ─────────────────────────────

    [TestMethod]
    public async Task Update_RuntimeMsixFound_InstallsWindowsAppRuntime()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix");
        _workspace.MsixDirectory = msixDir;
        _workspace.InstallRuntimeResult = (2, 0);

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);
        Assert.HasCount(1, _workspace.InstallRuntimeCalls, "A discovered MSIX directory must trigger a runtime install.");
        Assert.AreEqual(msixDir.FullName, _workspace.InstallRuntimeCalls[0].FullName);
    }

    [TestMethod]
    public async Task Update_NoRuntimeMsix_SkipsRuntimeInstall()
    {
        _workspace.MsixDirectory = null;

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(0, exit);
        Assert.IsEmpty(_workspace.InstallRuntimeCalls, "No MSIX directory means the runtime install is skipped.");
    }

    // ───────────────────────────── exception path ─────────────────────────────

    [TestMethod]
    public async Task Update_UnexpectedException_IsCaughtAndReturnsOne()
    {
        var msixDir = _tempDirectory.CreateSubdirectory("msix-throw");
        _workspace.MsixDirectory = msixDir;
        _workspace.InstallRuntimeException = new InvalidOperationException("boom during runtime install");

        var exit = await ParseAndInvokeWithCaptureAsync(GetCommand(), []);

        Assert.AreEqual(1, exit, "An unexpected exception must be caught and reported as exit code 1.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "boom during runtime install", StringComparison.Ordinal);
    }
}

/// <summary>
/// Test-local <see cref="IWindowsAppRuntimeService"/> that records runtime-install calls and can be
/// told to throw, so the update command's runtime-install and top-level catch paths are reachable
/// deterministically.
/// </summary>
internal sealed class UpdateWorkspaceFake : IWindowsAppRuntimeService
{
    public DirectoryInfo? MsixDirectory { get; set; }
    public List<DirectoryInfo> InstallRuntimeCalls { get; } = [];
    public (int InstalledCount, int ErrorCount) InstallRuntimeResult { get; set; } = (1, 0);
    public Exception? InstallRuntimeException { get; set; }

    public DirectoryInfo? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null, bool requireExactVersion = false) => MsixDirectory;

    public bool IsRuntimeRegisteredResult { get; set; } = true;

    public Task<(int InstalledCount, int ErrorCount, IReadOnlyList<(string Name, string Version)> RuntimePackages)> InstallWindowsAppRuntimeAsync(DirectoryInfo msixDir, TaskContext taskContext, CancellationToken cancellationToken, string? architecture = null)
    {
        InstallRuntimeCalls.Add(msixDir);
        if (InstallRuntimeException != null)
        {
            throw InstallRuntimeException;
        }
        return Task.FromResult((InstallRuntimeResult.InstalledCount, InstallRuntimeResult.ErrorCount, (IReadOnlyList<(string Name, string Version)>)[]));
    }

    public bool IsWindowsAppRuntimeRegistered(string? architecture, IReadOnlyList<(string Name, string Version)>? expectedRuntimePackages = null)
        => IsRuntimeRegisteredResult;
}
