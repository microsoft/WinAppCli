// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize] // Tests modify environment variables
public class UpdateNotificationServiceTests : BaseCommandTests
{
    private IUpdateNotificationService _updateNotificationService = null!;
    private UpdateNotificationService _concreteService = null!;
    private string? _originalCaller;
    private string? _originalUpdateCheck;

    // All environment variable names checked by CIEnvironmentDetectorForTelemetry
    private static readonly string[] CiVarNames =
    [
        "CI", "GITHUB_ACTIONS", "TF_BUILD", "APPVEYOR", "TRAVIS", "CIRCLECI",
        "TEAMCITY_VERSION", "JB_SPACE_API_URL",
        "CODEBUILD_BUILD_ID", "AWS_REGION", "BUILD_ID", "BUILD_URL", "PROJECT_ID"
    ];
    private Dictionary<string, string?> _savedCiVars = [];

    [TestInitialize]
    public void Setup()
    {
        _updateNotificationService = GetRequiredService<IUpdateNotificationService>();
        _concreteService = (UpdateNotificationService)_updateNotificationService;
        // Prevent background HTTP calls during unit tests
        _concreteService.SkipBackgroundRefreshForTesting = true;
        // Redirect notification output to the test console for assertion capture
        _concreteService.NotificationConsole = TestAnsiConsole;

        // Save and clear env vars to avoid interference
        _originalCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        _originalUpdateCheck = Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK");
        _savedCiVars = CiVarNames.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));

        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", null);
        foreach (var name in CiVarNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _originalCaller);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", _originalUpdateCheck);
        foreach (var (name, value) in _savedCiVars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [TestMethod]
    public void CheckAndNotify_NoCacheFile_NoNotificationAndStartsBackgroundRefresh()
    {
        // First run with no cache — nothing to show, background refresh should start
        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify on first run (no cache), got: {output}");
    }

    [TestMethod]
    public async Task RefreshCacheAsync_WritesUpdateCheckCacheFile()
    {
        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));

        await _concreteService.RefreshCacheAsync(cacheFile);

        cacheFile.Refresh();
        Assert.IsTrue(cacheFile.Exists, "Update check cache file should be created");
    }

    [TestMethod]
    public async Task RefreshCacheAsync_PreservesLastShownDate()
    {
        var cacheFile = new FileInfo(Path.Combine(_testCacheDirectory.FullName, ".update-check"));
        // Write an existing cache with a lastShownDate
        cacheFile.Directory?.Create();
        File.WriteAllText(cacheFile.FullName, $"{DateTime.UtcNow.AddHours(-25):O}\n999.0.0\n2026-01-15");

        await _concreteService.RefreshCacheAsync(cacheFile);

        var cache = UpdateNotificationService.ReadCache(cacheFile);
        Assert.AreEqual("2026-01-15", cache.LastShownDate, "LastShownDate should be preserved after refresh");
    }

    [TestMethod]
    public void CheckAndNotify_CachedNewerVersion_DisplaysNotification()
    {
        // Pre-populate cache with a newer version and stale "shown" date
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("999.0.0"), $"Expected notification with version, got: {output}");
        Assert.IsTrue(output.Contains("available"), $"Expected 'available' in notification, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_CachedNewerVersion_AlreadyShownToday_NoNotification()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n{today}");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify when already shown today, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_CachedSameVersion_NoNotification()
    {
        var currentVersion = VersionHelper.GetVersionString();
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n{currentVersion}\n");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify for same version, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_CachedOlderVersion_NoNotification()
    {
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n0.0.1\n");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify for older version, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_ShowsNotice_UpdatesLastShownDate()
    {
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFilePath = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFilePath, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var cache = UpdateNotificationService.ReadCache(new FileInfo(cacheFilePath));
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.AreEqual(today, cache.LastShownDate, "LastShownDate should be updated to today after showing notice");
    }

    [TestMethod]
    public void CheckAndNotify_StaleCache_DoesNotBlockOnNetwork()
    {
        // Write an expired cache entry — CheckAndNotify should return instantly
        // (the background refresh is fire-and-forget)
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        var expiredTime = DateTime.UtcNow.AddHours(-25).ToString("O");
        File.WriteAllText(cacheFile, $"{expiredTime}\n0.0.0\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _updateNotificationService.CheckAndNotify();
        sw.Stop();

        // Should complete nearly instantly (no network call in the foreground)
        Assert.IsTrue(sw.ElapsedMilliseconds < 1000, $"CheckAndNotify took {sw.ElapsedMilliseconds}ms — should be instant (no blocking network call)");
    }

    [TestMethod]
    public void CheckAndNotify_NpmCaller_ShowsNpmUpgradeHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "npm");
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("npm update -g @microsoft/winappcli"), $"Expected npm hint, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_NodejsPackageCaller_ShowsNpmUpgradeHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("npm update -g @microsoft/winappcli"), $"Expected npm hint, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_NuGetCaller_ShowsNuGetUpgradeHint()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nuget-package");
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("github.com/microsoft/winappcli/releases"), $"Expected NuGet releases page hint, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_StandaloneExe_ShowsReleasesPageHint()
    {
        // No WINAPP_CLI_CALLER set, defaults to standalone exe
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("github.com/microsoft/winappcli/releases"), $"Expected releases page hint, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_OptOutEnvVar_NoNotification()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", "0");
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify when opted out, got: {output}");
    }

    [TestMethod]
    public void CheckAndNotify_CIEnvironment_NoNotification()
    {
        Environment.SetEnvironmentVariable("CI", "true");
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFile = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFile, $"{DateTime.UtcNow:O}\n999.0.0\n2020-01-01");

        _updateNotificationService.CheckAndNotify();

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("available"), $"Should not notify in CI, got: {output}");
    }

    [TestMethod]
    public void ReadCache_BackwardCompatible_TwoLineFormat()
    {
        // Old cache format (2 lines, no lastShownDate)
        var cacheDir = _testCacheDirectory.FullName;
        var cacheFilePath = Path.Combine(cacheDir, ".update-check");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(cacheFilePath, $"{DateTime.UtcNow:O}\n999.0.0");

        var cache = UpdateNotificationService.ReadCache(new FileInfo(cacheFilePath));

        Assert.AreEqual("999.0.0", cache.LatestVersion);
        Assert.AreEqual("", cache.LastShownDate, "Missing lastShownDate should default to empty (never shown)");
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

    [TestMethod]
    public void IsNewerVersion_NewerPreReleaseNumericIdentifier_ReturnsTrue()
    {
        // beta.2 > beta.1 because the numeric identifier 2 > 1
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("1.0.0-beta.2", "1.0.0-beta.1"));
    }

    [TestMethod]
    public void IsNewerVersion_OlderPreReleaseNumericIdentifier_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("1.0.0-beta.1", "1.0.0-beta.2"));
    }

    [TestMethod]
    public void IsNewerVersion_SamePreRelease_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("1.0.0-beta.1", "1.0.0-beta.1"));
    }

    [TestMethod]
    public void IsNewerVersion_LaterAlphaPreRelease_ReturnsTrue()
    {
        // "rc" > "beta" lexically
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("1.0.0-rc.1", "1.0.0-beta.1"));
    }

    [TestMethod]
    public void IsNewerVersion_NumericVsAlphanumericPreRelease_ReturnsCorrectOrder()
    {
        // Per SemVer: numeric identifiers have lower precedence than alphanumeric ones
        Assert.IsFalse(UpdateNotificationService.IsNewerVersion("1.0.0-1", "1.0.0-alpha"));
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("1.0.0-alpha", "1.0.0-1"));
    }

    [TestMethod]
    public void IsNewerVersion_LongerPreReleaseWithMatchingPrefix_ReturnsTrue()
    {
        // "beta.1.2" > "beta.1" because more fields when prefix matches
        Assert.IsTrue(UpdateNotificationService.IsNewerVersion("1.0.0-beta.1.2", "1.0.0-beta.1"));
    }

    [TestMethod]
    public void ParseTagName_WithVPrefix_StripsPrefix()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":"v1.2.3"}""");
        Assert.AreEqual("1.2.3", UpdateNotificationService.ParseTagName(doc));
    }

    [TestMethod]
    public void ParseTagName_WithoutVPrefix_ReturnsAsIs()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":"1.2.3"}""");
        Assert.AreEqual("1.2.3", UpdateNotificationService.ParseTagName(doc));
    }

    [TestMethod]
    public void ParseTagName_MissingProperty_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"other":"value"}""");
        Assert.IsNull(UpdateNotificationService.ParseTagName(doc));
    }

    [TestMethod]
    public void ParseTagName_NullValue_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":null}""");
        Assert.IsNull(UpdateNotificationService.ParseTagName(doc));
    }

    [TestMethod]
    public void ParseTagName_EmptyString_ReturnsNull()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":""}""");
        Assert.IsNull(UpdateNotificationService.ParseTagName(doc));
    }

    [TestMethod]
    public void ParseTagName_PreReleaseWithVPrefix_StripsOnlyV()
    {
        using var doc = JsonDocument.Parse("""{"tag_name":"v2.0.0-beta.1"}""");
        Assert.AreEqual("2.0.0-beta.1", UpdateNotificationService.ParseTagName(doc));
    }

    [TestMethod]
    public void IsPreReleaseVersion_StableVersion_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsPreReleaseVersion("1.0.0"));
    }

    [TestMethod]
    public void IsPreReleaseVersion_PreReleaseVersion_ReturnsTrue()
    {
        Assert.IsTrue(UpdateNotificationService.IsPreReleaseVersion("1.0.0-prerelease.73"));
    }

    [TestMethod]
    public void IsPreReleaseVersion_BetaVersion_ReturnsTrue()
    {
        Assert.IsTrue(UpdateNotificationService.IsPreReleaseVersion("0.3.2-beta.1"));
    }

    [TestMethod]
    public void IsPreReleaseVersion_WithBuildMetadata_ReturnsFalse()
    {
        Assert.IsFalse(UpdateNotificationService.IsPreReleaseVersion("1.0.0+build123"));
    }

    [TestMethod]
    public void IsPreReleaseVersion_PreReleaseWithBuildMetadata_ReturnsTrue()
    {
        Assert.IsTrue(UpdateNotificationService.IsPreReleaseVersion("1.0.0-rc.1+build456"));
    }

    [TestMethod]
    public void IsPreReleaseVersion_BranchPrereleaseLabel_ReturnsTrue()
    {
        Assert.IsTrue(UpdateNotificationService.IsPreReleaseVersion("0.3.2-dev-my-feature.42"));
    }
}
