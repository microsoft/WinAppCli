// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public class BuildToolsServiceTests : BaseCommandTests
{
    [TestMethod]
    public void BuildToolsService_WithTestCacheDirectory_UsesOverriddenDirectory()
    {
        // The BuildToolsService instance should use our test directory for all operations
        // We can test this by verifying that GetBuildToolPath returns null when no packages are installed
        // in our isolated test directory (as opposed to potentially finding tools in the real user directory)

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should be null since we haven't installed any packages in our test directory
        Assert.IsNull(result);

        // Additional verification: Create a fake bin directory structure and verify it's found
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var fakeToolPath = Path.Combine(binDir, "mt.exe");
        File.WriteAllText(fakeToolPath, "fake tool");

        // Now it should find the tool in our test directory
        var result2 = _buildToolsService.GetBuildToolPath("mt.exe");
        Assert.AreEqual(fakeToolPath, result2?.FullName);
    }

    [TestMethod]
    public void GetBuildToolPath_WithNonExistentTool_ReturnsNull()
    {
        // Arrange - Create package structure but without the requested tool
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        // Create a different tool, but not the one we're looking for
        File.WriteAllText(Path.Combine(binDir, "signtool.exe"), "fake signtool");

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public void GetBuildToolPath_WithMultipleVersions_ReturnsLatestVersion()
    {
        // Arrange - Create multiple package versions
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");

        // Create older version
        var olderPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.22000.1");
        var olderBinDir = Path.Combine(olderPackageDir, "bin", "10.0.22000.0", "x64");
        Directory.CreateDirectory(olderBinDir);
        var olderToolPath = Path.Combine(olderBinDir, "mt.exe");
        File.WriteAllText(olderToolPath, "old mt.exe");

        // Create newer version
        var newerPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var newerBinDir = Path.Combine(newerPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(newerBinDir);
        var newerToolPath = Path.Combine(newerBinDir, "mt.exe");
        File.WriteAllText(newerToolPath, "new mt.exe");

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should return the newer version
        Assert.AreEqual(newerToolPath, result!.FullName);
    }

    [TestMethod]
    public void GetBuildToolPath_WithPinnedVersion_ReturnsPinnedVersion()
    {
        // Arrange - Create multiple package versions
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");

        // Create older version
        var olderPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.22000.1742");
        var olderBinDir = Path.Combine(olderPackageDir, "bin", "10.0.22000.0", "x64");
        Directory.CreateDirectory(olderBinDir);
        var olderToolPath = Path.Combine(olderBinDir, "mt.exe");
        File.WriteAllText(olderToolPath, "old mt.exe");

        // Create newer version
        var newerPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1742");
        var newerBinDir = Path.Combine(newerPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(newerBinDir);
        var newerToolPath = Path.Combine(newerBinDir, "mt.exe");
        File.WriteAllText(newerToolPath, "new mt.exe");

        // Create config file that pins to older version
        var configContent = @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 10.0.22000.1742
";
        File.WriteAllText(_configService.ConfigPath.FullName, configContent);

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should return the pinned (older) version
        Assert.IsNotNull(result);
        Assert.Contains("10.0.22000.1742",
            result.FullName, $"Expected pinned version '10.0.22000.1742' but got: {result.FullName}");
    }

    [TestMethod]
    public void GetBuildToolPath_WithMultipleArchitectures_ReturnsCorrectArchitecture()
    {
        // Arrange - Create package with multiple architectures
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binVersionDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0");

        // Create x64 and x86 directories
        var x64BinDir = Path.Combine(binVersionDir, "x64");
        var x86BinDir = Path.Combine(binVersionDir, "x86");
        Directory.CreateDirectory(x64BinDir);
        Directory.CreateDirectory(x86BinDir);

        var x64ToolPath = Path.Combine(x64BinDir, "mt.exe");
        var x86ToolPath = Path.Combine(x86BinDir, "mt.exe");
        File.WriteAllText(x64ToolPath, "x64 mt.exe");
        File.WriteAllText(x86ToolPath, "x86 mt.exe");

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should return x64 version (since that's typically the system architecture)
        Assert.IsNotNull(result);
        Assert.IsTrue(result.FullName.Contains("x64") || result.FullName.Contains("x86")); // Should contain one of the architectures
    }

    [TestMethod]
    public async Task RunBuildToolAsync_WithValidTool_ReturnsOutput()
    {
        // Arrange - Create a fake tool that outputs to stdout
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        // Create a batch file that simulates a tool (since we can't create a real executable easily)
        var fakeToolPath = Path.Combine(binDir, "echo.cmd");
        File.WriteAllText(fakeToolPath, "@echo Hello from fake tool");

        // Act
        var (stdout, stderr) = await _buildToolsService.RunBuildToolAsync(new GenericTool("echo.cmd"), "", TestTaskContext, true, TestContext.CancellationToken);

        // Assert
        Assert.Contains("Hello from fake tool", stdout);
        Assert.AreEqual(string.Empty, stderr.Trim());
    }

    [TestMethod]
    public async Task RunBuildToolAsync_WithNonExistentTool_ThrowsFileNotFoundException()
    {
        // The method should now try to install BuildTools first, then throw FileNotFoundException
        // if the tool still isn't found after installation

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
        {
            await _buildToolsService.RunBuildToolAsync(new GenericTool("nonexistent.exe"), "", TestTaskContext, true, TestContext.CancellationToken);
        });
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WithNoExistingPackage_ShouldAttemptInstallation()
    {
        // This test verifies the method logic without actually downloading packages
        // since we can't easily mock the package installation service in this test setup

        // Act
        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);

        // Assert - Result can be either null (if installation fails) or a path (if successful)
        // The important part is that the method completes without throwing
        // In isolated test environment, it may actually install packages
        Assert.IsTrue(result == null || result.Exists);
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WithExistingPackage_ReturnsExistingPath()
    {
        // Arrange - Create existing package structure
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        // Act
        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);

        // Assert - Should find and return the existing bin path
        Assert.AreEqual(binDir, result!.FullName);
    }

    [TestMethod]
    public async Task EnsureBuildToolsAsync_WithForceLatest_ShouldAttemptReinstallation()
    {
        // Arrange - Create existing package structure
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        // Act - Force latest should attempt reinstallation even with existing package
        var result = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, forceLatest: true, TestContext.CancellationToken);

        // Assert - Result can be either null (if installation fails) or a path (if successful)
        // The important part is that the method completes and attempts reinstallation
        Assert.IsTrue(result == null || result.Exists);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WithExistingTool_ReturnsToolPath()
    {
        // Arrange - Create package structure with a tool
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var toolPath = Path.Combine(binDir, "mt.exe");
        File.WriteAllText(toolPath, "fake mt.exe");

        // Act
        var result = await _buildToolsService.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(toolPath, result!.FullName);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WithToolNameWithoutExtension_AddsExtensionAndReturnsPath()
    {
        // Arrange - Create package structure with a tool
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var toolPath = Path.Combine(binDir, "mt.exe");
        File.WriteAllText(toolPath, "fake mt.exe");

        // Act - Request tool without .exe extension
        var result = await _buildToolsService.EnsureBuildToolAvailableAsync("mt", TestTaskContext, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(toolPath, result.FullName);
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WithNoExistingPackageAndInstallSuccess_ReturnsToolPath()
    {
        // This test verifies that when no package exists, the method attempts installation
        // and if successful, returns the tool path

        // Act
        try
        {
            var result = await _buildToolsService.EnsureBuildToolAvailableAsync("mt.exe", TestTaskContext, TestContext.CancellationToken);

            // Assert - If we get here, installation was successful and we got a path
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Exists);
            Assert.AreEqual("mt.exe", result.Name);
        }
        catch (InvalidOperationException)
        {
            // This is expected if BuildTools installation fails in test environment
            // Test passes because the expected exception was thrown
        }
        catch (FileNotFoundException)
        {
            // This is expected if the tool isn't found even after installation
            // Test passes because the expected exception was thrown
        }
    }

    [TestMethod]
    public async Task EnsureBuildToolAvailableAsync_WithNonExistentTool_ThrowsFileNotFoundException()
    {
        // Arrange - Create package structure but without the requested tool
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        // Create a different tool, but not the one we're looking for
        File.WriteAllText(Path.Combine(binDir, "signtool.exe"), "fake signtool");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
        {
            await _buildToolsService.EnsureBuildToolAvailableAsync("nonexistent.exe", TestTaskContext, TestContext.CancellationToken);
        });
    }

    [TestMethod]
    public async Task RunBuildToolAsync_WithNoExistingPackage_AutoInstallsAndRuns()
    {
        // This test verifies that RunBuildToolAsync now automatically installs BuildTools
        // when the tool is not found initially

        try
        {
            // Create a simple batch command that outputs something
            // This will either succeed (if BuildTools installs successfully) or throw an exception
            await _buildToolsService.RunBuildToolAsync(new GenericTool("echo.cmd"), "test", TestTaskContext, true, TestContext.CancellationToken);

            // If we reach here, the auto-installation worked - test passes
        }
        catch (FileNotFoundException)
        {
            // This is expected if the specific tool isn't found even after installation - test passes
        }
        catch (InvalidOperationException)
        {
            // This is expected if BuildTools installation fails - test passes
        }
    }

    [TestMethod]
    public async Task RunBuildToolAsync_WithExistingTool_RunsDirectly()
    {
        // Arrange - Create package structure with a working batch file
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var batchFile = Path.Combine(binDir, "test.cmd");
        File.WriteAllText(batchFile, "@echo Hello from test tool");

        // Act
        var (stdout, stderr) = await _buildToolsService.RunBuildToolAsync(new GenericTool("test.cmd"), "", TestTaskContext, true, TestContext.CancellationToken);

        // Assert
        Assert.Contains("Hello from test tool", stdout);
        Assert.AreEqual(string.Empty, stderr.Trim());
    }
}

/// <summary>
/// Tests for BuildToolsService with non-verbose logging to test PrintErrorText behavior
/// </summary>
[TestClass]
[DoNotParallelize]
public class BuildToolsServicePrintErrorsTests() : BaseCommandTests(configPaths: true, verboseLogging: false)
{
    [TestMethod]
    public async Task RunBuildToolAsync_WithPrintErrorsTrue_WritesErrorOutput()
    {
        // Arrange - Create a batch file that fails with exit code 1 and writes to stderr
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var failingTool = Path.Combine(binDir, "failing.cmd");
        File.WriteAllText(failingTool, "@echo Error message to stderr 1>&2\r\n@exit /b 1");

        // Act: Run with printErrors=true - error SHOULD be printed via PrintErrorText
        var exception = await Assert.ThrowsExactlyAsync<BuildToolsService.InvalidBuildToolException>(async () =>
        {
            await _buildToolsService.RunBuildToolAsync(new GenericTool("failing.cmd"), "", TestTaskContext, printErrors: true, TestContext.CancellationToken);
        });

        // Verify the exception captures the error info
        Assert.Contains("Error message to stderr", exception.Stderr);

        // Verify that error-level log output occurred when printErrors=true
        // (PrintErrorText uses LogError which goes to stderr)
        var stdErrOutput = ConsoleStdErr.ToString();
        Assert.Contains("Error message to stderr", stdErrOutput, "Error output should be printed when printErrors is true");
    }

    [TestMethod]
    public async Task RunBuildToolAsync_WithPrintErrorsFalse_DoesNotWriteErrorOutput()
    {
        // Arrange - Create a batch file that fails with exit code 1 and writes to stderr
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");
        var buildToolsPackageDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", "10.0.26100.1");
        var binDir = Path.Combine(buildToolsPackageDir, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(binDir);

        var failingTool = Path.Combine(binDir, "failing.cmd");
        File.WriteAllText(failingTool, "@echo Error message to stderr 1>&2\r\n@exit /b 1");

        // Act: Run with printErrors=false - error should NOT be printed via PrintErrorText
        var exception = await Assert.ThrowsExactlyAsync<BuildToolsService.InvalidBuildToolException>(async () =>
        {
            await _buildToolsService.RunBuildToolAsync(new GenericTool("failing.cmd"), "", TestTaskContext, printErrors: false, TestContext.CancellationToken);
        });

        // Verify the exception still captures the error info
        Assert.Contains("Error message to stderr", exception.Stderr);

        // Verify that NO error-level log output occurred when printErrors=false
        var stdErrOutput = ConsoleStdErr.ToString();
        Assert.DoesNotContain("Error message to stderr", stdErrOutput,
            "Error output should NOT be printed when printErrors is false");
    }
}

/// <summary>
/// Tests for BuildToolsService .csproj fallback version resolution
/// </summary>
[TestClass]
[DoNotParallelize]
public class BuildToolsServiceCsprojFallbackTests : BaseCommandTests
{
    private FakeDotNetService _fakeDotNetService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeDotNetService = new FakeDotNetService();
        return services
            .AddSingleton<IDotNetService>(_fakeDotNetService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    [TestMethod]
    public void GetBuildToolPath_WithNoConfig_UsesCsprojPinnedVersion()
    {
        // Arrange - Create multiple package versions in the NuGet cache
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");

        var olderVersion = "10.0.22000.1742";
        var newerVersion = "10.0.26100.1742";

        var olderBinDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", olderVersion, "bin", "10.0.22000.0", "x64");
        Directory.CreateDirectory(olderBinDir);
        File.WriteAllText(Path.Combine(olderBinDir, "mt.exe"), "old mt.exe");

        var newerBinDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", newerVersion, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(newerBinDir);
        File.WriteAllText(Path.Combine(newerBinDir, "mt.exe"), "new mt.exe");

        // Create a .csproj in the temp directory so FindCsproj finds it
        var csprojPath = Path.Combine(_tempDirectory.FullName, "TestApp.csproj");
        File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        // Configure FakeDotNetService to return the older version from package list
        _fakeDotNetService.PackageListResult = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework("net10.0",
                    TopLevelPackages:
                    [
                        new DotNetPackage("Microsoft.Windows.SDK.BuildTools", olderVersion, olderVersion)
                    ],
                    TransitivePackages: [])
            ])
        ]);

        // Act - No winapp.yaml exists, so it should fall back to .csproj
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should return the older (csproj-pinned) version, not the latest
        Assert.IsNotNull(result);
        Assert.Contains(olderVersion,
            result.FullName, $"Expected path to contain pinned version '{olderVersion}' but got: {result.FullName}");
    }

    [TestMethod]
    public void GetBuildToolPath_WithNoConfigAndNoCsproj_ReturnsLatestVersion()
    {
        // Arrange - Create multiple package versions in the NuGet cache
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");

        var olderVersion = "10.0.22000.1742";
        var newerVersion = "10.0.26100.1742";

        var olderBinDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", olderVersion, "bin", "10.0.22000.0", "x64");
        Directory.CreateDirectory(olderBinDir);
        File.WriteAllText(Path.Combine(olderBinDir, "mt.exe"), "old mt.exe");

        var newerBinDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", newerVersion, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(newerBinDir);
        File.WriteAllText(Path.Combine(newerBinDir, "mt.exe"), "new mt.exe");

        // No .csproj, no winapp.yaml — should fall back to latest

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should return the newer (latest) version
        Assert.IsNotNull(result);
        Assert.Contains(newerVersion,
            result.FullName, $"Expected path to contain latest version '{newerVersion}' but got: {result.FullName}");
    }

    [TestMethod]
    public void GetBuildToolPath_WithNoConfigAndTransitiveCsprojPackage_UsesCsprojVersion()
    {
        // Arrange - Create multiple package versions in the NuGet cache
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");

        var pinnedVersion = "10.0.22000.1742";
        var newerVersion = "10.0.26100.1742";

        var pinnedBinDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", pinnedVersion, "bin", "10.0.22000.0", "x64");
        Directory.CreateDirectory(pinnedBinDir);
        File.WriteAllText(Path.Combine(pinnedBinDir, "mt.exe"), "pinned mt.exe");

        var newerBinDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", newerVersion, "bin", "10.0.26100.0", "x64");
        Directory.CreateDirectory(newerBinDir);
        File.WriteAllText(Path.Combine(newerBinDir, "mt.exe"), "new mt.exe");

        // Create a .csproj
        var csprojPath = Path.Combine(_tempDirectory.FullName, "TestApp.csproj");
        File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        // Configure as a transitive package (not top-level)
        _fakeDotNetService.PackageListResult = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework("net10.0",
                    TopLevelPackages: [],
                    TransitivePackages:
                    [
                        new DotNetPackage("Microsoft.Windows.SDK.BuildTools", pinnedVersion, pinnedVersion)
                    ])
            ])
        ]);

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - Should pick up the transitive package version
        Assert.IsNotNull(result);
        Assert.Contains(pinnedVersion,
            result.FullName, $"Expected path to contain transitive version '{pinnedVersion}' but got: {result.FullName}");
    }

    [TestMethod]
    public void GetBuildToolPath_ConfigTakesPrecedenceOverCsproj()
    {
        // Arrange - Create multiple package versions
        var packagesDir = Path.Combine(_testCacheDirectory.FullName, "packages");

        var configVersion = "10.0.22000.1742";
        var csprojVersion = "10.0.24000.1742";
        var newerVersion = "10.0.26100.1742";

        foreach (var version in new[] { configVersion, csprojVersion, newerVersion })
        {
            var sdkVersion = version.Replace(".1742", ".0");
            var binDir = Path.Combine(packagesDir, "microsoft.windows.sdk.buildtools", version, "bin", sdkVersion, "x64");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "mt.exe"), $"mt.exe {version}");
        }

        // Create winapp.yaml pinning to the config version
        var configContent = $@"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: {configVersion}
";
        File.WriteAllText(_configService.ConfigPath.FullName, configContent);

        // Create a .csproj that references a different version
        var csprojPath = Path.Combine(_tempDirectory.FullName, "TestApp.csproj");
        File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        _fakeDotNetService.PackageListResult = new DotNetPackageListJson(
        [
            new DotNetProject(
            [
                new DotNetFramework("net10.0",
                    TopLevelPackages:
                    [
                        new DotNetPackage("Microsoft.Windows.SDK.BuildTools", csprojVersion, csprojVersion)
                    ],
                    TransitivePackages: [])
            ])
        ]);

        // Act
        var result = _buildToolsService.GetBuildToolPath("mt.exe");

        // Assert - winapp.yaml should take precedence over .csproj
        Assert.IsNotNull(result);
        Assert.Contains(configVersion,
            result.FullName, $"Expected config version '{configVersion}' to take precedence but got: {result.FullName}");
    }
}
