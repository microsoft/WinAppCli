// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // Tests modify environment variables
public class UpdateNotificationServiceTests : BaseCommandTests
{
    private IUpdateNotificationService _updateNotificationService = null!;
    private string? _originalCaller;
    private string? _originalLatestVersion;

    [TestInitialize]
    public void Setup()
    {
        _updateNotificationService = GetRequiredService<IUpdateNotificationService>();
        // Save and clear env vars to avoid interference
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
    public async Task CheckAndNotifyAsync_FirstCall_WritesUpdateCheckCacheFile()
    {
        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));
        cacheFile.Refresh();
        Assert.IsTrue(cacheFile.Exists, "Update check cache file should be created");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_SecondCallWithinThreshold_SkipsCheck()
    {
        // First call writes cache
        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));
        cacheFile.Refresh();
        var firstWriteTime = cacheFile.LastWriteTimeUtc;

        // Small delay to detect write time difference
        await Task.Delay(50);

        // Second call should skip (cache is fresh)
        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);
        cacheFile.Refresh();
        Assert.AreEqual(firstWriteTime, cacheFile.LastWriteTimeUtc, "Cache file should not be rewritten within threshold");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_ExpiredCache_RechecksAndWritesCache()
    {
        // Write an expired cache entry
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        var expiredTime = DateTime.UtcNow.AddHours(-25).ToString("O");
        File.WriteAllText(cacheFile, $"{expiredTime}\n");

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        // Cache should be refreshed with a new timestamp
        var lines = await File.ReadAllLinesAsync(cacheFile, TestContext.CancellationToken);
        Assert.IsTrue(lines.Length >= 1);
        Assert.IsTrue(DateTimeOffset.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var newTimestamp));
        Assert.IsTrue((DateTimeOffset.UtcNow - newTimestamp).TotalMinutes < 1, "Cache timestamp should be recent");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_NewerVersionAvailable_DisplaysNotification()
    {
        // Set a version that is newer than the current CLI version
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("999.0.0"), $"Expected notification with version, got: {output}");
        Assert.IsTrue(output.Contains("available"), $"Expected 'available' in notification, got: {output}");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_SameVersion_NoNotification()
    {
        var currentVersion = VersionHelper.GetVersionString();
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", currentVersion);

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify for same version, got: {output}");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_OlderVersion_NoNotification()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "0.0.1");

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify for older version, got: {output}");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_NpmCaller_ShowsNpmUpgradeHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "npm");

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("npm update -g @microsoft/winappcli"), $"Expected npm hint, got: {output}");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_NodejsPackageCaller_ShowsNpmUpgradeHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("npm update -g @microsoft/winappcli"), $"Expected npm hint, got: {output}");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_NuGetCaller_ShowsNuGetUpgradeHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nuget-package");

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("Microsoft.Windows.SDK.BuildTools.WinApp"), $"Expected NuGet hint, got: {output}");
    }

    [TestMethod]
    public async Task CheckAndNotifyAsync_StandaloneExe_ShowsReleasesPageHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_LATEST_VERSION", "999.0.0");
        // No WINAPP_CLI_CALLER set, defaults to standalone exe

        await _updateNotificationService.CheckAndNotifyAsync(TestContext.CancellationToken);

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("github.com/microsoft/winappcli/releases"), $"Expected releases page hint, got: {output}");
    }

    [TestMethod]
    public void IsNewerVersion_NewerVersion_ReturnsTrue()
    {
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("2.0.0", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_SameVersion_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("1.0.0", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_OlderVersion_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("0.9.0", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_PreReleaseToStable_ReturnsTrue()
    {
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("1.0.0", "1.0.0-beta.1"));
    }

    [TestMethod]
    public void IsNewerVersion_StableToPreRelease_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("1.0.0-beta.1", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_WithBuildMetadata_StripsAndCompares()
    {
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("2.0.0+build123", "1.0.0+abc456"));
    }

    [TestMethod]
    public void IsNewerVersion_InvalidLatest_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("not-a-version", "1.0.0"));
    }

    [TestMethod]
    public void IsNewerVersion_InvalidCurrent_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("2.0.0", "not-a-version"));
    }
}
