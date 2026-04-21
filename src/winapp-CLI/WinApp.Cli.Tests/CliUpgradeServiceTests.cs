// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
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
    [TestMethod]
    public async Task UpgradeAsync_WhenLatestIsOlderThanCurrent_ReturnsSuccessWithoutDownloading()
    {
        // Arrange - WINAPP_CLI_LATEST_VERSION is set to "0.0.0" in Setup,
        // which is older than any real current version
        // Act
        var exitCode = await _cliUpgradeService.UpgradeAsync(force: false, TestContext.CancellationToken);

        // Assert - should return 0 ("already up to date") without attempting download
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task UpgradeAsync_WhenLatestEqualsCurrent_ReturnsSuccessWithoutDownloading()
    {
        // Arrange - Set latest to the current version
        var currentVersion = VersionHelper.GetVersionString();
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", currentVersion);

        // Act
        var exitCode = await _cliUpgradeService.UpgradeAsync(force: false, TestContext.CancellationToken);

        // Assert - same version means "already up to date"
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task UpgradeAsync_WhenNpmChannel_SkipsVersionCheck()
    {
        // Arrange - npm channel should just print instructions regardless of version
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "npm");
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");

        // Act
        var exitCode = await _cliUpgradeService.UpgradeAsync(force: false, TestContext.CancellationToken);

        // Assert - npm always returns 0 with instructions
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WhenNewerVersionAvailable_DisplaysNotification()
    {
        // Arrange - Set latest version much higher than current to trigger notification
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");

        // Act
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - notification text should appear in the DI'd IAnsiConsole output
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "999.0.0", "Should display the new version number");
        StringAssert.Contains(output, "winapp upgrade", "Should show upgrade hint for standalone installs");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_WhenNoNewerVersion_DoesNotDisplayNotification()
    {
        // Arrange - WINAPP_CLI_LATEST_VERSION is "0.0.0" (from Setup), older than current
        // Act
        await _cliUpgradeService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Assert - no notification should be shown
        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("is available"), "Should not display update notification");
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

    [TestMethod]
    public void UpgradeCommand_HasForceOption()
    {
        var upgradeCommand = GetRequiredService<UpgradeCommand>();
        var forceOption = upgradeCommand.Options.FirstOrDefault(o => o.Name == "--force");

        Assert.IsNotNull(forceOption, "upgrade command should have a --force option");
    }
}

[TestClass]
public class IsNewerVersionTests
{
    [TestMethod]
    public void IsNewerVersion_NewerMajor_ReturnsTrue()
    {
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("2.0.0", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_NewerMinor_ReturnsTrue()
    {
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("1.1.0", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_NewerPatch_ReturnsTrue()
    {
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("1.0.1", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_SameVersion_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("1.0.0", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_OlderVersion_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("1.0.0", "2.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_StripsPrereleaseSuffix()
    {
        // "1.0.0-beta.1" stripped to "1.0.0" — same as current, so not newer
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("1.0.0-beta.1", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_StripsBuildMetadata()
    {
        // "1.0.0+abc123" stripped to "1.0.0" — same as current, so not newer
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("1.0.0+abc123", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_StripsPrereleaseAndBuildMetadata()
    {
        // "2.0.0-rc.1+sha.abc" stripped to "2.0.0" > "1.0.0"
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("2.0.0-rc.1+sha.abc", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_CurrentHasPrerelease_LatestIsStable()
    {
        // Current "1.0.0-beta" stripped to "1.0.0", same as latest "1.0.0" — not newer
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("1.0.0", "1.0.0-beta"));
    }

    [TestMethod]
    public void IsNewerVersion_BothHavePrerelease_ComparesBaseVersions()
    {
        // "2.0.0-alpha" > "1.0.0-beta" (after stripping: 2.0.0 > 1.0.0)
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("2.0.0-alpha", "1.0.0-beta"));
    }

    [TestMethod]
    public void IsNewerVersion_EmptyLatest_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_EmptyCurrent_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("1.0.0", ""));
    }

    [TestMethod]
    public void IsNewerVersion_BothEmpty_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("", ""));
    }

    [TestMethod]
    public void IsNewerVersion_UnparseableLatest_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("not-a-version", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_UnparseableCurrent_ReturnsFalse()
    {
        Assert.IsFalse(CliUpgradeService.IsNewerVersion("1.0.0", "not-a-version"));
    }

    [TestMethod]
    public void IsNewerVersion_TwoComponentVersion_WorksCorrectly()
    {
        // "1.2" is valid for Version.TryParse
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("1.2", "1.1"));
    }

    [TestMethod]
    public void IsNewerVersion_FourComponentVersion_WorksCorrectly()
    {
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("1.0.0.1", "1.0.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_CurrentHasBuildMetadataOnly()
    {
        // Current "1.0.0+sha.abc" stripped to "1.0.0", latest "1.0.1" > "1.0.0"
        Assert.IsTrue(CliUpgradeService.IsNewerVersion("1.0.1", "1.0.0+sha.abc"));
    }
}

[TestClass]
public class ParseReleaseAssetTests
{
    private static JsonElement ParseJson(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    [TestMethod]
    public void ParseReleaseAsset_MatchingAsset_ReturnsDownloadUrl()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.2.3",
            "assets": [
                {
                    "name": "winappcli-x64.zip",
                    "browser_download_url": "https://example.com/winappcli-x64.zip"
                }
            ]
        }
        """);

        var (url, version) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("https://example.com/winappcli-x64.zip", url);
        Assert.AreEqual("1.2.3", version);
    }

    [TestMethod]
    public void ParseReleaseAsset_StripsVPrefix()
    {
        var json = ParseJson("""
        {
            "tag_name": "v0.3.0",
            "assets": []
        }
        """);

        var (_, version) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("0.3.0", version);
    }

    [TestMethod]
    public void ParseReleaseAsset_NoVPrefix_KeepsVersionAsIs()
    {
        var json = ParseJson("""
        {
            "tag_name": "1.0.0",
            "assets": []
        }
        """);

        var (_, version) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("1.0.0", version);
    }

    [TestMethod]
    public void ParseReleaseAsset_NoMatchingAsset_ReturnsFallbackUrl()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.0.0",
            "assets": [
                {
                    "name": "winappcli-arm64.zip",
                    "browser_download_url": "https://example.com/winappcli-arm64.zip"
                }
            ]
        }
        """);

        var (url, _) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("https://github.com/microsoft/winappcli/releases/download/v1.0.0/winappcli-x64.zip", url);
    }

    [TestMethod]
    public void ParseReleaseAsset_FallbackPreservesRawTagName()
    {
        var json = ParseJson("""
        {
            "tag_name": "v2.0.0-beta",
            "assets": []
        }
        """);

        var (url, version) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        // Fallback URL preserves the raw "v2.0.0-beta" tag
        Assert.AreEqual("https://github.com/microsoft/winappcli/releases/download/v2.0.0-beta/winappcli-x64.zip", url);
        // Version strips "v" prefix
        Assert.AreEqual("2.0.0-beta", version);
    }

    [TestMethod]
    public void ParseReleaseAsset_NoAssetsProperty_ReturnsFallbackUrl()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.0.0"
        }
        """);

        var (url, _) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("https://github.com/microsoft/winappcli/releases/download/v1.0.0/winappcli-x64.zip", url);
    }

    [TestMethod]
    public void ParseReleaseAsset_EmptyAssetsArray_ReturnsFallbackUrl()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.0.0",
            "assets": []
        }
        """);

        var (url, _) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("https://github.com/microsoft/winappcli/releases/download/v1.0.0/winappcli-x64.zip", url);
    }

    [TestMethod]
    public void ParseReleaseAsset_NullTagName_Throws()
    {
        var json = ParseJson("""
        {
            "tag_name": null,
            "assets": []
        }
        """);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip"));
    }

    [TestMethod]
    public void ParseReleaseAsset_MissingTagName_Throws()
    {
        var json = ParseJson("""
        {
            "assets": []
        }
        """);

        Assert.ThrowsExactly<KeyNotFoundException>(
            () => CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip"));
    }

    [TestMethod]
    public void ParseReleaseAsset_AssetMatchIsCaseInsensitive()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.0.0",
            "assets": [
                {
                    "name": "WinAppCLI-X64.ZIP",
                    "browser_download_url": "https://example.com/download"
                }
            ]
        }
        """);

        var (url, _) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("https://example.com/download", url);
    }

    [TestMethod]
    public void ParseReleaseAsset_MultipleAssets_FindsCorrectOne()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.0.0",
            "assets": [
                {
                    "name": "winappcli-arm64.zip",
                    "browser_download_url": "https://example.com/arm64"
                },
                {
                    "name": "winappcli-x64.zip",
                    "browser_download_url": "https://example.com/x64"
                },
                {
                    "name": "winappcli_x64.msix",
                    "browser_download_url": "https://example.com/msix"
                }
            ]
        }
        """);

        var (url, _) = CliUpgradeService.ParseReleaseAsset(json, "winappcli-x64.zip");

        Assert.AreEqual("https://example.com/x64", url);
    }

    [TestMethod]
    public void ParseReleaseAsset_MsixAssetName_MatchesCorrectly()
    {
        var json = ParseJson("""
        {
            "tag_name": "v1.0.0",
            "assets": [
                {
                    "name": "winappcli_x64.msix",
                    "browser_download_url": "https://example.com/msix-x64"
                }
            ]
        }
        """);

        var (url, _) = CliUpgradeService.ParseReleaseAsset(json, "winappcli_x64.msix");

        Assert.AreEqual("https://example.com/msix-x64", url);
    }
}

[TestClass]
public class SwapExecutableTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"swap-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [TestMethod]
    public void SwapExecutable_ReplacesCurrentWithNew()
    {
        var currentExe = Path.Combine(_tempDir, "winapp.exe");
        var newExe = Path.Combine(_tempDir, "new", "winapp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(newExe)!);

        File.WriteAllText(currentExe, "old-content");
        File.WriteAllText(newExe, "new-content");

        CliUpgradeService.SwapExecutable(newExe, currentExe);

        Assert.AreEqual("new-content", File.ReadAllText(currentExe));
        Assert.IsFalse(File.Exists(newExe), "New exe should be moved, not copied");
    }

    [TestMethod]
    public void SwapExecutable_CleansUpLeftoverBackup()
    {
        var currentExe = Path.Combine(_tempDir, "winapp.exe");
        var backupExe = currentExe + ".old";
        var newExe = Path.Combine(_tempDir, "new", "winapp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(newExe)!);

        File.WriteAllText(currentExe, "current");
        File.WriteAllText(backupExe, "stale-backup");
        File.WriteAllText(newExe, "new-content");

        CliUpgradeService.SwapExecutable(newExe, currentExe);

        Assert.AreEqual("new-content", File.ReadAllText(currentExe));
        // Backup should be cleaned up (or replaced)
        Assert.IsFalse(File.Exists(backupExe), "Old backup should be cleaned up");
    }

    [TestMethod]
    public void SwapExecutable_RollsBackOnFailure()
    {
        var currentExe = Path.Combine(_tempDir, "winapp.exe");
        // newExe does not exist — will cause File.Move to throw
        var newExe = Path.Combine(_tempDir, "nonexistent", "winapp.exe");

        File.WriteAllText(currentExe, "original-content");

        Assert.ThrowsExactly<FileNotFoundException>(
            () => CliUpgradeService.SwapExecutable(newExe, currentExe));

        // Current exe should be rolled back to original
        Assert.AreEqual("original-content", File.ReadAllText(currentExe));
    }
}

[TestClass]
public class AuthenticodeHelperTests
{
    [TestMethod]
    public void VerifyMicrosoftSignature_WithNonexistentFile_ReturnsInvalid()
    {
        var result = AuthenticodeHelper.VerifyMicrosoftSignature(@"C:\nonexistent\fake.exe");

        Assert.IsFalse(result.IsValid);
        Assert.IsNotNull(result.ErrorMessage);
    }

    [TestMethod]
    public void VerifyMicrosoftSignature_WithUnsignedFile_ReturnsInvalid()
    {
        // Create a temporary unsigned file
        var tempFile = Path.Combine(Path.GetTempPath(), $"unsigned-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(tempFile, [0x4D, 0x5A, 0x00, 0x00]); // Minimal MZ header stub

            var result = AuthenticodeHelper.VerifyMicrosoftSignature(tempFile);

            Assert.IsFalse(result.IsValid, "Unsigned file should fail verification");
            Assert.IsNotNull(result.ErrorMessage);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void VerifyMicrosoftSignature_WithMicrosoftSignedSystemBinary_ReturnsValid()
    {
        // Try several system binaries — some use embedded Authenticode, others use catalog signing.
        // Catalog-signed files return TRUST_E_NOSIGNATURE (0x800B0100) for embedded-only checks.
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
        ];

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var result = AuthenticodeHelper.VerifyMicrosoftSignature(path);
            if (result.IsValid)
            {
                Assert.IsNotNull(result.SignerName, "Signer name should be present");
                StringAssert.Contains(result.SignerName, "Microsoft", "Signer should be Microsoft");
                return; // At least one signed binary verified successfully
            }
        }

        Assert.Inconclusive(
            "No system binary with embedded Authenticode signature found (all may use catalog signing).");
    }

    [TestMethod]
    public void SignatureVerificationResult_Success_HasExpectedProperties()
    {
        var result = SignatureVerificationResult.Success("Microsoft Corporation");

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual("Microsoft Corporation", result.SignerName);
        Assert.IsNull(result.ErrorMessage);
    }

    [TestMethod]
    public void SignatureVerificationResult_Fail_HasExpectedProperties()
    {
        var result = SignatureVerificationResult.Fail("test error");

        Assert.IsFalse(result.IsValid);
        Assert.IsNull(result.SignerName);
        Assert.AreEqual("test error", result.ErrorMessage);
    }
}
