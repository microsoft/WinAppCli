// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli;

namespace WinApp.Cli.Tests;

[TestClass]
public class ProgramResolveLoggingModeTests
{
    [TestMethod]
    public void ResolveLoggingMode_NoFlags_DefaultsToInformation()
    {
        var mode = Program.ResolveLoggingMode(["init"]);

        Assert.AreEqual(LogLevel.Information, mode.MinimumLevel);
        Assert.IsFalse(mode.Quiet);
        Assert.IsFalse(mode.Verbose);
        Assert.IsFalse(mode.Json);
        Assert.IsNull(mode.ConflictError);
    }

    [TestMethod]
    [DataRow("--verbose")]
    [DataRow("-v")]
    public void ResolveLoggingMode_Verbose_SetsDebug(string flag)
    {
        var mode = Program.ResolveLoggingMode([flag]);

        Assert.AreEqual(LogLevel.Debug, mode.MinimumLevel);
        Assert.IsTrue(mode.Verbose);
        Assert.IsNull(mode.ConflictError);
    }

    [TestMethod]
    [DataRow("--quiet")]
    [DataRow("-q")]
    public void ResolveLoggingMode_Quiet_SetsWarning(string flag)
    {
        var mode = Program.ResolveLoggingMode([flag]);

        Assert.AreEqual(LogLevel.Warning, mode.MinimumLevel);
        Assert.IsTrue(mode.Quiet);
        Assert.IsNull(mode.ConflictError);
    }

    [TestMethod]
    public void ResolveLoggingMode_Json_SetsNone()
    {
        var mode = Program.ResolveLoggingMode(["--json"]);

        Assert.AreEqual(LogLevel.None, mode.MinimumLevel);
        Assert.IsTrue(mode.Json);
        Assert.IsNull(mode.ConflictError);
    }

    [TestMethod]
    public void ResolveLoggingMode_QuietAndVerbose_ReportsConflict()
    {
        var mode = Program.ResolveLoggingMode(["--quiet", "--verbose"]);

        Assert.AreEqual("Cannot specify both --quiet and --verbose options together.", mode.ConflictError);
    }

    [TestMethod]
    public void ResolveLoggingMode_QuietAndJson_ReportsConflict()
    {
        var mode = Program.ResolveLoggingMode(["--quiet", "--json"]);

        Assert.AreEqual("Cannot specify both --quiet and --json options together.", mode.ConflictError);
    }

    [TestMethod]
    public void ResolveLoggingMode_VerboseAndJson_ReportsConflict()
    {
        var mode = Program.ResolveLoggingMode(["--verbose", "--json"]);

        Assert.AreEqual("Cannot specify both --verbose and --json options together.", mode.ConflictError);
    }

    [TestMethod]
    public void ResolveLoggingMode_FlagAfterDoubleDash_IsIgnored()
    {
        // Tokens after a standalone "--" are passthrough payload, not winapp global flags.
        var mode = Program.ResolveLoggingMode(["run", ".", "--", "--json"]);

        Assert.IsFalse(mode.Json, "--json after -- must be treated as passthrough, not a global flag.");
        Assert.AreEqual(LogLevel.Information, mode.MinimumLevel);
    }
}

