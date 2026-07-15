// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Drives the interactive SDK-install-mode <c>SelectionPrompt</c> in
/// <see cref="WorkspaceSetupService.SetupWorkspaceAsync"/> (AskSdkInstallModeAsync), which is only
/// reached when <c>SdkInstallMode</c> is left unspecified and <c>--use-defaults</c> is not passed.
/// Also covers the .NET short-circuit that skips the prompt when the project already references the
/// Windows App SDK.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceSdkPromptTests : BaseCommandTests
{
    private FakeNugetService _nuget = null!;
    private FakeDotNetService _dotnet = null!;
    private FakePackageRegistrationService _reg = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new FakeNugetService();
        _dotnet = new FakeDotNetService();
        _reg = new FakePackageRegistrationService();
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<INugetService>(_nuget)
            .AddSingleton<IDotNetService>(_dotnet)
            .AddSingleton<IPackageRegistrationService>(_reg);
    }

    private void CreateExistingManifest()
    {
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Package />");
    }

    private static async Task<FileInfo> CreateCsprojAsync(DirectoryInfo directory, string targetFramework)
    {
        var csprojPath = Path.Combine(directory.FullName, "App.csproj");
        await File.WriteAllTextAsync(csprojPath, $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{targetFramework}</TargetFramework>
  </PropertyGroup>
</Project>");
        return new FileInfo(csprojPath);
    }

    /// <summary>Answers "No" to the "Package.appxmanifest already exists. Overwrite?" prompt.</summary>
    private void DeclineManifestOverwrite()
    {
        TestAnsiConsole.Input.PushKey(ConsoleKey.N);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    private WorkspaceSetupOptions InteractiveOptions() => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        UseDefaults = false,
        NoGitignore = true
    };

    [TestMethod]
    public async Task SdkSelectionPrompt_ChoosingDoNotSetup_SkipsInstall()
    {
        // Native project (no csproj); existing manifest so we can decline manifest generation.
        CreateExistingManifest();
        DeclineManifestOverwrite();

        // SDK selection prompt -> navigate to the last choice ("Do not setup ...").
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(InteractiveOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task SdkSelectionPrompt_ChoosingStable_DotNet_InstallsWinAppSdk()
    {
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        _dotnet.PackageListResult = new DotNetPackageListJson([]); // no existing WinApp SDK reference

        DeclineManifestOverwrite();

        // SDK selection prompt -> first choice ("Setup Stable ...") is highlighted; Enter selects it.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // "Add package ...?" prompt (installWinAppPackage) -> Yes.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Y);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(InteractiveOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(
            _dotnet.AddedPackages.Any(p => p.PackageName == DotNetService.WINAPP_SDK_NUGET_PACKAGE),
            "Choosing Stable should add the Windows App SDK package reference.");
    }

    [TestMethod]
    public async Task SdkSelectionPrompt_DotNet_AlreadyReferencesWinAppSdk_SkipsPrompt()
    {
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();

        // Report that the project already references the Windows App SDK so the prompt is skipped
        // (SdkInstallMode defaults to None) and no SelectionPrompt input is required.
        _dotnet.PackageListResult = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework(
                    "net10.0-windows10.0.26100.0",
                    [new DotNetPackage(DotNetService.WINAPP_SDK_NUGET_PACKAGE, "1.6.0", "1.6.0")],
                    [])
            ])
        ]);

        DeclineManifestOverwrite();

        // "Add package ...?" prompt (installWinAppPackage) -> No; the SDK selection prompt is skipped.
        TestAnsiConsole.Input.PushKey(ConsoleKey.N);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(InteractiveOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        // Prompt skipped -> SDK install mode is None -> no new WinApp SDK package added.
        Assert.IsFalse(
            _dotnet.AddedPackages.Any(p => p.PackageName == DotNetService.WINAPP_SDK_NUGET_PACKAGE),
            "When the project already references the Windows App SDK, no package should be added.");
    }

    [TestMethod]
    public async Task MultipleCsproj_PromptsForSelection()
    {
        // Two .csproj files trigger the "Multiple .csproj files found" SelectionPrompt.
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0"); // App.csproj
        var second = Path.Combine(_tempDirectory.FullName, "Other.csproj");
        await File.WriteAllTextAsync(second, @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  </PropertyGroup>
</Project>");
        CreateExistingManifest();

        // Select the first project (highlighted) with Enter.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true, // avoids every prompt except the csproj selection
            NoGitignore = true
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task SdkSelectionPrompt_ChoosingPreview_DotNet_InstallsWinAppSdk()
    {
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);

        DeclineManifestOverwrite();

        // SDK selection prompt -> second choice ("Setup Preview ...").
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // "Add package ...?" prompt -> Yes.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Y);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(InteractiveOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(
            _dotnet.AddedPackages.Any(p => p.PackageName == DotNetService.WINAPP_SDK_NUGET_PACKAGE),
            "Choosing Preview should add the Windows App SDK package reference.");
    }

    [TestMethod]
    public async Task SdkSelectionPrompt_ChoosingExperimental_DotNet_InstallsWinAppSdk()
    {
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);

        DeclineManifestOverwrite();

        // SDK selection prompt -> third choice ("Setup Experimental ...").
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        // "Add package ...?" prompt -> Yes.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Y);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(InteractiveOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(
            _dotnet.AddedPackages.Any(p => p.PackageName == DotNetService.WINAPP_SDK_NUGET_PACKAGE),
            "Choosing Experimental should add the Windows App SDK package reference.");
    }

    [TestMethod]
    public async Task SdkSelectionPrompt_VersionFetchFails_StillShowsPromptAndCompletes()
    {
        // A native project whose SDK-version lookups all fail: SafeGetLatestVersionAsync should
        // swallow the errors so the selection prompt still renders (without version labels).
        CreateExistingManifest();
        _nuget.PackagesToThrow.Add(BuildToolsService.CPP_SDK_PACKAGE);
        _nuget.PackagesToThrow.Add(BuildToolsService.WINAPP_SDK_PACKAGE);

        DeclineManifestOverwrite();

        // SDK selection prompt -> last choice ("Do not setup ...") so no install is attempted.
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.DownArrow);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(InteractiveOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
    }
}
