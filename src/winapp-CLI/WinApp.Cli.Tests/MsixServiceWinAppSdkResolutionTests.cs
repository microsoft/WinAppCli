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

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services.AddSingleton<INugetService, FakeNugetService>();
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
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
