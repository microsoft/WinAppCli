// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for WorkspaceSetupService SDK installation mode handling
/// </summary>
[TestClass]
public class WorkspaceSetupServiceTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IPowerShellService, FakePowerShellService>();
    }

    #region Helper methods

    /// <summary>
    /// Creates a minimal .csproj file in the specified directory with the given TargetFramework.
    /// </summary>
    private static async Task<FileInfo> CreateCsprojAsync(DirectoryInfo directory, string projectName, string targetFramework)
    {
        var csprojPath = Path.Combine(directory.FullName, $"{projectName}.csproj");
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{targetFramework}</TargetFramework>
  </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, content);
        return new FileInfo(csprojPath);
    }

    /// <summary>
    /// Creates a minimal .csproj file with multiple TargetFrameworks (multi-targeting).
    /// </summary>
    private static async Task<FileInfo> CreateMultiTargetCsprojAsync(DirectoryInfo directory, string projectName)
    {
        var csprojPath = Path.Combine(directory.FullName, $"{projectName}.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net10.0-windows10.0.26100.0</TargetFrameworks>
  </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, content);
        return new FileInfo(csprojPath);
    }

    #endregion

    [TestMethod]
    public async Task SetupWorkspace_WithSdkInstallModeNone_CompletesWithoutPackageInstallation()
    {
        // Arrange
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            ForceLatestBuildTools = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // When SdkInstallMode is None, the command completes early without creating
        // the packages directory or saving config (by design - skips SDK installation)
    }

    [TestMethod]
    public async Task SetupWorkspace_WithConfigOnly_CreatesConfigFile()
    {
        // Arrange
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            ConfigOnly = true,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify config was created
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");
    }

    [TestMethod]
    public async Task SetupWorkspace_ConfigOnly_ExistingConfigValidated()
    {
        // Arrange - Create existing config
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 10.0.26100.1
");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            ConfigOnly = true,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify existing config was preserved (not overwritten)
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("10.0.26100.1", configContent, "Existing config version should be preserved");
    }

    [TestMethod]
    public async Task SetupWorkspace_DoesNotUpdateGitignore_WhenNoGitignoreIsTrue()
    {
        // Arrange
        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "# Original content\n*.log");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            ConfigOnly = true, // Use config-only to avoid long-running operations
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true // Should NOT update .gitignore
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify .gitignore was NOT updated
        var gitignoreContent = await File.ReadAllTextAsync(gitignorePath);
        Assert.DoesNotContain(".winapp", gitignoreContent, ".gitignore should not contain .winapp when NoGitignore is true");
    }

    [TestMethod]
    public async Task SetupWorkspace_WithRequireExistingConfig_FailsWhenConfigMissing()
    {
        // Arrange - Don't create any config file
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true, // This should fail when config doesn't exist
            UseDefaults = true,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Setup should fail when config is required but missing");
    }
}

/// <summary>
/// End-to-end tests for the merged .NET and native workspace setup code paths.
/// These tests verify that the unified WorkspaceSetupService correctly handles
/// both .NET (csproj) and native (C++) projects through the shared flow,
/// including the key fix: Windows App SDK Runtime installation on the .NET path.
/// </summary>
[TestClass]
public class WorkspaceSetupServiceMergedPathTests : BaseCommandTests
{
    private FakeNugetService _fakeNugetService = null!;
    private FakeDotNetService _fakeDotNetService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeNugetService = new FakeNugetService();
        _fakeDotNetService = new FakeDotNetService();

