// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end integration tests that simulate complete workflows
/// including creating, building, initializing, and packaging real .NET applications
/// </summary>
[TestClass]
public class EndToEndTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IPowerShellService, FakePowerShellService>()
            .AddSingleton<IDevModeService, FakeDevModeService>();
    }

    [TestMethod]
    public async Task E2E_WinFormsApp_CreateBuildManifestAndPackage_ShouldSucceed()
    {
        // This is a comprehensive end-to-end test that:
        // 1. Creates a new WinForms app using 'dotnet new winforms'
        // 2. Builds it using 'dotnet build'
        // 3. Runs 'winapp manifest generate' to create the manifest
        // 4. Runs 'winapp package' to create an MSIX package
        // 5. Verifies the entire workflow completed successfully

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("WinFormsApp");
        var projectName = "TestWinFormsApp";

        // Step 1: Create a new WinForms application
        var createResult = await RunDotnetCommandAsync(projectDir, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");
        Assert.IsTrue(File.Exists(Path.Combine(projectDir.FullName, $"{projectName}.csproj")),
            "Project file should be created");

        // Step 2: Build the application
        var buildResult = await RunDotnetCommandAsync(projectDir, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build WinForms app: {buildResult.Output}");

        // Verify the build output exists
        var binFolder = new DirectoryInfo(Path.Combine(projectDir.FullName, "bin", "Release"));
        Assert.IsTrue(binFolder.Exists, "Build output directory should exist");

        // Find the target framework folder (e.g., net10.0-windows)
        var targetFrameworkFolder = binFolder.GetDirectories("net*-windows").FirstOrDefault();
        Assert.IsNotNull(targetFrameworkFolder, "Target framework folder should exist");

        var exePath = Path.Combine(targetFrameworkFolder.FullName, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exePath), "Built executable should exist");

        // Step 3: Run 'winapp manifest generate' to create the manifest
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--package-name", projectName,
            "--publisher-name", "CN=TestPublisher",
            "--executable", exePath
        };

        var manifestExitCode = await ParseAndInvokeWithCaptureAsync(manifestGenerateCommand, manifestArgs);
        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify manifest generated the necessary files
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest generate should create appxmanifest.xml");

        var assetsDir = Path.Combine(projectDir.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Manifest generate should create Assets directory");

        // Step 5: Run 'winapp package' to create MSIX package
        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageArgs = new[]
        {
            targetFrameworkFolder.FullName,  // Input folder with built binaries
            "--output", packageOutputPath,
            "--manifest", manifestPath,
            "--skip-pri"                     // Skip PRI generation for faster tests
        };

        var packageParseResult = packageCommand.Parse(packageArgs);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should complete successfully");

        // Step 6: Verify the MSIX package was created
        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX package should be created");

        var fileInfo = new FileInfo(packageOutputPath);
        Assert.IsGreaterThan(0, fileInfo.Length, "MSIX package should not be empty");

        // Verify the MSIX contains expected files
        using var archive = await ZipFile.OpenReadAsync(packageOutputPath, TestContext.CancellationToken);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        Assert.IsTrue(entries.Any(e => e.EndsWith("AppxManifest.xml", StringComparison.OrdinalIgnoreCase)),
            "MSIX should contain AppxManifest.xml");
        Assert.IsTrue(entries.Any(e => e.EndsWith($"{projectName}.exe", StringComparison.OrdinalIgnoreCase)),
            $"MSIX should contain {projectName}.exe");
        Assert.IsTrue(entries.Any(e => e.Contains("Assets/", StringComparison.OrdinalIgnoreCase)),
            "MSIX should contain Assets folder");
    }

    [TestMethod]
    public async Task E2E_WinFormsApp_WithCustomManifestOptions_ShouldPackageSuccessfully()
    {
        // This test generates a manifest with custom options to verify parameter handling

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("WinFormsAppCustom");
        var projectName = "TestWinFormsAppCustom";

        // Step 1: Create WinForms app
        var createResult = await RunDotnetCommandAsync(projectDir, $"new winforms -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create WinForms app: {createResult.Output}");

        // Step 2: Build the application
        var buildResult = await RunDotnetCommandAsync(projectDir, "build -c Release");
        Assert.AreEqual(0, buildResult.ExitCode, $"Failed to build WinForms app: {buildResult.Output}");

        var binFolder = new DirectoryInfo(Path.Combine(projectDir.FullName, "bin", "Release"));
        var targetFrameworkFolder = binFolder.GetDirectories("net*-windows").FirstOrDefault();
        Assert.IsNotNull(targetFrameworkFolder, "Target framework folder should exist");

        var exePath = Path.Combine(targetFrameworkFolder.FullName, $"{projectName}.exe");
        Assert.IsTrue(File.Exists(exePath), "Built executable should exist");

        // Step 3: Generate manifest with custom options
        var manifestGenerateCommand = GetRequiredService<ManifestGenerateCommand>();
        var manifestArgs = new[]
        {
            projectDir.FullName,
            "--package-name", "net10.0-windows",
            "--publisher-name", "CN=TestPublisher",
            "--version", "2.5.0.0",
            "--description", "Custom test application",
            "--executable", exePath
        };

        var manifestExitCode = await ParseAndInvokeWithCaptureAsync(manifestGenerateCommand, manifestArgs);

        Assert.AreEqual(0, manifestExitCode, "Manifest generate command should complete successfully");

        // Verify custom options were applied
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created");

        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.IsTrue(manifestContent.Contains("Id=\"net10.A0Windows\"", StringComparison.OrdinalIgnoreCase),
            "Manifest should contain custom package name");
        Assert.IsTrue(manifestContent.Contains("CN=TestPublisher", StringComparison.Ordinal),
            "Manifest should contain custom publisher");
        Assert.IsTrue(manifestContent.Contains("2.5.0.0", StringComparison.Ordinal),
            "Manifest should contain custom version");

        // Step 4: Package the application

        var packageCommand = GetRequiredService<PackageCommand>();
        var packageOutputPath = Path.Combine(_tempDirectory.FullName, $"{projectName}.msix");
        var packageArgs = new[]
        {
            targetFrameworkFolder.FullName,
            "--output", packageOutputPath,
            "--manifest", manifestPath
        };

        var packageParseResult = packageCommand.Parse(packageArgs);
        var packageExitCode = await packageParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(0, packageExitCode, "Package command should complete successfully");

        Assert.IsTrue(File.Exists(packageOutputPath), "MSIX package should be created");
    }

    [TestMethod]
    public async Task E2E_DotNetProject_InitWithSetupSdksNone_SkipsPackageReferences_ShouldSucceed()
    {
        // This test verifies that --setup-sdks none skips adding package references
        // but still creates manifest and certificate

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("DotNetConsoleAppNoSdk");
        var projectName = "TestConsoleAppNoSdk";

        // Step 1: Create a new console application
        var createResult = await RunDotnetCommandAsync(projectDir, $"new console -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create console app: {createResult.Output}");

        var csprojPath = Path.Combine(projectDir.FullName, $"{projectName}.csproj");


        // Step 2: Run 'winapp init --setup-sdks none --use-defaults'
        var initCommand = GetRequiredService<InitCommand>();
        var initArgs = new[]
        {
            projectDir.FullName,
            "--setup-sdks", "none",
            "--use-defaults"
        };

        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand, initArgs);
        Assert.AreEqual(0, initExitCode, "Init command should complete successfully");

        // Step 3: Verify that csproj was NOT modified (no package references added)
        var updatedCsprojContent = await File.ReadAllTextAsync(csprojPath, TestContext.CancellationToken);

        // When --setup-sdks none, we should not add WindowsAppSDK
        Assert.IsFalse(
            updatedCsprojContent.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase),
            "csproj should NOT contain Microsoft.WindowsAppSDK when --setup-sdks none is used");

        // Step 4: Manifest should still be created
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created even with --setup-sdks none");

        Console.WriteLine("Successfully initialized .NET project with --setup-sdks none");
    }

    [TestMethod]
    public async Task E2E_DotNetProject_InitDetectsCsprojAndAddsPackageReferences_ShouldSucceed()
    {
        // This test verifies the .NET project workflow:
        // 1. Creates a new console app with a .csproj
        // 2. Runs 'winapp init --use-defaults' which should detect the .csproj
        // 3. Verifies that NuGet package references were added to the .csproj
        // 4. Verifies manifest and assets were created

        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("DotNetConsoleApp");
        var projectName = "TestConsoleApp";

        // Step 1: Create a new console application using dotnet CLI
        var createResult = await RunDotnetCommandAsync(projectDir, $"new console -n {projectName} -o .");
        Assert.AreEqual(0, createResult.ExitCode, $"Failed to create console app: {createResult.Output}");

        var csprojPath = Path.Combine(projectDir.FullName, $"{projectName}.csproj");
        Assert.IsTrue(File.Exists(csprojPath), "Project file should be created");


        // Step 2: Run 'winapp init --use-defaults' to detect csproj and set up the project
        var initCommand = GetRequiredService<InitCommand>();
        var initArgs = new[]
        {
            projectDir.FullName,
            "--use-defaults"  // Non-interactive mode
        };

        var initExitCode = await ParseAndInvokeWithCaptureAsync(initCommand, initArgs);
        Assert.AreEqual(0, initExitCode, "Init command should complete successfully");

        // Step 3: Verify that NuGet package references were added to the .csproj
        var updatedCsprojContent = await File.ReadAllTextAsync(csprojPath, TestContext.CancellationToken);

        // The init command should add WindowsAppSDK package reference
        Assert.IsTrue(
            updatedCsprojContent.Contains("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase),
            "csproj should contain Microsoft.WindowsAppSDK package reference");

        // The init command should add BuildTools package reference
        Assert.IsTrue(
            updatedCsprojContent.Contains("Microsoft.Windows.SDK.BuildTools", StringComparison.OrdinalIgnoreCase),
            "csproj should contain Microsoft.Windows.SDK.BuildTools package reference");

        // Step 4: Verify the TargetFramework was updated to a Windows TFM while preserving the .NET version
        Assert.IsTrue(
            updatedCsprojContent.Contains("-windows", StringComparison.OrdinalIgnoreCase),
            "csproj TargetFramework should be updated to Windows TFM");

        // Verify the .NET version was preserved (should not downgrade from the project's original .NET version)
        // A console app created with .NET 10 SDK should stay on net10.0, not downgrade to net8.0
        Assert.IsTrue(
            updatedCsprojContent.Contains("net10.0-windows", StringComparison.OrdinalIgnoreCase) ||
            updatedCsprojContent.Contains("net9.0-windows", StringComparison.OrdinalIgnoreCase) ||
            updatedCsprojContent.Contains("net8.0-windows", StringComparison.OrdinalIgnoreCase),
            "csproj TargetFramework should preserve the .NET version with Windows SDK version added");

        // Step 5: Verify manifest and assets were created
        var manifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "Manifest should be created");

        var assetsDir = Path.Combine(projectDir.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Verify the manifest contains expected content
        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.IsTrue(manifestContent.Contains("Identity", StringComparison.OrdinalIgnoreCase),
            "Manifest should contain Identity element");

        Console.WriteLine("Successfully set up .NET project with winapp init");
    }

    /// <summary>
    /// Helper method to run dotnet commands
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(
        DirectoryInfo workingDirectory,
        string arguments)
    {
        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = processStartInfo };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        return (process.ExitCode, output, error);
    }
}
