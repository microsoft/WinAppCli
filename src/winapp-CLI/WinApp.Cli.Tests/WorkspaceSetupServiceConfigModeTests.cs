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
        var csprojPath = Path.Join(directory.FullName, $"{projectName}.csproj");
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
        Assert.IsTrue(File.Exists(Path.Join(_tempDirectory.FullName, "winapp.yaml")));
    }

    [TestMethod]
    public async Task ConfigOnly_PreviewMode_CreatesConfigFile()
    {
        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(ConfigOnlyOptions(SdkInstallMode.Preview), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(File.Exists(Path.Join(_tempDirectory.FullName, "winapp.yaml")));
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
        var csproj = await CreateCsprojAsync(_tempDirectory, "App");

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
        // A zero exit code alone would also be produced by the "nothing to restore" no-op branch, so assert the
        // delegation itself: exactly one dotnet restore, naming this project.
        Assert.HasCount(1, _dotnet.InheritedCalls);
        StringAssert.Contains(_dotnet.InheritedCalls[0], "restore", StringComparison.Ordinal);
        StringAssert.Contains(_dotnet.InheritedCalls[0], csproj.FullName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restore runs non-interactively (CI, or straight after a clone) and has no project-selection option, so
    /// a directory with several projects must restore all of them rather than opening init's picker, which
    /// would block on redirected input.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetMultiProject_WithoutYaml_RestoresEveryProjectWithoutPrompting()
    {
        var first = await CreateCsprojAsync(_tempDirectory, "AppOne");
        var second = await CreateCsprojAsync(_tempDirectory, "AppTwo");

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
        Assert.HasCount(2, _dotnet.InheritedCalls);
        Assert.IsTrue(
            _dotnet.InheritedCalls.Any(c => c.Contains(first.FullName, StringComparison.Ordinal)),
            $"Expected a restore for {first.Name}. Calls: {string.Join(" | ", _dotnet.InheritedCalls)}");
        Assert.IsTrue(
            _dotnet.InheritedCalls.Any(c => c.Contains(second.FullName, StringComparison.Ordinal)),
            $"Expected a restore for {second.Name}. Calls: {string.Join(" | ", _dotnet.InheritedCalls)}");
    }

    /// <summary>
    /// `restore --config-dir <dir>` selects the NuGet configuration for the native path, so the delegated
    /// .NET restore must use it too — otherwise a .NET project would resolve packages from different feeds
    /// than a native one given the same option.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WithSeparateConfigDir_PassesThatConfigToDotnetRestore()
    {
        await CreateCsprojAsync(_tempDirectory, "App");

        var configDir = _tempDirectory.CreateSubdirectory("nugetconfig");
        var configFile = Path.Join(configDir.FullName, "nuget.config");
        await File.WriteAllTextAsync(
            configFile,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = configDir,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.HasCount(1, _dotnet.InheritedCalls);
        StringAssert.Contains(_dotnet.InheritedCalls[0], "--configfile", StringComparison.Ordinal);
        StringAssert.Contains(_dotnet.InheritedCalls[0], configFile, StringComparison.Ordinal);
    }

    /// <summary>
    /// In the default case the config directory IS the project directory, so nothing is passed and dotnet's
    /// normal nuget.config discovery — including the user and machine levels — is left intact. Passing
    /// --configfile there would replace that hierarchy with a single file.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WithDefaultConfigDir_DoesNotOverrideNugetDiscovery()
    {
        await CreateCsprojAsync(_tempDirectory, "App");
        await File.WriteAllTextAsync(
            Path.Join(_tempDirectory.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """);

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
        Assert.HasCount(1, _dotnet.InheritedCalls);
        Assert.DoesNotContain("--configfile", _dotnet.InheritedCalls[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing `dotnet restore` must surface as a non-zero exit rather than being reported as a success.
    /// </summary>
    [TestMethod]
    public async Task Restore_DotNetProject_WhenDotnetRestoreFails_ReturnsError()
    {
        await CreateCsprojAsync(_tempDirectory, "App");
        _dotnet.RunDotnetInheritedHandler = _ => 1;

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreNotEqual(0, result);
    }
}
