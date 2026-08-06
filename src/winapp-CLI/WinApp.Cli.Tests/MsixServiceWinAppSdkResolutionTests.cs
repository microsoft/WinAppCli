// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class MsixServiceWinAppSdkResolutionTests : BaseCommandTests
{
    private const string TestFramework = "net9.0-windows10.0.26100.0";

    private MsixService _msixService = null!;
    private FakeNugetService _nuget = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services.AddSingleton<INugetService, FakeNugetService>();
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
        _nuget = (FakeNugetService)GetRequiredService<INugetService>();
    }

    // Helper to build a DotNetPackageListJson with Microsoft.WindowsAppSDK at the given version.
    private static DotNetPackageListJson BuildCsprojPackageList(string sdkVersion)
    {
        return new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(
                    TestFramework,
                    [new DotNetPackage(BuildToolsService.WINAPP_SDK_PACKAGE, sdkVersion, sdkVersion)],
                    []
                )
            ])
        ]);
    }

    // Helper to build a DotNetPackageListJson with no Microsoft.WindowsAppSDK entry.
    private static DotNetPackageListJson BuildCsprojPackageListWithoutSdk()
    {
        return new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(
                    TestFramework,
                    [new DotNetPackage("SomeOther.Package", "1.0.0", "1.0.0")],
                    []
                )
            ])
        ]);
    }

    // Helper to build a DotNetPackageListJson with Microsoft.WindowsAppSDK (top-level) plus the
    // separate Microsoft.WindowsAppSDK.Runtime package as a restored transitive dependency (1.8+ layout).
    private static DotNetPackageListJson BuildCsprojPackageListWithRuntime(string sdkVersion, string runtimeVersion)
    {
        return new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(
                    TestFramework,
                    [new DotNetPackage(BuildToolsService.WINAPP_SDK_PACKAGE, sdkVersion, sdkVersion)],
                    [new DotNetPackage(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, runtimeVersion, runtimeVersion)]
                )
            ])
        ]);
    }

    // Helper: framework-dependent app that pulls in the Windows App SDK via the runtime and other
    // sub-packages transitively, WITHOUT the meta Microsoft.WindowsAppSDK package.
    private static DotNetPackageListJson BuildCsprojPackageListRuntimeWithoutMeta(string runtimeVersion)
    {
        return new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(
                    TestFramework,
                    [],
                    [
                        new DotNetPackage("Microsoft.WindowsAppSDK.WinUI", runtimeVersion, runtimeVersion),
                        new DotNetPackage(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, runtimeVersion, runtimeVersion)
                    ]
                )
            ])
        ]);
    }

    #region GetWinAppSDKPackageDependenciesAsync: resolution priority tests

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_BothCsprojAndYamlHaveSdk_ResolvesCsprojVersionFirst()
    {
        // Arrange: yaml has 1.6.0, csproj has 1.7.250401001 — csproj must win
        var yamlConfig = new WinappConfig();
        yamlConfig.SetVersion(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        _configService.Save(yamlConfig);

        var csprojPackageList = BuildCsprojPackageList("1.7.250401001");

        // Act
        var (_, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("1.7.250401001", mainVersion, "csproj version should take priority over winapp.yaml");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_CsprojNullAndYamlHasSdk_FallsBackToYaml()
    {
        // Arrange: no csproj package list, yaml has 1.6.0
        var yamlConfig = new WinappConfig();
        yamlConfig.SetVersion(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        _configService.Save(yamlConfig);

        // Act
        var (_, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            dotNetPackageList: null, TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("1.6.0", mainVersion, "Should fall back to winapp.yaml when no .csproj package list is provided");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_CsprojLacksSdkAndYamlHasSdk_FallsBackToYaml()
    {
        // Arrange: csproj has packages but not Microsoft.WindowsAppSDK; yaml has 1.6.0
        var yamlConfig = new WinappConfig();
        yamlConfig.SetVersion(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        _configService.Save(yamlConfig);

        var csprojPackageList = BuildCsprojPackageListWithoutSdk();

        // Act
        var (_, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("1.6.0", mainVersion, "Should fall back to winapp.yaml when .csproj package list does not contain Microsoft.WindowsAppSDK");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_NeitherCsprojNorYamlHasSdk_ReturnsNull()
    {
        // Arrange: no yaml, no sdk in csproj — both sources fail
        var csprojPackageList = BuildCsprojPackageListWithoutSdk();

        // Act
        var (cachedPackages, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert: both return values are null when no source provides the SDK version
        Assert.IsNull(cachedPackages, "Should return null packages when no source has Microsoft.WindowsAppSDK");
        Assert.IsNull(mainVersion, "Should return null version when no source has Microsoft.WindowsAppSDK");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_BothNullAndNoYaml_ReturnsNull()
    {
        // Arrange: no csproj package list, no yaml at all

        // Act
        var (cachedPackages, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            dotNetPackageList: null, TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(cachedPackages, "Should return null packages when neither .csproj nor winapp.yaml has Microsoft.WindowsAppSDK");
        Assert.IsNull(mainVersion, "Should return null version when neither .csproj nor winapp.yaml has Microsoft.WindowsAppSDK");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_RuntimeInTransitiveList_ResolvesLocallyWithoutNetwork()
    {
        // Arrange: 1.8+ layout — main package top-level, runtime package restored as a transitive
        // dependency. Simulate an offline NuGet source so any network probe would fail.
        var csprojPackageList = BuildCsprojPackageListWithRuntime("1.8.250401001", "1.8.250401001");
        _nuget.ThrowOnGetPackageDependencies = true;

        // Act
        var (cachedPackages, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert: resolved entirely from the restored list, no network round-trip.
        Assert.AreEqual(0, _nuget.GetPackageDependenciesCallCount, "The network dependency lookup must be skipped when the runtime package is already in the restored list");
        Assert.IsNotNull(cachedPackages, "Should resolve packages locally even when NuGet is offline");
        Assert.AreEqual("1.8.250401001", mainVersion);
        Assert.IsTrue(cachedPackages!.ContainsKey(BuildToolsService.WINAPP_SDK_PACKAGE), "Main package should be present");
        Assert.IsTrue(cachedPackages.ContainsKey(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE), "Runtime package should be resolved from the transitive list");
        Assert.AreEqual("1.8.250401001", cachedPackages[BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE], "Runtime version should be the exact restored version on disk");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_RuntimeNotInList_FallsBackToNetworkLookup()
    {
        // Arrange: only the main package is present (e.g. Windows App SDK 1.7 and earlier, where the
        // runtime ships inside the main package and there is no separate transitive runtime package).
        var csprojPackageList = BuildCsprojPackageList("1.7.250401001");

        // Act
        _ = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert: the network dependency lookup is still consulted for this case.
        Assert.AreEqual(1, _nuget.GetPackageDependenciesCallCount, "Should fall back to the network dependency lookup when no separate runtime package is in the list");
    }

    [TestMethod]
    public async Task GetWinAppSDKPackageDependenciesAsync_RuntimeWithoutMetaPackage_ResolvesFromRuntimeVersion()
    {
        // Arrange: framework-dependent app that pulls in the Windows App SDK via the runtime (and
        // other sub-packages) transitively but never the meta Microsoft.WindowsAppSDK package — a
        // valid, buildable config that previously failed runtime resolution. Simulate offline NuGet.
        var csprojPackageList = BuildCsprojPackageListRuntimeWithoutMeta("1.8.250916003");
        _nuget.ThrowOnGetPackageDependencies = true;

        // Act
        var (cachedPackages, mainVersion) = await _msixService.GetWinAppSDKPackageDependenciesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert: resolves from the runtime package even though the meta package is absent.
        Assert.AreEqual(0, _nuget.GetPackageDependenciesCallCount, "Runtime resolution must not require a network round-trip");
        Assert.IsNotNull(cachedPackages, "Should resolve the runtime locally when the meta package is absent");
        Assert.IsTrue(cachedPackages!.ContainsKey(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE), "Runtime package should be present");
        Assert.AreEqual("1.8.250916003", cachedPackages[BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE]);
        Assert.IsFalse(cachedPackages.ContainsKey(BuildToolsService.WINAPP_SDK_PACKAGE), "Meta package should not be fabricated when it is not in the graph");
        Assert.AreEqual("1.8.250916003", mainVersion, "Main version should fall back to the runtime version when the meta package is absent");
    }

    #endregion

    #region GetAllUserPackagesAsync: resolution priority tests

    [TestMethod]
    public async Task GetAllUserPackagesAsync_CsprojHasPackages_ReturnsCsprojPackages()
    {
        // Arrange: yaml also has a package, but csproj should win since it has content
        var yamlConfig = new WinappConfig();
        yamlConfig.SetVersion("SomeYamlOnlyPackage", "9.0.0");
        _configService.Save(yamlConfig);

        var csprojPackageList = BuildCsprojPackageList("1.7.250401001");

        // Act
        var packages = await _msixService.GetAllUserPackagesAsync(
            csprojPackageList, TestTaskContext, TestContext.CancellationToken);

        // Assert: should return csproj packages
        Assert.IsTrue(packages.ContainsKey(BuildToolsService.WINAPP_SDK_PACKAGE), "csproj package should be present");
        // yaml packages should NOT be present since csproj had entries
        Assert.IsFalse(packages.ContainsKey("SomeYamlOnlyPackage"), "yaml-only package should not be returned when csproj has packages");
    }

    [TestMethod]
    public async Task GetAllUserPackagesAsync_CsprojNullAndYamlExists_ReturnsYamlPackages()
    {
        // Arrange: no csproj list, yaml has a package
        var yamlConfig = new WinappConfig();
        yamlConfig.SetVersion(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        _configService.Save(yamlConfig);

        // Act
        var packages = await _msixService.GetAllUserPackagesAsync(
            dotNetPackageList: null, TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(packages.ContainsKey(BuildToolsService.WINAPP_SDK_PACKAGE), "yaml package should be present when no csproj list provided");
        Assert.AreEqual("1.6.0", packages[BuildToolsService.WINAPP_SDK_PACKAGE]);
    }

    [TestMethod]
    public async Task GetAllUserPackagesAsync_CsprojEmptyAndYamlExists_FallsBackToYaml()
    {
        // Arrange: csproj list has no packages (empty frameworks), yaml has packages
        var yamlConfig = new WinappConfig();
        yamlConfig.SetVersion(BuildToolsService.WINAPP_SDK_PACKAGE, "1.6.0");
        _configService.Save(yamlConfig);

        var emptyCsprojList = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(TestFramework, [], [])
            ])
        ]);

        // Act
        var packages = await _msixService.GetAllUserPackagesAsync(
            emptyCsprojList, TestTaskContext, TestContext.CancellationToken);

        // Assert: falls back to yaml since csproj produced no packages
        Assert.IsTrue(packages.ContainsKey(BuildToolsService.WINAPP_SDK_PACKAGE), "Should fall back to yaml when csproj is empty");
    }

    #endregion
}