[TestClass]
[DoNotParallelize] // Invokes Program.Main, which mutates static Console streams and env vars.
public class ProgramMainTests
{
    private string _tempCacheDir = null!;
    private string? _savedCacheDir;
    private string? _savedUpdateCheck;
    private string? _savedCaller;

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
        _tempCacheDir = Path.Combine(Path.GetTempPath(), $"winapp_program_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempCacheDir);
        // Mark first-run complete and disable update checks so Main runs deterministically offline.
        File.Create(Path.Combine(_tempCacheDir, ".first-run-complete")).Dispose();

        _savedCacheDir = Environment.GetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY");
        _savedUpdateCheck = Environment.GetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK");
        _savedCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        _savedCiVars = CiVarNames.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));

        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _tempCacheDir);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", "0");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
        foreach (var name in CiVarNames)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CACHE_DIRECTORY", _savedCacheDir);
        Environment.SetEnvironmentVariable("WINAPP_CLI_UPDATE_CHECK", _savedUpdateCheck);
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _savedCaller);
        foreach (var (name, value) in _savedCiVars)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try { Directory.Delete(_tempCacheDir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> InvokeProgramAsync(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;

        // Writers are intentionally not disposed: Spectre.Console's static AnsiConsole may reference
        // them after Main returns.
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

    [TestMethod]
    public async Task Main_NoArguments_ShowsBannerAndHelp_ReturnsZero()
    {
        var (stdout, _, exitCode) = await InvokeProgramAsync([]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(stdout.Contains("Windows App Development CLI", StringComparison.Ordinal),
            $"No-args invocation must print the banner. Got stdout: {stdout}");
    }

    [TestMethod]
    public async Task Main_SingleDashLongOptionTypo_PrintsSuggestion_ReturnsOne()
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["ui", "inspect", "-app", "some-app"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(stderr.Contains("Did you mean", StringComparison.Ordinal),
            $"A single-dash long-option typo must suggest the double-dash form. Got stderr: {stderr}");
        Assert.IsTrue(stderr.Contains("--app", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Main_ConflictingLoggingFlags_PrintsError_ReturnsOne()
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["--quiet", "--verbose"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(stderr.Contains("Cannot specify both --quiet and --verbose options together.", StringComparison.Ordinal),
            $"Conflicting logging flags must be rejected. Got stderr: {stderr}");
    }

    [TestMethod]
    public async Task Main_CliSchemaWithCaller_SetsCallerEnvVarAndReturnsZero()
    {
        var (stdout, _, exitCode) = await InvokeProgramAsync(["--cli-schema", "--caller", "test-caller"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual("test-caller", Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER"),
            "The --caller value must be promoted to the WINAPP_CLI_CALLER environment variable.");
        Assert.IsTrue(stdout.Contains('{', StringComparison.Ordinal),
            $"--cli-schema must emit machine-readable JSON. Got: {stdout}");
    }

    [TestMethod]
    public async Task Main_UnknownCommand_FallsThroughToInvocation_WithoutTypoSuggestion()
    {
        // A bare unknown command produces parse errors but is not a single-dash typo, so the typo
        // shortcut is skipped and the parsed args flow into the normal invocation path.
        var (_, stderr, exitCode) = await InvokeProgramAsync(["this-is-not-a-real-command"]);

        Assert.AreNotEqual(0, exitCode, "An unknown command must not succeed.");
        Assert.IsFalse(stderr.Contains("Did you mean", StringComparison.Ordinal),
            $"A non-dash unknown command must not trigger the single-dash typo hint. Got stderr: {stderr}");
    }
}

[TestClass]
[DoNotParallelize] // The RunWithTelemetryAsync failure test redirects the static Console.Error stream.
public class ProgramSeamTests
{
    private static System.CommandLine.ParseResult ParseEmptyRoot() =>
        new System.CommandLine.RootCommand("test").Parse([], WinApp.Cli.Helpers.WinAppParserConfiguration.Default);

    [TestMethod]
    public void ConfigureConsoleEncoding_NonThrowingAction_IsApplied()
    {
        var applied = false;

        Program.ConfigureConsoleEncoding(() => applied = true);

        Assert.IsTrue(applied, "The encoding mutation must run when it does not throw.");
    }

    [TestMethod]
    public void ConfigureConsoleEncoding_ThrowingAction_IsSwallowed()
    {
        // Setting console encoding throws on redirected/unsupported handles; startup must survive it.
        Program.ConfigureConsoleEncoding(() => throw new IOException("encoding not supported"));
    }

    [TestMethod]
    public async Task RunWithTelemetryAsync_CompleteMode_ReturnsInvokeResult()
    {
        var invoked = false;

        var result = await Program.RunWithTelemetryAsync(ParseEmptyRoot(), isCompleteMode: true, () =>
        {
            invoked = true;
            return Task.FromResult(3);
        });

        Assert.AreEqual(3, result);
        Assert.IsTrue(invoked);
    }

    [TestMethod]
    public async Task RunWithTelemetryAsync_NonCompleteMode_LogsTelemetryAndReturnsResult()
    {
        var result = await Program.RunWithTelemetryAsync(ParseEmptyRoot(), isCompleteMode: false,
            () => Task.FromResult(0));

        Assert.AreEqual(0, result);
    }

    [TestMethod]
    public async Task RunWithTelemetryAsync_InvokeThrows_ReturnsOneAndWritesError()
    {
        var originalErr = Console.Error;
        var stderr = new StringWriter();
        try
        {
            Console.SetError(stderr);

            var result = await Program.RunWithTelemetryAsync(ParseEmptyRoot(), isCompleteMode: false,
                () => throw new InvalidOperationException("boom"));

            Assert.AreEqual(1, result);
            Assert.IsTrue(stderr.ToString().Contains("An unexpected error occurred: boom", StringComparison.Ordinal),
                $"The top-level handler must surface the failure message. Got: {stderr}");
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
