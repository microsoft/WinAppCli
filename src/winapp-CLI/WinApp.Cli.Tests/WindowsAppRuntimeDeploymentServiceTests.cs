// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class WindowsAppRuntimeDeploymentServiceTests
{
    private DirectoryInfo _root = null!;
    private DirectoryInfo _output = null!;
    private FakeNugetService _nuget = null!;
    private FakeWindowsAppRuntimeService _runtime = null!;
    private WindowsAppRuntimeDeploymentService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"winapp-runtime-deploy-{Guid.NewGuid():N}"));
        _output = _root.CreateSubdirectory("app");
        _nuget = new FakeNugetService { CacheDirectory = _root };
        _runtime = new FakeWindowsAppRuntimeService
        {
            MsixDirectory = _root.CreateSubdirectory("msix"),
            RuntimePackages =
            [
                ("Microsoft.WindowsAppRuntime.2", "2.2.0.0"),
                ("Microsoft.WinAppRuntime.DDLM.2.2", "6000.1.2.0"),
            ],
        };
        _nuget.InstallPackageResult = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = "2.2.0",
            [BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE] = "2.2.0",
            ["Microsoft.WindowsAppSDK.Foundation"] = "2.1.0",
        };
        SeedBootstrap("Microsoft.WindowsAppSDK.Foundation", "2.1.0", "x64");

        _service = new WindowsAppRuntimeDeploymentService(
            _nuget,
            _runtime);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (_root.Exists)
            {
                _root.Delete(recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    [TestMethod]
    public async Task FrameworkDependent_PreflightStagesBootstrapAndReturnsInstallGuidance()
    {
        _runtime.IsRuntimeRegisteredResult = false;
        var bootstrapPath = Path.Combine(_output.FullName, "Microsoft.WindowsAppRuntime.Bootstrap.dll");
        await File.WriteAllTextAsync(bootstrapPath, "stale-bootstrap");

        var result = await _service.PrepareAsync(
            "2.2.0",
            "x64",
            _output,
            install: false,
            NewTaskContext(),
            CancellationToken.None);

        Assert.IsFalse(result.Ready);
        Assert.AreEqual("framework-dependent", result.DeploymentMode);
        Assert.AreEqual("2.2.0", result.RuntimeVersion);
        Assert.IsTrue(File.Exists(result.BootstrapDllPath));
        Assert.AreEqual("bootstrap", await File.ReadAllTextAsync(bootstrapPath));
        StringAssert.Contains(result.Guidance, "--version 2.2.0 --arch x64");
        CollectionAssert.Contains(
            _nuget.InstalledPackages,
            (BuildToolsService.WINAPP_SDK_PACKAGE, "2.2.0"));
        Assert.AreEqual(
            "Microsoft.WinAppRuntime.DDLM.2.2",
            result.RuntimePackages[0].Name,
            "Machine-readable package identities should be sorted deterministically");
        Assert.AreEqual(true, _runtime.LastRequireExactVersion);
        Assert.IsEmpty(_runtime.InstallRuntimeCalls);
    }

    [TestMethod]
    public async Task FrameworkDependent_InstallRechecksExactRuntime()
    {
        _runtime.RuntimeRegisteredResults.Enqueue(false);
        _runtime.RuntimeRegisteredResults.Enqueue(true);
        _runtime.InstallRuntimeResult = (2, 0);

        var result = await _service.PrepareAsync(
            "2.2.0",
            "amd64",
            _output,
            install: true,
            NewTaskContext(),
            CancellationToken.None);

        Assert.IsTrue(result.Ready);
        Assert.IsTrue(result.Installed);
        Assert.AreEqual(2, result.InstalledPackageCount);
        Assert.AreEqual("x64", result.Architecture);
        Assert.HasCount(1, _runtime.InstallRuntimeCalls);
        Assert.AreEqual(2, _runtime.IsRuntimeRegisteredCallCount);
    }

    [TestMethod]
    public async Task VersionRangeIsRejectedBeforeRestore()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _service.PrepareAsync(
            "[2.2.0,3.0.0)",
            "x64",
            _output,
            install: false,
            NewTaskContext(),
            CancellationToken.None));

        Assert.IsEmpty(_nuget.InstalledPackages);
    }

    [TestMethod]
    public async Task InvalidVersionIsRejectedBeforeRestore()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => _service.PrepareAsync(
            "latest",
            "x64",
            _output,
            install: false,
            NewTaskContext(),
            CancellationToken.None));

        Assert.IsEmpty(_nuget.InstalledPackages);
    }

    [TestMethod]
    public async Task FrameworkDependent_MissingRuntimeIdentitiesFailsInsteadOfCheckingAnyInstalledRuntime()
    {
        _runtime.RuntimePackages = [];

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => _service.PrepareAsync(
            "2.2.0",
            "x64",
            _output,
            install: false,
            NewTaskContext(),
            CancellationToken.None));

        StringAssert.Contains(exception.Message, "No framework-dependent runtime package identities");
        Assert.AreEqual(0, _runtime.IsRuntimeRegisteredCallCount);
    }

    private void SeedBootstrap(string package, string version, string arch)
    {
        var path = Path.Combine(
            _nuget.GetNuGetPackageDir(package, version).FullName,
            "runtimes",
            $"win-{arch}",
            "native");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Microsoft.WindowsAppRuntime.Bootstrap.dll"), "bootstrap");
    }

    private static TaskContext NewTaskContext() =>
        new(
            new GroupableTask("runtime-deploy-test", null),
            null,
            new TestConsole(),
            NullLogger<WindowsAppRuntimeDeploymentService>.Instance,
            new Lock());
}
