// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Drives interactive confirmation prompts in <see cref="WorkspaceSetupService.SetupWorkspaceAsync"/>:
/// the "winapp.yaml exists with pinned versions. Overwrite?" prompt (existing-config path) and the
/// "Configuring developer mode" sub-task (enable succeeds / returns -1 / throws).
/// </summary>
[TestClass]
public class WorkspaceSetupServiceInteractiveTests : BaseCommandTests
{
    private FakeNugetService _nuget = null!;
    private FakeDotNetService _dotnet = null!;
    private FakePackageRegistrationService _reg = null!;
    private FakeDevModeService _devMode = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _nuget = new FakeNugetService();
        _dotnet = new FakeDotNetService();
        _reg = new FakePackageRegistrationService();
        _devMode = new FakeDevModeService();
        return services
            .AddSingleton<IDevModeService>(_devMode)
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

    private async Task CreateExistingConfigAsync()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.6.0\n");
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

    private void PushConfirm(bool yes)
    {
        TestAnsiConsole.Input.PushKey(yes ? ConsoleKey.Y : ConsoleKey.N);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    #region Existing-config overwrite prompt

    [TestMethod]
    public async Task ExistingConfig_OverwritePrompt_Yes_Proceeds()
    {
        // .NET project with a pinned winapp.yaml + explicit Stable mode, interactive.
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        await CreateExistingConfigAsync();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);
        _devMode.IsEnabledResult = true; // avoid the dev-mode prompt

        PushConfirm(true);  // "winapp.yaml exists... Overwrite?" -> Yes
        PushConfirm(false); // "Package.appxmanifest already exists. Overwrite?" -> No
        PushConfirm(true);  // "Add package ...?" -> Yes

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = false,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.Stable
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        // Overwrite=Yes regenerates the workspace from fresh SDK versions, so the pinned winapp.yaml
        // is NOT preserved: IgnoreConfig stays false and AskSdkInstallModeAsync is re-run.
        Assert.IsFalse(
            options.IgnoreConfig,
            "Answering Yes to the overwrite prompt should regenerate config, leaving IgnoreConfig false.");
    }

    [TestMethod]
    public async Task ExistingConfig_OverwritePrompt_No_KeepsPinnedVersions()
    {
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        await CreateExistingConfigAsync();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);
        _devMode.IsEnabledResult = true;

