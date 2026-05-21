// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Integration tests verifying that the Program-level gating logic correctly
/// suppresses update notifications for --json, --quiet, and --cli-schema modes,
/// and that --caller plumbs through to the notification hint text.
/// Tests that exercise suppression invoke Program.Main directly.
/// Tests that verify notification content use the service layer with env vars
/// matching what Program.Main would set.
/// </summary>
[TestClass]
[DoNotParallelize] // Modifies static Console streams and environment variables
public class UpdateNotificationGatingTests
{
    private string _tempCacheDir = null!;
    private string? _savedCacheDir;
    private string? _savedCaller;
    private string? _savedUpdateCheck;

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
        // Create temp cache directory and seed with a "newer" version
        _tempCacheDir = Path.Combine(Path.GetTempPath(), $"winapp_gating_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempCacheDir);
        SeedUpdateCheckCache(GetGuaranteedNewerVersion());

        // Create first-run marker so FirstRunService doesn't trigger logging
        File.Create(Path.Combine(_tempCacheDir, ".first-run-complete")).Dispose();

        // Save and override env vars
        _savedCacheDir = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        _savedCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        _savedUpdateCheck = Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK");
        _savedCiVars = CiVarNames.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));

        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _tempCacheDir);
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", null);

        // Clear CI vars to avoid suppression
        foreach (var name in CiVarNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _savedCacheDir);
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _savedCaller);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", _savedUpdateCheck);
        foreach (var (name, value) in _savedCiVars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_tempCacheDir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task JsonMode_SuppressesUpdateNotice_StdoutHasNoNotice()
    {
        var (stdout, stderr, _) = await InvokeProgramAsync(["get-winapp-path", "--global", "--json"]);

        Assert.IsFalse(stdout.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"--json stdout must not contain update notice. Got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"--json stderr must not contain update notice. Got stderr: {stderr}");
    }

    [TestMethod]
    public async Task QuietMode_SuppressesUpdateNotice()
    {
        var (stdout, stderr, _) = await InvokeProgramAsync(["get-winapp-path", "--global", "--quiet"]);

        Assert.IsFalse(stdout.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"--quiet stdout must not contain update notice. Got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"--quiet stderr must not contain update notice. Got stderr: {stderr}");
    }

    [TestMethod]
    public async Task CliSchemaMode_SuppressesUpdateNotice()
    {
        var (stdout, stderr, _) = await InvokeProgramAsync(["--cli-schema"]);

        Assert.IsFalse(stdout.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"--cli-schema stdout must not contain update notice. Got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"--cli-schema stderr must not contain update notice. Got stderr: {stderr}");
    }

    [TestMethod]
    public async Task NormalMode_ShowsUpdateNotice_OnStderr()
    {
        // Invoke through the real entrypoint — the notification should appear on stderr,
        // never stdout. We capture stderr via Console.SetError.
        var (stdout, stderr, _) = await InvokeProgramAsync(["get-winapp-path", "--global"]);

        Assert.IsFalse(stdout.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"Update notice must not appear on stdout. Got stdout: {stdout}");
        Assert.IsTrue(stderr.Contains("available", StringComparison.OrdinalIgnoreCase),
            $"Update notice should appear on stderr in normal mode. Got stderr: {stderr}");
    }

    [TestMethod]
    public async Task CallerNpm_ProducesNpmHint()
    {
        // --caller npm should set WINAPP_CLI_CALLER=npm which makes the update notice
        // include the npm update hint.
        var (_, stderr, _) = await InvokeProgramAsync(["get-winapp-path", "--global", "--caller", "npm"]);

        Assert.IsTrue(stderr.Contains("npm update", StringComparison.OrdinalIgnoreCase),
            $"With --caller npm, notice should contain npm update hint. Got stderr: {stderr}");
    }

    /// <summary>
    /// Seeds the .update-check file with a timestamp (now) and a specified "latest" version
    /// so the notification fires immediately without needing network access.
    /// </summary>
    private void SeedUpdateCheckCache(string version)
    {
        var content = $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}\n{version}\n";
        File.WriteAllText(Path.Combine(_tempCacheDir, ".update-check"), content);
    }

    private static string GetGuaranteedNewerVersion()
    {
        var currentCore = UpdateNotificationService.GetCoreVersion(WinApp.Cli.Helpers.VersionHelper.GetVersionString());
        return Version.TryParse(currentCore, out var parsed)
            ? $"{parsed.Major + 1}.0.0"
            : "5.0.0";
    }

    /// <summary>
    /// Invokes Program.Main with captured stdout/stderr.
    /// Writers are intentionally not disposed to avoid ObjectDisposedException from
    /// Spectre.Console's static AnsiConsole.Console which may reference them after return.
    /// </summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> InvokeProgramAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        try
        {
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var exitCode = await Program.Main(args);

            return (stdoutWriter.ToString(), stderrWriter.ToString(), exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
