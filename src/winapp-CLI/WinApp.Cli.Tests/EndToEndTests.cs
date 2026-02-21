// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.IO.Compression;
using System.Text;
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

    [TestMethod]
    public async Task E2E_NodeWrapper_PackageWithMissingInputFolder_ShouldSurfaceErrorOutput()
    {
        // Arrange
        var missingInput = Path.Combine(_tempDirectory.FullName, "missing-package-input");

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["package", missingInput],
            _tempDirectory,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail for non-existent package input folder.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("Input folder not found", StringComparison.OrdinalIgnoreCase)
                || combinedOutput.Contains("Directory does not exist", StringComparison.OrdinalIgnoreCase),
            $"Expected package missing-folder error to be surfaced. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_AddElectronDebugIdentityWithoutElectronExe_ShouldSurfaceNodeError()
    {
        // Arrange
        var emptyProjectDir = _tempDirectory.CreateSubdirectory("NodeNoElectronProject");
        var manifestPath = Path.Combine(emptyProjectDir.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, "<Package />", TestContext.CancellationToken);

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "add-electron-debug-identity", "--manifest", manifestPath],
            emptyProjectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail when electron.exe does not exist.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("Electron executable not found at:", StringComparison.OrdinalIgnoreCase),
            $"Expected Node-layer electron missing error to be surfaced. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_ClearElectronDebugIdentityWithoutElectronArtifacts_ShouldSurfaceNodeError()
    {
        // Arrange
        var emptyProjectDir = _tempDirectory.CreateSubdirectory("NodeNoElectronArtifacts");

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "clear-electron-debug-identity"],
            emptyProjectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail when electron executable and backup are missing.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("Neither Electron executable nor backup found in:", StringComparison.OrdinalIgnoreCase),
            $"Expected Node-layer missing-electron-artifacts error to be surfaced. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_AddElectronDebugIdentityWithMissingManifest_ShouldSurfaceNativeErrorWithoutGenericWrapper()
    {
        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("NodeMissingManifestPath");
        var electronDistDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "node_modules", "electron", "dist"));
        var electronExePath = Path.Combine(electronDistDir.FullName, "electron.exe");
        await File.WriteAllTextAsync(electronExePath, "fake electron executable", TestContext.CancellationToken);

        var missingManifestPath = Path.Combine(projectDir.FullName, "don", "appxmanifest.xml");

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "add-electron-debug-identity", "--manifest", missingManifestPath],
            projectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail for non-existent manifest path.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("File does not exist", StringComparison.OrdinalIgnoreCase)
                || combinedOutput.Contains("AppX manifest not found", StringComparison.OrdinalIgnoreCase),
            $"Expected native manifest-not-found error to be surfaced. Output: {combinedOutput}");
        Assert.IsFalse(
            combinedOutput.Contains("Failed to add Electron debug identity: winapp-cli exited with code", StringComparison.OrdinalIgnoreCase),
            $"Did not expect duplicate generic wrapper error output. Output: {combinedOutput}");
    }

    [TestMethod]
    public async Task E2E_NodeSubcommand_AddElectronDebugIdentityWithInvalidManifest_ShouldSurfaceManifestError()
    {
        // Arrange
        var projectDir = _tempDirectory.CreateSubdirectory("NodeInvalidManifestPath");
        var electronDistDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "node_modules", "electron", "dist"));
        var electronExePath = Path.Combine(electronDistDir.FullName, "electron.exe");
        await File.WriteAllTextAsync(electronExePath, "fake electron executable", TestContext.CancellationToken);

        var invalidManifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(invalidManifestPath, "<Package></Package>", TestContext.CancellationToken);

        // Act
        var result = await RunNodeWinappCommandAsync(
            ["node", "add-electron-debug-identity", "--manifest", invalidManifestPath, "--no-install"],
            projectDir,
            TestContext.CancellationToken);

        // Assert
        Assert.AreNotEqual(0, result.ExitCode, "Command should fail for invalid AppX manifest.");
        var combinedOutput = $"{result.Output}\n{result.Error}";
        Assert.IsTrue(
            combinedOutput.Contains("No Identity element found in AppX manifest", StringComparison.OrdinalIgnoreCase),
            $"Expected invalid-manifest diagnostic to be surfaced. Output: {combinedOutput}");
        Assert.IsFalse(
            combinedOutput.Contains("PowerShell command failed with exit code", StringComparison.OrdinalIgnoreCase),
            $"Did not expect generic PowerShell exit-code prefix in output. Output: {combinedOutput}");
    }

        [TestMethod]
        public async Task E2E_CreateDebugIdentityWithInvalidIdentityName_ShouldSurfaceDecodedPowerShellError()
        {
                // Arrange
                var projectDir = _tempDirectory.CreateSubdirectory("NodeInvalidIdentityNameManifest");
                var electronDistDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "node_modules", "electron", "dist"));
                var electronExePath = Path.Combine(electronDistDir.FullName, "electron.exe");

                // Use a real PE file so mt.exe succeeds and registration reaches PowerShell Add-AppxPackage.
                var sourceExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                File.Copy(sourceExePath, electronExePath, overwrite: true);

                var assetsDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "Assets"));
                await File.WriteAllTextAsync(Path.Combine(assetsDir.FullName, "Square150x150Logo.png"), "placeholder", TestContext.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(assetsDir.FullName, "Square44x44Logo.png"), "placeholder", TestContext.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(assetsDir.FullName, "StoreLogo.png"), "placeholder", TestContext.CancellationToken);

                var invalidManifestPath = Path.Combine(projectDir.FullName, "appxmanifest.xml");
                var invalidManifestContent = """
                        <?xml version="1.0" encoding="utf-8"?>
                        <Package
                                xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                                xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                                xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                                IgnorableNamespaces="uap rescap">
                            <Identity Name="my-  app" Publisher="CN=TestPublisher" Version="1.0.0.0" />
                            <Properties>
                                <DisplayName>Test App</DisplayName>
                                <PublisherDisplayName>Test Publisher</PublisherDisplayName>
                                <Logo>Assets/StoreLogo.png</Logo>
                            </Properties>
                            <Resources>
                                <Resource Language="en-us" />
                            </Resources>
                            <Applications>
                                <Application Id="App" Executable="electron.exe" EntryPoint="Windows.FullTrustApplication">
                                    <uap:VisualElements
                                        DisplayName="Test App"
                                        Description="Test App"
                                        BackgroundColor="transparent"
                                        Square150x150Logo="Assets/Square150x150Logo.png"
                                        Square44x44Logo="Assets/Square44x44Logo.png" />
                                </Application>
                            </Applications>
                            <Capabilities>
                                <rescap:Capability Name="runFullTrust" />
                            </Capabilities>
                        </Package>
                        """;
                await File.WriteAllTextAsync(invalidManifestPath, invalidManifestContent, TestContext.CancellationToken);

                var repoRoot = FindRepositoryRoot();
                if (repoRoot == null)
                {
                    Assert.Inconclusive("Could not find repository root containing src/winapp-CLI/WinApp.Cli/WinApp.Cli.csproj.");
                }

                var cliProjectPath = Path.Combine(repoRoot!.FullName, "src", "winapp-CLI", "WinApp.Cli", "WinApp.Cli.csproj");
                if (!File.Exists(cliProjectPath))
                {
                    Assert.Inconclusive($"Native CLI project not found at: {cliProjectPath}");
                }

                // Act
                var result = await RunDotnetCommandAsync(
                    projectDir,
                    $"run --project \"{cliProjectPath}\" -- create-debug-identity \"{electronExePath}\" --manifest \"{invalidManifestPath}\"");

                    // Assert
                    Assert.AreNotEqual(0, result.ExitCode, "Command should fail for manifest with invalid Identity Name value.");
                    var combinedOutput = $"{result.Output}\n{result.Error}";
                    Assert.IsTrue(
                        combinedOutput.Contains("Failed to add package identity:", StringComparison.OrdinalIgnoreCase),
                        $"Expected add-package-identity failure message to be surfaced. Output: {combinedOutput}");
                    Assert.IsFalse(
                        combinedOutput.Contains("Get-AppPackageLog", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("At line:", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("CategoryInfo", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("_x000D__x000A_", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("<Objs Version=", StringComparison.OrdinalIgnoreCase)
                            || combinedOutput.Contains("#< CLIXML", StringComparison.OrdinalIgnoreCase),
                        $"Did not expect raw CLIXML output. Output: {combinedOutput}");
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

    private static async Task<(int ExitCode, string Output, string Error)> RunNodeWinappCommandAsync(
        string[] args,
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken)
    {
        var repoRoot = FindRepositoryRoot();
        if (repoRoot == null)
        {
            Assert.Inconclusive("Could not find repository root containing src/winapp-npm/dist/cli.js.");
        }

        var cliPath = Path.Combine(repoRoot!.FullName, "src", "winapp-npm", "dist", "cli.js");
        if (!File.Exists(cliPath))
        {
            Assert.Inconclusive($"Node CLI entry point not found at: {cliPath}");
        }

        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "node",
            Arguments = BuildNodeArguments(cliPath, args),
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new System.Diagnostics.Process { StartInfo = processStartInfo };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

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

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Inconclusive("Node.js executable was not found on PATH for this test run.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
    }

    private static DirectoryInfo? FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var markerPath = Path.Combine(current.FullName, "src", "winapp-npm", "dist", "cli.js");
            if (File.Exists(markerPath))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string BuildNodeArguments(string cliPath, string[] args)
    {
        static string Escape(string value)
            => value.Contains(' ') || value.Contains('"')
                ? $"\"{value.Replace("\"", "\\\"")}\""
                : value;

        var escaped = args.Select(Escape);
        return $"{Escape(cliPath)} {string.Join(" ", escaped)}";
    }
}
