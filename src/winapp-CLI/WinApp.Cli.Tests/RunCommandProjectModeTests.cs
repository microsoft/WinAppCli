// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Project-mode routing tests for <see cref="RunCommand"/>. A <see cref="FakeProjectRunService"/>
/// supplies canned build outcomes so the packaged/unpackaged launch branches can be verified without
/// invoking the real .NET SDK. See spec <c>specs/winapp-run-csproj.md</c> §7–§9.
/// </summary>
[TestClass]
public class RunCommandProjectModeTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="TestPackage" Publisher="CN=TestPublisher" Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private FileInfo CreateCsproj(string name = "App.csproj")
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return new FileInfo(path);
    }

    private DirectoryInfo CreateTargetDir(bool withManifest)
    {
        var dir = _tempDirectory.CreateSubdirectory($"bin_{Guid.NewGuid():N}");
        if (withManifest)
        {
            File.WriteAllText(Path.Combine(dir.FullName, "appxmanifest.xml"), TestManifestContent);
        }
        return dir;
    }

    private void SetUnpackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, bool selfContained, string arch = "x64")
    {
        var exe = Path.Combine(targetDir.FullName, "App.exe");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, exe, ProjectPackaging.Unpackaged, selfContained, arch), 0);
    }

    private void SetPackagedOutcome(FileInfo csproj, DirectoryInfo targetDir, string arch = "x64")
    {
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, arch), 0);
    }

    #region Unpackaged

    [TestMethod]
    public async Task ProjectMode_Unpackaged_InstallsRuntimeForArchAndLaunchesExecutable()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count, "Unpackaged app should launch via LaunchExecutable");
        StringAssert.Contains(_fakeAppLauncherService.LaunchExecutableCalls[0].ExePath, "App.exe");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Unpackaged app must NOT use AUMID activation");
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Runtime should be installed for a non-self-contained app");
        Assert.AreEqual("x64", _fakeMsixService.EnsureRuntimeInstalledCalls[0].Architecture, "Runtime install must honor the resolved arch");
        Assert.AreEqual(csproj.FullName, _fakeMsixService.EnsureRuntimeInstalledCalls[0].ProjectFile);
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Arm64_InstallsRuntimeForArm64()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false, arch: "arm64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "arm64", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("arm64", _fakeMsixService.EnsureRuntimeInstalledCalls[0].Architecture);
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_NoRestore_ThreadsNoRestoreIntoRuntimeInstall()
    {
        // C43: a --no-restore run must not trigger an implicit restore during runtime discovery, so the
        // flag has to reach EnsureWindowsAppRuntimeInstalledAsync (which forwards it to dotnet list package).
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        var exe = Path.Combine(targetDir.FullName, "App.exe");
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, exe, ProjectPackaging.Unpackaged, false, "x64", NoRestore: true), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-restore", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count);
        Assert.IsTrue(_fakeMsixService.EnsureRuntimeInstalledCalls[0].NoRestore, "Runtime install must honor the run's --no-restore");
    }

    [TestMethod]
    public async Task ProjectMode_UnpackagedSelfContained_SkipsRuntimeInstall()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        Assert.AreEqual(0, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Self-contained apps carry their own runtime — no install");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_PropagatesNonZeroExitCode()
    {
        // Spec M3: a directly-launched unpackaged app that exits non-zero must be reported as a
        // failure, not masked as success. Waiting on the owned handle (not a PID re-attach) keeps
        // ExitCode valid even when the process exits before the wait begins. Runs WITHOUT --detach so
        // the wait/exit-code path is exercised; self-contained to skip the runtime-install branch.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        _fakeAppLauncherService.FakeExitCode = 42;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(42, exitCode, "A non-zero exit from the launched app must propagate (M3 exit-code loss)");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        Assert.IsNotNull(_fakeAppLauncherService.LastLaunchedProcess);
        Assert.IsTrue(_fakeAppLauncherService.LastLaunchedProcess!.Disposed, "The owned handle must be disposed after the wait");
        Assert.IsFalse(_fakeAppLauncherService.LastLaunchedProcess!.Killed, "A normally-exiting app must not be killed");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_RuntimePrepFailure_AbortsWithoutLaunching()
    {
        // Spec R2-M2: when runtime preparation throws (e.g. the version-specific gate can't confirm the
        // required Windows App Runtime is registered), the run must abort with a non-zero exit and must
        // never launch the app. Verifies the command wiring of the abort, not just the gate in isolation.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        _fakeMsixService.EnsureRuntimeInstalledException = new InvalidOperationException("runtime not registered");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreNotEqual(0, exitCode, "A runtime-prep failure must produce a non-zero exit");
        Assert.AreEqual(1, _fakeMsixService.EnsureRuntimeInstalledCalls.Count, "Runtime prep should have been attempted");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "The app must NOT launch when runtime prep fails");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_RejectsIdentityOnlyOption()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        // Probe defaults to false (indeterminate), so this exercises the AUTHORITATIVE post-build gate:
        // the app is built + resolved, then --clean is rejected because it resolved unpackaged.
        var command = GetRequiredService<RunCommand>();

        // --clean only makes sense for a packaged (MSIX) app.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--clean"]);

        Assert.AreEqual(1, exitCode, "Identity-only options must be rejected for unpackaged apps");
        Assert.AreEqual(1, _fakeProjectRunService.BuildAndResolveCalls.Count, "Indeterminate packaging must build, then reject at the authoritative gate");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "App must not launch when an invalid option was supplied");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_RejectsExecutableOption()
    {
        // M6: --executable selects an entry inside an MSIX layout; it is meaningless for an unpackaged
        // app (which launches the built apphost directly). It must be rejected, not silently ignored.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--executable", "Other.exe"]);

        Assert.AreEqual(1, exitCode, "--executable must be rejected for unpackaged apps");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "App must not launch when --executable was supplied to an unpackaged app");
    }

    [TestMethod]
    public async Task ProjectMode_DefinitivelyUnpackaged_RejectsIdentityOnlyOptionBeforeBuilding()
    {
        // Issue #676: when the project is definitively unpackaged (WindowsPackageType=None), an
        // identity-only option like --no-launch is rejected by the pre-build probe — the user does
        // not pay the build cost first.
        var csproj = CreateCsproj();
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-launch"]);

        Assert.AreEqual(1, exitCode, "An identity-only option on a definitively-unpackaged app must fail");
        Assert.AreEqual(1, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "The pre-build probe must run");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "The fast-fail must reject before building");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_CompatibleOptions_SkipTheFastFailProbe()
    {
        // --detach is valid for unpackaged apps, so the pre-build probe should be short-circuited
        // (no incompatible option to reject) and the app should build + launch normally.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "The probe must be skipped when no incompatible option is present");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_NoBuild_SkipsTheFastFailProbe()
    {
        // Under --no-build there is no build cost to save, so the pre-build probe is skipped; the
        // authoritative post-build gate still rejects the incompatible option.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        _fakeProjectRunService.DefinitivelyUnpackaged = true;
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--no-build", "--no-launch"]);

        Assert.AreEqual(1, exitCode, "The incompatible option is still rejected at the authoritative gate");
        Assert.AreEqual(0, _fakeProjectRunService.IsDefinitivelyUnpackagedCalls.Count, "The probe must be skipped under --no-build");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_ForcedUnpackaged_ForwardsPropertyAndLaunchesExecutable()
    {
        // C4 regression (unit level): -p WindowsPackageType=None is forwarded to the build and the app
        // runs unpackaged. The end-to-end WindowsPackageType assertion lives in the Pester sample test.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [csproj.FullName, "-p", "WindowsPackageType=None", "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchExecutableCalls.Count);
        CollectionAssert.Contains(_fakeProjectRunService.BuildOptions[0].Properties.ToArray(), "WindowsPackageType=None",
            "User -p property must be forwarded to the build options");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Detach_SuppressesChildStdio()
    {
        // C37: a detached launch must NOT let the child inherit winapp's std handles — inheriting keeps the
        // npm wrapper's captured stdout pipe open, so `run({detach:true})` would block until the app exits.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(LaunchStdioMode.Suppress, _fakeAppLauncherService.LastLaunchStdioMode,
            "A detached launch must suppress child stdio so it doesn't hold the parent capture pipe open");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Json_SuppressesChildStdio()
    {
        // C37: under --json the child must not inherit winapp's stdout, or app output would corrupt the
        // single JSON object the CLI writes.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(LaunchStdioMode.Suppress, _fakeAppLauncherService.LastLaunchStdioMode,
            "A --json launch must suppress child stdio so app output cannot corrupt the JSON envelope");
    }

    [TestMethod]
    public async Task ProjectMode_Unpackaged_Foreground_InheritsChildStdio()
    {
        // C37: a plain foreground run streams the app's output inline (like `dotnet run`), so the child
        // inherits winapp's std handles.
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetUnpackagedOutcome(csproj, targetDir, selfContained: true);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(LaunchStdioMode.Inherit, _fakeAppLauncherService.LastLaunchStdioMode,
            "A foreground, non-JSON launch must inherit stdio so output streams inline");
    }

    #endregion

    #region Packaged

    [TestMethod]
    public async Task ProjectMode_Packaged_InstallsArchRuntimeAndLaunchesViaAumid()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        SetPackagedOutcome(csproj, targetDir, arch: "x64");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Packaged app should register a loose-layout identity");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.AreEqual("x64", _fakeMsixService.AddLooseLayoutRuntimeCalls[0].RuntimeArch, "Loose-layout runtime install must honor the resolved arch");
        Assert.AreEqual(csproj.FullName, _fakeMsixService.AddLooseLayoutRuntimeCalls[0].ProjectFile);
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Packaged app should launch via AUMID");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count, "Packaged app must NOT launch the apphost exe directly");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_ThreadsResolvedFrameworkIntoRuntimeProvisioning()
    {
        // M2: for a multi-targeted packaged app the resolved TFM must reach loose-layout runtime
        // provisioning so the WASDK runtime is narrowed to that framework's package set (not an
        // arbitrary FirstOrDefault across all TFMs, which can install the wrong runtime version).
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: true);
        const string tfm = "net10.0-windows10.0.26100.0";
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(
            new ProjectRunResolution(csproj, targetDir.FullName, null, ProjectPackaging.Packaged, false, "x64", tfm), 0);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.AreEqual(tfm, _fakeMsixService.AddLooseLayoutRuntimeCalls[0].Framework, "The resolved target framework must be threaded into runtime provisioning");
    }

    [TestMethod]
    public async Task ProjectMode_Packaged_NoManifestInOutput_Errors()
    {
        var csproj = CreateCsproj();
        var targetDir = CreateTargetDir(withManifest: false);
        SetPackagedOutcome(csproj, targetDir);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode, "A packaged app with no AppxManifest.xml in the output is a misconfiguration");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count);
    }

    #endregion

    #region Guardrails / errors

    [TestMethod]
    public async Task ProjectMode_SdkTooOld_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.SdkError = "SDK too old.";
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Build must not run when the SDK is incapable");
    }

    [TestMethod]
    public async Task ProjectMode_BuildFails_PropagatesExitCode()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildOutcome = new ProjectBuildOutcome(null, 7);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(7, exitCode, "A build failure must propagate the dotnet exit code");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_BuildThrowsGuardrail_Errors()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.BuildThrows = new ProjectRunException("Not a runnable project.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchExecutableCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_InvalidArch_ErrorsBeforeBuild()
    {
        var csproj = CreateCsproj();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--arch", "sparc", "--detach"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "An unsupported --arch must fail before building");
    }

    [TestMethod]
    public async Task ResolveInputAmbiguity_Errors()
    {
        var csproj = CreateCsproj();
        _fakeProjectRunService.ResolveInputThrows = new ProjectRunException("Multiple .csproj files found.");
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName]);

        Assert.AreEqual(1, exitCode, "Ambiguous multi-csproj input must surface an error");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
    }

    [TestMethod]
    public async Task ProjectMode_MalformedProperty_Errors()
    {
        // Spec L3: a -p value that isn't Name=Value (here, no '=') is rejected before building so it
        // never becomes a malformed '-p:' MSBuild argument.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "NoEqualsSign", "--detach"]);

        Assert.AreEqual(1, exitCode, "A malformed -p (no '=') must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_WhitespaceNameProperty_Errors()
    {
        // C16 (Copilot review): a -p whose name before '=' is empty or whitespace-only (here " =Value")
        // must be rejected before building, not forwarded as a nonsensical '-p: =Value' MSBuild argument.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", " =Value", "--detach"]);

        Assert.AreEqual(1, exitCode, "A -p with a whitespace-only name must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_LeadingEqualsProperty_Errors()
    {
        // C16: a -p that starts with '=' (empty name) is likewise rejected before building.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p", "=Value", "--detach"]);

        Assert.AreEqual(1, exitCode, "A -p with an empty name must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_ValuelessProperty_Errors()
    {
        // Spec L3: a bare -p with no value is rejected in the handler (via the raw OptionResult:
        // more '-p' identifier tokens than captured values) rather than silently producing no
        // property. Detecting it in the handler -- instead of relying on a System.CommandLine arity
        // error -- keeps the failure on the command's own error path so --json still gets JSON.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "-p"]);

        Assert.AreEqual(1, exitCode, "A valueless -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "Validation must happen before building");
    }

    [TestMethod]
    public async Task ProjectMode_ValuelessProperty_Json_EmitsJsonError()
    {
        // Spec L3: under --json a valueless -p must produce a structured JSON error envelope, not
        // just a plain-text/parser error. This is the case the fix targets.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();
        // Widen the test console so Spectre does not word-wrap the (long) JSON error line, which
        // would inject newlines into the string value and make it unparseable.
        TestAnsiConsole.Profile.Width = 1000;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [csproj.FullName, "--json", "-p"]);

        Assert.AreEqual(1, exitCode, "A valueless -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count);
        var output = TestAnsiConsole.Output;
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        Assert.IsTrue(jsonStart >= 0 && jsonEnd > jsonStart, "Output should contain a JSON object");
        var doc = System.Text.Json.JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
        Assert.IsTrue(doc.RootElement.TryGetProperty("Error", out var error), "JSON must carry an 'Error' field");
        StringAssert.Contains(error.GetString(), "without a value", "Error must explain the valueless -p");
    }

    [TestMethod]
    public async Task ProjectMode_RepeatableProperty_Succeeds()
    {
        // The valueless-detection must not regress the supported repeatable -p happy path.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [csproj.FullName, "-p", "WindowsPackageType=None", "-p", "Foo=Bar", "--detach"]);

        Assert.AreEqual(0, exitCode, "Repeatable -p with values must succeed");
        Assert.AreEqual(1, _fakeProjectRunService.BuildAndResolveCalls.Count, "The project must still build");
    }

    [TestMethod]
    public async Task ProjectMode_SemicolonPackedProperty_IsRejectedBeforeBuilding()
    {
        // C29 (Copilot review): MSBuild's /p splits a single token on ';' into MULTIPLE properties, so a
        // packed -p like "Foo=bar;RuntimeIdentifier=win-arm64" would smuggle a dedicated-flag property
        // (RuntimeIdentifier) past the ForwardableProperties filter — which only inspects the name before
        // the FIRST '=' — and override the arch winapp conveys via the RID. It must be rejected up front.
        var csproj = CreateCsproj();
        SetUnpackagedOutcome(csproj, CreateTargetDir(withManifest: false), selfContained: false);
        var command = GetRequiredService<RunCommand>();
        // Widen the console so Spectre does not word-wrap the (long) JSON error line.
        TestAnsiConsole.Profile.Width = 1000;

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, [csproj.FullName, "--json", "-p", "Foo=bar;RuntimeIdentifier=win-arm64"]);

        Assert.AreEqual(1, exitCode, "A ';'-packed -p must fail");
        Assert.AreEqual(0, _fakeProjectRunService.BuildAndResolveCalls.Count, "The pack must be rejected before building");
        var output = TestAnsiConsole.Output;
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        Assert.IsTrue(jsonStart >= 0 && jsonEnd > jsonStart, "Output should contain a JSON object");
        var doc = System.Text.Json.JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
        Assert.IsTrue(doc.RootElement.TryGetProperty("Error", out var error), "JSON must carry an 'Error' field");
        StringAssert.Contains(error.GetString(), "';'", "Error must explain the ';' packing is not allowed");
    }

    #endregion

    #region Folder mode (regression)

    [TestMethod]
    public async Task FolderMode_DelegatesToPipelineWithoutRuntimeHints()
    {
        // Folder mode must pass null runtimeArch/projectFile so behavior is byte-identical to before
        // project mode existed.
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "appxmanifest.xml"), TestManifestContent);
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count);
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutRuntimeCalls.Count);
        Assert.IsNull(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].RuntimeArch, "Folder mode must not pass a runtime arch");
        Assert.IsNull(_fakeMsixService.AddLooseLayoutRuntimeCalls[0].ProjectFile, "Folder mode must not pass a project file");
    }

    #endregion
}
