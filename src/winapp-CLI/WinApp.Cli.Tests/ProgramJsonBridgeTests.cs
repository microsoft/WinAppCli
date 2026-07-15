// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Integration tests for the Program-level parse-error → JSON bridge.
/// All tests invoke <see cref="Program.Main"/> directly so the real bridge logic is
/// exercised — not a reimplementation in the test helper.
/// </summary>
/// <remarks>
/// The class is marked <c>[DoNotParallelize]</c> because <see cref="BaseCommandTests.InvokeProgramAsync"/>
/// redirects the process-wide <see cref="Console.Out"/> and <see cref="Console.Error"/> streams.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class ProgramJsonBridgeTests : BaseCommandTests
{
    [TestMethod]
    public async Task Pen_PressureInfinity_Rejected_NoInjection()
    {
        // SCL in the full parse chain parses "Infinity" as float.PositiveInfinity (parse succeeds).
        // The handler's !float.IsFinite guard then rejects it with a JSON invalid_arguments error.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--pressure", "Infinity", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-finite Infinity pressure must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for Infinity pressure");
    }

    // -------------------------------------------------------------------------
    // M4 — parse-time typed-option failures (previously tested via the now-deleted
    //       duplicate bridge in ParseAndInvokeWithCaptureAsync)
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_PressureTypo_Json_EmitsStructuredInvalidArgumentsError()
    {
        // --pressure nope is rejected at SCL parse time (Option<float> cannot parse "nope").
        // The bridge must intercept the error and emit an invalid_arguments JSON envelope.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--pressure", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Bad --pressure must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for bad --pressure");
    }

    [TestMethod]
    public async Task Pen_PressureTypo_NoApp_Json_EmitsInvalidArguments_NotMissingApp()
    {
        // Parse errors are intercepted at Program level before the missing-app check, so a bad
        // --pressure value without -a must return invalid_arguments, not missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--pressure", "nope", "--json"]); // no -a

        Assert.AreEqual(1, exitCode, "Bad --pressure must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) when --pressure is unparseable");
    }

    [TestMethod]
    public async Task Pen_TiltXTypo_Json_EmitsStructuredInvalidArgumentsError()
    {
        // --tilt-x nope is rejected at SCL parse time (Option<int>).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--tilt-x", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Bad --tilt-x must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for bad --tilt-x");
    }

    [TestMethod]
    public async Task Pen_TiltYTypo_Json_EmitsStructuredInvalidArgumentsError()
    {
        // --tilt-y nope is rejected at SCL parse time (Option<int>).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-a", "TestApp", "--at", "100,100", "--tilt-y", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Bad --tilt-y must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty on parse error");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error envelope; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for bad --tilt-y");
    }

    // -------------------------------------------------------------------------
    // M1 — effective JSON derived from PARSED option, not raw token scan
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonFalse_NoEnvelope_PlainTextError()
    {
        // M1: `--json false` must suppress the bridge — the pre-scan sees --json as present,
        // but the parsed boolean value is false, so effectiveJson = false.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--pressure", "nope", "--json", "false"]);

        Assert.AreEqual(1, exitCode, "Parse error must still exit 1");
        // Stderr must NOT contain a JSON error envelope.
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"--json false must NOT emit a JSON bridge envelope; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_CommandWithoutJsonOption_NoEnvelope()
    {
        // M1: `complete` has no --json option.  Even though the pre-scan sees --json in the
        // raw args, effectiveJson must be false and the bridge must not fire.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["complete", "wat", "--json"]);

        // No JSON bridge envelope on stderr — the bridge must not have fired.
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"Bridge must not fire for a command without --json; got stderr: {stderr}");
        Assert.IsFalse(stdout.Contains("\"error\":"),
            $"Bridge must not fire for a command without --json; got stdout: {stdout}");
    }

    // -------------------------------------------------------------------------
    // M2 — single-dash typo path routes through the JSON bridge in JSON mode
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_WithJson_EmitsJsonEnvelope()
    {
        // M2: `-pressure` is a single-dash long-option typo. Previously it bypassed the bridge
        // because the typo-check returned before the bridge. Now it must emit a JSON envelope
        // when --json is active on the selected command.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-pressure", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"JSON bridge must emit an envelope for single-dash typo in --json mode; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for single-dash typo");
    }

    // -------------------------------------------------------------------------
    // No false-positive: a valid invocation with --json must NOT emit invalid_arguments
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_ValidJsonInvocation_NoFalseInvalidArguments()
    {
        // Regression guard: a successfully parsed --json command with a runtime error (no app)
        // must NOT be caught by the parse-error bridge. It should surface as missing_app, not
        // invalid_arguments, because the parse succeeded (no SCL parse errors).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "1,1", "--json"]); // valid parse; missing -a → missing_app at runtime

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        // Bridge must NOT have fired (no parse errors → no invalid_arguments).
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"Bridge must not fire for a successfully parsed command; got stderr: {stderr}");
        // Runtime error (missing_app) should be in stderr.
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Expected missing_app error; got stderr: {stderr}");
    }
}
