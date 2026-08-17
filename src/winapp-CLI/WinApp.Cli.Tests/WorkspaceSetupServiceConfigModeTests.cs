// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="WorkspaceSetupService.SetupWorkspaceAsync"/> config-only and restore
/// (RequireExistingConfig) modes: creating a config with prerelease/preview package modes,
/// tolerating version-query failures, and the "nothing to restore" / missing-yaml guards.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceConfigModeTests : BaseCommandTests
{
    private FakeNugetService _nuget = null!;
    private FakeDotNetService _dotnet = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new FakeNugetService();
        _dotnet = new FakeDotNetService();

        return services
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<INugetService>(_nuget)
            .AddSingleton<IDotNetService>(_dotnet);
    }

    private WorkspaceSetupOptions ConfigOnlyOptions(SdkInstallMode mode) => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        UseDefaults = true,
        ConfigOnly = true,
        NoGitignore = true,
        SdkInstallMode = mode
    };

    private static async Task<FileInfo> CreateCsprojAsync(DirectoryInfo directory, string projectName)
    {
        var csprojPath = Path.Combine(directory.FullName, $"{projectName}.csproj");
        await File.WriteAllTextAsync(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  </PropertyGroup>
</Project>");
        return new FileInfo(csprojPath);
    }

    [TestMethod]
    public async Task ConfigOnly_ExperimentalMode_CreatesConfigFile()
    {
        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Experimental), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(File.Exists(Path.Combine(_tempDirectory.FullName, "winapp.yaml")));
    }

    [TestMethod]
    public async Task ConfigOnly_PreviewMode_CreatesConfigFile()
    {
        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Preview), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(File.Exists(Path.Combine(_tempDirectory.FullName, "winapp.yaml")));
    }

    [TestMethod]
    public async Task ConfigOnly_ToleratesVersionQueryFailure()
    {
        // Make every SDK package version query fail; config creation should still succeed.
        foreach (var pkg in NugetService.SDK_PACKAGES)
        {
            _nuget.PackagesToThrow.Add(pkg);
        }

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Stable), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    /// <summary>
    /// `winapp init` on a .NET project records the SDK package versions as PackageReferences in the .csproj
    /// rather than in a winapp.yaml, so a follow-up `winapp restore` finds no yaml. That must not be an error:
    /// restore delegates to `dotnet restore`, which is what actually restores those PackageReferences.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WithoutYaml_DelegatesToDotnetRestore()
    {
        await CreateCsprojAsync(_tempDirectory, "App");

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }
}