        return services
            .AddSingleton<IPowerShellService, FakePowerShellService>()
            .AddSingleton<IDevModeService, FakeDevModeService>()
            .AddSingleton<INugetService>(_fakeNugetService)
            .AddSingleton<IDotNetService>(_fakeDotNetService);
    }

    #region Helper methods

    private static async Task<FileInfo> CreateCsprojAsync(DirectoryInfo directory, string projectName, string targetFramework)
    {
        var csprojPath = Path.Combine(directory.FullName, $"{projectName}.csproj");
        var content = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>{targetFramework}</TargetFramework>
  </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, content);
        return new FileInfo(csprojPath);
    }

    private static async Task<FileInfo> CreateMultiTargetCsprojAsync(DirectoryInfo directory, string projectName)
    {
        var csprojPath = Path.Combine(directory.FullName, $"{projectName}.csproj");
        var content = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net10.0-windows10.0.26100.0</TargetFrameworks>
  </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(csprojPath, content);
        return new FileInfo(csprojPath);
    }

    #endregion

    #region .NET project detection tests

    [TestMethod]
    public async Task SetupWorkspace_DetectsDotNetProject_WhenCsprojExists()
    {
        // Arrange - Create a .csproj in the temp directory
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify .NET project was detected by checking that it went through
        // the .NET-specific NuGet package addition path
        Assert.IsNotEmpty(_fakeDotNetService.AddedPackages,
            "Should have added NuGet packages, confirming .NET project was detected");
    }

    [TestMethod]
    public async Task SetupWorkspace_DetectsNativeProject_WhenNoCsprojExists()
    {
        // Arrange - No .csproj in temp directory
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify that no .NET-specific operations were performed
        Assert.IsEmpty(_fakeDotNetService.AddedPackages,
            "No NuGet packages should be added via dotnet CLI for native projects");
    }

    #endregion

    #region .NET TFM validation tests

    [TestMethod]
    public async Task SetupWorkspace_DotNet_UpdatesUnsupportedTargetFramework()
    {
        // Arrange - Create a .csproj with an unsupported TFM (no -windows)
        var csproj = await CreateCsprojAsync(_tempDirectory, "TestApp", "net8.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify TFM was updated in the csproj file
        var updatedContent = await File.ReadAllTextAsync(csproj.FullName);
        Assert.Contains("-windows", updatedContent, "TFM should be updated to include -windows for Windows App SDK support");
        Assert.DoesNotContain(">net8.0<", updatedContent, "Original unsupported TFM should be replaced");
    }

    [TestMethod]
    public async Task SetupWorkspace_DotNet_PreservesValidTargetFramework()
    {
        // Arrange - Create a .csproj with a valid TFM
        var validTfm = "net10.0-windows10.0.26100.0";
        var csproj = await CreateCsprojAsync(_tempDirectory, "TestApp", validTfm);

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify the TFM was not changed
        var updatedContent = await File.ReadAllTextAsync(csproj.FullName);
        Assert.Contains(validTfm, updatedContent, "Valid TFM should be preserved unchanged");
    }

    [TestMethod]
    public async Task SetupWorkspace_DotNet_FailsForMultiTargetedProject()
    {
        // Arrange - Create a multi-targeted .csproj
        await CreateMultiTargetCsprojAsync(_tempDirectory, "TestApp");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Setup should fail for multi-targeted projects");

        // Error messages go to stderr via the TextWriterLogger
        var errorOutput = ConsoleStdErr.ToString();
        Assert.Contains("multi-target", errorOutput, "Error should mention multi-targeting");
    }

    #endregion

    #region .NET NuGet package management tests

    [TestMethod]
    public async Task SetupWorkspace_DotNet_AddsCorrectNuGetPackages()
    {
        // Arrange - Create a .csproj with valid TFM
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify that the correct NuGet packages were queried
        Assert.Contains(BuildToolsService.BUILD_TOOLS_PACKAGE, _fakeNugetService.QueriedPackages,
            "Should query for BuildTools package version");
        Assert.Contains(DotNetService.WINAPP_SDK_NUGET_PACKAGE, _fakeNugetService.QueriedPackages,
            "Should query for WindowsAppSDK package version");

        // Verify that the correct NuGet packages were added to the project
        Assert.HasCount(2, _fakeDotNetService.AddedPackages, "Should add exactly 2 NuGet packages");

        var addedNames = _fakeDotNetService.AddedPackages.Select(p => p.PackageName).ToList();
        Assert.Contains(BuildToolsService.BUILD_TOOLS_PACKAGE, addedNames,
            "Should add BuildTools as PackageReference");
        Assert.Contains(DotNetService.WINAPP_SDK_NUGET_PACKAGE, addedNames,
            "Should add WindowsAppSDK as PackageReference");

        // Verify the version used matches what the fake NuGet service returned
        foreach (var (_, _, version) in _fakeDotNetService.AddedPackages)
        {
            Assert.AreEqual(_fakeNugetService.DefaultVersion, version,
                "Package version should match the version from NuGet service");
        }
    }

    [TestMethod]
    public async Task SetupWorkspace_Native_DoesNotAddNuGetPackageReferences()
    {
        // Arrange - No .csproj in temp directory (native project)
        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify no NuGet package references were added (native doesn't use PackageReferences)
        Assert.IsEmpty(_fakeDotNetService.AddedPackages,
            "Native projects should not have NuGet package references added via dotnet CLI");
    }

    #endregion

    #region Windows App SDK Runtime installation tests (bug fix verification)

    [TestMethod]
    public async Task SetupWorkspace_DotNet_AttemptsRuntimeInstall()
    {
        // This test verifies the key bug fix: the Windows App SDK Runtime install
        // is now shared between .NET and native paths.
        // Previously, the .NET path skipped the runtime install entirely.

        // Arrange - Create a .csproj with valid TFM
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert - The overall setup should still succeed
        // (runtime install failure is non-blocking)
        Assert.AreEqual(0, exitCode, "Setup should complete despite runtime install not finding MSIX packages");

        // Verify the runtime install was ATTEMPTED by checking output for the
        // runtime install step. This is the key behavioral change from the merge:
        // before, .NET projects never reached this code path.
        // Note: Non-error log messages go to static AnsiConsole, error logs to ConsoleStdErr,
        // and Spectre status display goes to TestAnsiConsole
        var ansiOutput = TestAnsiConsole.Output;
        var logOutput = ConsoleStdErr.ToString();
        var combinedOutput = ansiOutput + logOutput;

        var runtimeInstallAttempted =
            combinedOutput.Contains("Windows App SDK Runtime", StringComparison.OrdinalIgnoreCase) ||
            combinedOutput.Contains("MSIX", StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(runtimeInstallAttempted,
            "Output should show the runtime install step was attempted for .NET projects. " +
            $"This verifies the merged code path. Output:\n{combinedOutput}");
    }

    [TestMethod]
    public async Task SetupWorkspace_DotNet_SkipsRuntimeInstall_WhenSdkModeNone()
    {
        // Arrange - Create a .csproj
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify no packages were queried or added (SdkInstallMode.None skips everything)
        Assert.IsEmpty(_fakeDotNetService.AddedPackages,
            "With SdkInstallMode.None, no packages should be added");
    }

    #endregion

    #region Shared behavior tests

    [TestMethod]
    public async Task SetupWorkspace_DotNet_DoesNotCreateWinappYaml()
    {
        // The .NET path should NOT create a winapp.yaml config file.
        // .NET projects use .csproj PackageReferences instead.

        // Arrange
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify no winapp.yaml was created — .NET projects don't need it
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(configPath),
            "winapp.yaml should NOT be created for .NET projects (they use .csproj PackageReferences)");
    }

    [TestMethod]
    public async Task SetupWorkspace_DotNet_DoesNotInstallNativeSdkPackages()
    {
        // The .NET path should not install the C++ SDK packages that the native path uses
        // (e.g., Microsoft.Windows.CppWinRT, Microsoft.Windows.SDK.CPP, etc.)

        // Arrange
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // Verify no C++-specific packages were queried through NuGet
        // The native path queries NugetService.SDK_PACKAGES which includes CppWinRT, CPP SDK, etc.
        // The .NET path only queries BuildTools and WindowsAppSDK
        foreach (var queried in _fakeNugetService.QueriedPackages)
        {
            Assert.IsFalse(
                queried.Equals("Microsoft.Windows.CppWinRT", StringComparison.OrdinalIgnoreCase) ||
                queried.Equals(BuildToolsService.CPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase),
                $"C++ package '{queried}' should not be queried for .NET projects");
        }

        // Verify no native-specific subdirectories were created in .winapp
        // (include/, lib/, bin/ are created only for the native C++ path)
        var localWinappDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, ".winapp"));
        if (localWinappDir.Exists)
        {
            var nativeSubDirs = new[] { "include", "lib", "bin" };
            foreach (var subDir in nativeSubDirs)
            {
                var path = new DirectoryInfo(Path.Combine(localWinappDir.FullName, subDir));
                Assert.IsFalse(path.Exists,
                    $".winapp/{subDir} should not be created for .NET projects");
            }
        }
    }

    [TestMethod]
    public async Task SetupWorkspace_DotNet_ReportsSuccessMessage()
    {
        // Arrange
        await CreateCsprojAsync(_tempDirectory, "TestApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        var combinedOutput = TestAnsiConsole.Output + ConsoleStdOut.ToString() + ConsoleStdErr.ToString();
        var hasSuccessMessage = combinedOutput.Contains(".NET project setup completed", StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(hasSuccessMessage,
            $"Output should contain .NET-specific success message. Output:\n{combinedOutput}");
    }

    [TestMethod]
    public async Task SetupWorkspace_DotNet_SelectsSingleCsprojAutomatically()
    {
        // When there's exactly one .csproj, it should be selected automatically
        // without prompting

        // Arrange
        await CreateCsprojAsync(_tempDirectory, "MyApp", "net10.0-windows10.0.26100.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully");

        // All package additions should reference the correct project
        foreach (var (csprojPath, _, _) in _fakeDotNetService.AddedPackages)
        {
            Assert.Contains("MyApp.csproj", csprojPath,
                $"Package should be added to MyApp.csproj, but was added to {csprojPath}");
        }
    }

    #endregion

    #region SDK None with TFM update tests

    [TestMethod]
    public async Task SetupWorkspace_DotNet_NoSdks_StillUpdatesTfm()
    {
        // When the user selects SdkInstallMode.None but has an unsupported TFM,
        // the TFM should still be updated (with --use-defaults) and setup should succeed.

        // Arrange - Create a .csproj with an unsupported TFM (no -windows)
        var csproj = await CreateCsprojAsync(_tempDirectory, "TestApp", "net8.0");

        var workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        var options = new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            SdkInstallMode = SdkInstallMode.None,
            UseDefaults = true,
            RequireExistingConfig = false,
            NoGitignore = true
        };

        // Act
        var exitCode = await workspaceSetupService.SetupWorkspaceAsync(options, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Setup should complete successfully even with SdkInstallMode.None");

        // Verify TFM was still updated in the csproj file
        var updatedContent = await File.ReadAllTextAsync(csproj.FullName);
        Assert.Contains("-windows", updatedContent,
            "TFM should be updated to include -windows even when SDK installation is skipped");
        Assert.DoesNotContain(">net8.0<", updatedContent,
            "Original unsupported TFM should be replaced");

        // Verify no NuGet packages were added (SdkInstallMode.None skips package installation)
        Assert.IsEmpty(_fakeDotNetService.AddedPackages,
            "With SdkInstallMode.None, no packages should be added");
    }

    #endregion
}
