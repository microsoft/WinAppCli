// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // Tests modify WINAPP_CLI_CALLER environment variable
public class CliUpgradeServiceTests : BaseCommandTests
{
    private ICliUpgradeService _cliUpgradeService = null!;
    private string? _originalCaller;
    private string? _originalLatestVersion;

    [TestInitialize]
    public void Setup()
    {
        _cliUpgradeService = GetRequiredService<ICliUpgradeService>();
        // Save and clear env var to avoid interference from parallel tests
        _originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        _originalLatestVersion = Environment.GetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "0.0.0");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _originalCaller);
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", _originalLatestVersion);
    }

    [TestMethod]
    public void DetectInstallChannel_WhenNoPackageIdentity_ReturnsStandaloneExe()
    {
        // Act - In a test environment, we don't have MSIX package identity and env var is cleared
        var channel = _cliUpgradeService.DetectInstallChannel();

        // Assert
        Assert.AreEqual(InstallChannel.StandaloneExe, channel);
    }

    [TestMethod]
    public void DetectInstallChannel_WhenCallerIsNpm_ReturnsNpm()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "npm");

        var channel = _cliUpgradeService.DetectInstallChannel();

        Assert.AreEqual(InstallChannel.Npm, channel);
    }

    [TestMethod]
    public void DetectInstallChannel_WhenCallerIsNodejsPackage_ReturnsNpm()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var channel = _cliUpgradeService.DetectInstallChannel();

        Assert.AreEqual(InstallChannel.Npm, channel);
    }

    [TestMethod]
    public void DetectInstallChannel_WhenCallerIsNuget_ReturnsNuGet()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nuget-package");

        var channel = _cliUpgradeService.DetectInstallChannel();

        Assert.AreEqual(InstallChannel.NuGet, channel);
    }

    [TestMethod]
    public void DetectInstallChannel_IsCaseInsensitive()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "NPM");

        var channel = _cliUpgradeService.DetectInstallChannel();

        Assert.AreEqual(InstallChannel.Npm, channel);
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_FirstCall_WritesUpdateCheckCacheFile()
    {
        // Act
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - Cache file should be created in the test cache directory
        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));
        cacheFile.Refresh();
        Assert.IsTrue(cacheFile.Exists, "Update check cache file should be created");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WithRecentCache_DoesNotUpdateCacheFile()
    {
        // Arrange - Write a cache file with a recent timestamp and no update available
        var cachePath = Path.Combine(_testCacheDirectory.FullName, ".update-check");
        var recentTime = DateTime.UtcNow;
        await File.WriteAllTextAsync(cachePath, $"{recentTime:O}\n");
        var originalWriteTime = File.GetLastWriteTimeUtc(cachePath);

        // Small delay to ensure file system timestamp would differ if rewritten
        await Task.Delay(50);

        // Act - Should use cached result and not rewrite cache
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - File should not have been modified
        var currentWriteTime = File.GetLastWriteTimeUtc(cachePath);
        Assert.AreEqual(originalWriteTime, currentWriteTime,
            "Cache file should not be rewritten when within check interval");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WithStaleCache_UpdatesCacheTimestamp()
    {
        // Arrange - Write a cache file with an old timestamp (> 24 hours ago)
        var cachePath = Path.Combine(_testCacheDirectory.FullName, ".update-check");
        var staleTime = DateTimeOffset.UtcNow.AddHours(-25);
        await File.WriteAllTextAsync(cachePath, $"{staleTime:O}\n");

        // Act - Should check for update since cache is stale
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - Cache file should be updated with a new timestamp
        var lines = await File.ReadAllLinesAsync(cachePath);
        Assert.IsTrue(lines.Length >= 1, "Cache file should have at least one line");
        Assert.IsTrue(DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var newTimestamp), "Cache should contain a valid timestamp");
        Assert.IsTrue(newTimestamp > staleTime, "Timestamp should be updated to a more recent time");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WithCachedSameVersion_CacheRemainsEmpty()
    {
        // Arrange - Write a cache file indicating no update available (empty version)
        var cachePath = Path.Combine(_testCacheDirectory.FullName, ".update-check");
        await File.WriteAllTextAsync(cachePath, $"{DateTime.UtcNow:O}\n");

        // Act
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - Cache should still have empty version line
        var lines = await File.ReadAllLinesAsync(cachePath);
        Assert.IsTrue(lines.Length >= 2 || (lines.Length == 1 && string.IsNullOrEmpty(lines[0]) == false),
            "Cache file should retain its format");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WhenCacheDirectoryDoesNotExist_CreatesItAndWritesCache()
    {
        // Arrange - Ensure cache directory does not exist
        if (_testCacheDirectory.Exists)
        {
            _testCacheDirectory.Delete(true);
        }

        // Act - Should create the directory and write the cache file
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - Cache file should exist
        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));
        cacheFile.Refresh();
        Assert.IsTrue(cacheFile.Exists, "Cache file should be created even when directory didn't exist");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WithCorruptCache_NeverThrows()
    {
        // Arrange - Corrupt cache file
        var cacheDir = _testCacheDirectory.FullName;
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllTextAsync(
            Path.Combine(cacheDir, ".update-check"),
            "this is not a valid cache file\ncorrupted content\nextra line");

        // Act & Assert - Should never throw regardless of cache state
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_CalledTwiceQuickly_OnlyWritesCacheOnce()
    {
        // Act
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);
        var cachePath = Path.Combine(_testCacheDirectory.FullName, ".update-check");
        var firstWriteTime = File.GetLastWriteTimeUtc(cachePath);

        // Small delay to ensure file system timestamp would change
        await Task.Delay(50);

        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);
        var secondWriteTime = File.GetLastWriteTimeUtc(cachePath);

        // Assert - Second call should use cache, not update the file
        Assert.AreEqual(firstWriteTime, secondWriteTime,
            "Second call within check interval should not rewrite cache file");
    }
}