        PushConfirm(false); // "winapp.yaml exists... Overwrite?" -> No (keep pinned versions)
        PushConfirm(false); // "Package.appxmanifest already exists. Overwrite?" -> No
        PushConfirm(true);  // "Add package ...?" -> Yes

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = false,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.Stable
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        // Overwrite=No keeps the pinned winapp.yaml versions by skipping config regeneration, which
        // the product signals by setting IgnoreConfig=true on the options.
        Assert.IsTrue(
            options.IgnoreConfig,
            "Answering No to the overwrite prompt should keep the pinned versions (IgnoreConfig=true).");
    }

    #endregion

    #region Existing-config UseDefaults (non-interactive overwrite)

    [TestMethod]
    public async Task ExistingConfig_UseDefaults_IgnoresPinnedVersionsAndReinstalls()
    {
        // With --use-defaults and a pinned winapp.yaml, the overwrite prompt is skipped and the
        // config is ignored (fresh versions reinstalled) without any interactive input.
        await CreateCsprojAsync(_tempDirectory, "net10.0-windows10.0.26100.0");
        CreateExistingManifest();
        await CreateExistingConfigAsync();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);
        _devMode.IsEnabledResult = false; // exercises the --use-defaults dev-mode short-circuit

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.Stable
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.IsTrue(options.IgnoreConfig, "--use-defaults with a pinned config should set IgnoreConfig.");
        Assert.IsTrue(
            _dotnet.AddedPackages.Any(p => p.PackageName == DotNetService.WINAPP_SDK_NUGET_PACKAGE),
            "The Windows App SDK package should still be added.");
    }

    #endregion

    #region Developer-mode sub-task

    private WorkspaceSetupOptions NativeNoInstallOptions() => new()
    {
        BaseDirectory = _tempDirectory,
        ConfigDir = _tempDirectory,
        UseDefaults = false,
        NoGitignore = true,
        SdkInstallMode = SdkInstallMode.None
    };

    [TestMethod]
    public async Task DevMode_EnablePrompt_Yes_EnableReturnsNonStandardCode()
    {
        // A non-(-1)/non-3010 exit code is treated as "enabled" but logs the raw code.
        CreateExistingManifest();
        _devMode.IsEnabledResult = false;
        _devMode.EnsureResult = 5;

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(true);  // "Enable Developer Mode..." -> Yes

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(NativeNoInstallOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, _devMode.EnsureCallCount);
    }

    [TestMethod]
    public async Task DevMode_AlreadyEnabledBySubTask_SkipsEnable()
    {
        // Developer Mode is reported disabled at prompt time but enabled by the time the sub-task
        // runs (e.g. enabled out-of-band) -> the sub-task short-circuits without calling Ensure.
        CreateExistingManifest();
        _devMode.IsEnabledSequence = new Queue<bool>([false, true]);

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(true);  // "Enable Developer Mode..." -> Yes

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(NativeNoInstallOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.AreEqual(0, _devMode.EnsureCallCount, "Ensure should be skipped when already enabled.");
    }

    [TestMethod]
    public async Task DevMode_EnablePrompt_Yes_EnableSucceeds()
    {
        // Native project, SDK install skipped, dev mode not yet enabled -> user opts in and it succeeds.
        CreateExistingManifest();
        _devMode.IsEnabledResult = false;
        _devMode.EnsureResult = 0;

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(true);  // "Enable Developer Mode..." -> Yes

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(NativeNoInstallOptions(), TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        Assert.AreEqual(1, _devMode.EnsureCallCount, "Developer Mode enable should have been attempted.");
    }

    [TestMethod]
    public async Task DevMode_EnablePrompt_Yes_EnableReturnsMinusOne()
    {
        CreateExistingManifest();
        _devMode.IsEnabledResult = false;
        _devMode.EnsureResult = -1;

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(true);  // "Enable Developer Mode..." -> Yes

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(NativeNoInstallOptions(), TestContext.CancellationToken);

        Assert.AreEqual(1, _devMode.EnsureCallCount);
    }

    [TestMethod]
    public async Task DevMode_EnablePrompt_Yes_EnableThrows_IsHandled()
    {
        CreateExistingManifest();
        _devMode.IsEnabledResult = false;
        _devMode.EnsureThrows = new InvalidOperationException("boom");

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(true);  // "Enable Developer Mode..." -> Yes

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(NativeNoInstallOptions(), TestContext.CancellationToken);

        // The sub-task catches the exception; setup should not throw out of SetupWorkspaceAsync.
        Assert.AreEqual(1, _devMode.EnsureCallCount);
    }

    #endregion

    #region .NET TargetFramework update prompt (interactive)

    [TestMethod]
    public async Task Tfm_UpdatePrompt_Yes_UpdatesTargetFramework()
    {
        var csproj = await CreateCsprojAsync(_tempDirectory, "net8.0"); // unsupported (no -windows)
        CreateExistingManifest();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);
        _devMode.IsEnabledResult = true;

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(true);  // "Update TargetFramework to ...?" -> Yes
        PushConfirm(true);  // "Add package ...?" -> Yes

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = false,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.Stable
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var updated = await File.ReadAllTextAsync(csproj.FullName);
        Assert.Contains("-windows", updated);
    }

    [TestMethod]
    public async Task Tfm_UpdatePrompt_No_WithSdkInstall_Errors()
    {
        await CreateCsprojAsync(_tempDirectory, "net8.0");
        CreateExistingManifest();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);
        _devMode.IsEnabledResult = true;

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(false); // "Update TargetFramework to ...?" -> No

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = false,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.Stable
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreNotEqual(0, result, "Declining a required TFM update should abort setup.");
    }

    [TestMethod]
    public async Task Tfm_UpdatePrompt_No_WithoutSdkInstall_SkipsUpdate()
    {
        var csproj = await CreateCsprojAsync(_tempDirectory, "net8.0");
        CreateExistingManifest();
        _dotnet.PackageListResult = new DotNetPackageListJson([]);
        _devMode.IsEnabledResult = true;

        PushConfirm(false); // manifest overwrite -> No
        PushConfirm(false); // "Update TargetFramework to ...?" -> No (not required, SDK install skipped)
        PushConfirm(false); // "Add package ...?" -> No

        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = false,
            NoGitignore = true,
            SdkInstallMode = SdkInstallMode.None
        };

        var service = GetRequiredService<IWorkspaceSetupService>();
        var result = await service.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        Assert.AreEqual(0, result);
        var content = await File.ReadAllTextAsync(csproj.FullName);
        Assert.Contains(">net8.0<", content, "TFM should be left unchanged when the update is declined and not required.");
    }

    #endregion
}