[TestClass]
[DoNotParallelize] // Tests modify WINAPP_CLI_CALLER environment variable
public class UpgradeCommandTests : BaseCommandTests
{
    private string? _originalCaller;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services;
    }

    [TestInitialize]
    public void Setup()
    {
        _originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _originalCaller);
    }

    [TestMethod]
    public async Task UpgradeCommand_WhenNpmInstall_ExitsSuccessfully()
    {
        // Arrange
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "npm");
        var upgradeCommand = GetRequiredService<UpgradeCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(upgradeCommand, []);

        // Assert - npm installs just print instructions and exit 0
        Assert.AreEqual(0, exitCode, "Upgrade command should succeed for npm installs");
    }

    [TestMethod]
    public async Task UpgradeCommand_WhenNugetInstall_ExitsSuccessfully()
    {
        // Arrange
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nuget-package");
        var upgradeCommand = GetRequiredService<UpgradeCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(upgradeCommand, []);

        // Assert - NuGet installs just print instructions and exit 0
        Assert.AreEqual(0, exitCode, "Upgrade command should succeed for NuGet installs");
    }

    [TestMethod]
    public void UpgradeCommand_IsRegistered_InRootCommand()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var upgradeCmd = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "upgrade");

        Assert.IsNotNull(upgradeCmd, "upgrade command should be registered in root command");
    }

    [TestMethod]
    public void UpgradeCommand_HasCorrectDescription()
    {
        var upgradeCommand = GetRequiredService<UpgradeCommand>();

        Assert.AreEqual("upgrade", upgradeCommand.Name);
        Assert.Contains("latest version", upgradeCommand.Description!,
            "Description should mention updating to latest version");
    }

    [TestMethod]
    public void UpgradeCommand_IsInSetupCategory_InHelp()
    {
        // Verify the upgrade command appears in the root command's Setup category
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var upgradeCmd = rootCommand.Subcommands.FirstOrDefault(c => c.Name == "upgrade");

        Assert.IsNotNull(upgradeCmd, "upgrade command should exist");
        // Verify it's an UpgradeCommand type (used for help categorization)
        Assert.IsInstanceOfType<UpgradeCommand>(upgradeCmd);
    }
}
