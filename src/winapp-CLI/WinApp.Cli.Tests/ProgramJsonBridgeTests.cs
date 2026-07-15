// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
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

    // -------------------------------------------------------------------------
    // M1 — value-aware pre-scan: --json=true / --json=false spellings
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonEqualsTrue_ParseError_EmitsJsonEnvelope()
    {
        // M1: `--json=true` uses the equals-sign spelling. The pre-scan must detect it as
        // json=true so the logger is suppressed and the bridge fires on parse error.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=true", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty (logger suppressed)");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"--json=true must fire the bridge on parse error; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for --json=true parse error");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsTrue_MissingApp_JsonOnlyOnStderr_NoLoggerLine()
    {
        // M1: `--json=true` must suppress the logger (LogLevel.None) so that only the
        // structured JSON error appears on stderr and stdout is clean.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=true", "--at", "100,100", "--app", "__no_such_app__"]);

        Assert.AreEqual(1, exitCode, "Missing/invalid app must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(),
            $"Stdout must be empty when --json=true — no logger line should contaminate stdout; got: {stdout}");
        // A JSON error envelope must appear on stderr (runtime error, not parse error).
        Assert.IsTrue(stderr.Contains('{'),
            $"stderr must contain a JSON error; got: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonFalse_Verbose_NoConflict()
    {
        // M1: `--json false --verbose` must NOT trigger the json/verbose conflict check.
        // The pre-scan must correctly resolve --json as false (not true) when followed by "false".
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json", "false", "--verbose", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        // Must NOT emit the conflict error.
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json false must not trigger the --json/--verbose conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json false must not trigger the --json/--verbose conflict; got stderr: {stderr}");
        // Must NOT emit a JSON bridge envelope (json is false).
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"--json false must not emit a bridge envelope; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M3 — bridge is scoped to ui descendants only
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_NonUiCommand_ParseError_NoNestedUiSchema()
    {
        // M3: `cert info --bogus nope --json` has a parse error. The bridge must NOT impose the
        // UI nested schema {"error":{"code":"...","message":"..."}} on non-ui commands. The cert
        // command uses a flat schema or default SCL error output.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["cert", "info", "--bogus", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");

        // Deserialize structurally rather than string-matching so the assertion is robust against
        // JSON formatting differences (e.g. "error": { vs "error":{) — M4 regression fix.
        static bool HasNestedUiErrorSchema(string text)
        {
            var idx = text.IndexOf('{');
            if (idx < 0) { return false; }
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(text.AsSpan(idx).TrimEnd());
                return el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("error", out var errProp)
                    && errProp.ValueKind == JsonValueKind.Object
                    && errProp.TryGetProperty("code", out _);
            }
            catch { return false; }
        }

        Assert.IsFalse(HasNestedUiErrorSchema(stdout),
            $"Non-ui command must NOT get the nested UI error schema on stdout; got: {stdout}");
        Assert.IsFalse(HasNestedUiErrorSchema(stderr),
            $"Non-ui command must NOT get the nested UI error schema on stderr; got: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_UiPen_ParseError_StillGetsNestedSchema()
    {
        // M3 regression guard: ui pen must still get the nested UI schema after the bridge is scoped.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--pressure", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"ui pen must still get the nested UI error schema; got stderr: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "ui pen error.code must be 'invalid_arguments'");
    }

    // -------------------------------------------------------------------------
    // M4 — UiTouch: app-independent arg validation before missing-app check
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_UnknownGesture_NoApp_Json_EmitsInvalidArguments()
    {
        // M4: --gesture nope is invalid regardless of --app. Must return invalid_arguments, not missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--gesture", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid gesture must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for unknown gesture");
    }

    [TestMethod]
    public async Task Touch_BadAtCoord_NoApp_Json_EmitsInvalidArguments()
    {
        // M4: --at nope is an invalid coordinate regardless of --app. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --at must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for bad --at coordinate");
    }

    [TestMethod]
    public async Task Touch_TooManyFingers_NoApp_Json_EmitsInvalidArguments()
    {
        // M4: --fingers 99 exceeds the touch-injection contact limit. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "100,100", "--fingers", "99", "--json"]);

        Assert.AreEqual(1, exitCode, "--fingers 99 must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for --fingers out of range");
    }

    [TestMethod]
    public async Task Touch_ValidArgs_NoApp_Json_EmitsMissingApp()
    {
        // M4 regression guard: valid args with no app must still return missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Valid args + no app must return missing_app; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"Valid args + no app must NOT return invalid_arguments; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M5 — UiPen: --path and --at parsing before missing-app check
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_BadPath_NoApp_Json_EmitsInvalidArguments()
    {
        // M5: --path nope is an invalid path regardless of --app. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--path", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --path must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for bad --path");
    }

    [TestMethod]
    public async Task Pen_BadAt_NoApp_Json_EmitsInvalidArguments()
    {
        // M5: --at nope is an invalid coordinate regardless of --app. Must return invalid_arguments.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "nope", "--json"]);

        Assert.AreEqual(1, exitCode, "Invalid --at must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for bad --at coordinate");
    }

    [TestMethod]
    public async Task Pen_ValidArgs_NoApp_Json_EmitsMissingApp()
    {
        // M5 regression guard: valid args with no app must still return missing_app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode, "Missing app must exit 1");
        Assert.IsTrue(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Valid args + no app must return missing_app; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInvalidArguments),
            $"Valid args + no app must NOT return invalid_arguments; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // M1 (round-10) — pen/touch: --app <non-existent> must return missing_app,
    // not internal_error.  Both commands must agree (parity regression).
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_AppNotFound_WithAt_ReturnsMissingApp()
    {
        // When --app names a process that is not running, the session resolver throws
        // InvalidOperationException. The command must catch it as missing_app, not bubble
        // it to the generic handler (which emits internal_error).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "10,10", "--app", "__no_such_app_9b3a__", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-existent app must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Non-existent --app with valid --at must return missing_app, not internal_error");
    }

    [TestMethod]
    public async Task Pen_AppNotFound_WithPath_ReturnsMissingApp()
    {
        // Same as above but with --path instead of --at (covers the path-from-option code branch).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--path", "10,10 20,20", "--app", "__no_such_app_9b3a__", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-existent app must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Non-existent --app with valid --path must return missing_app, not internal_error");
    }

    [TestMethod]
    public async Task Touch_AppNotFound_WithAt_ReturnsMissingApp()
    {
        // Parity test: touch must agree with pen — both must return missing_app for a non-existent app.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "touch", "--at", "10,10", "--app", "__no_such_app_9b3a__", "--json"]);

        Assert.AreEqual(1, exitCode, "Non-existent app must fail with exit code 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Non-existent --app with valid --at must return missing_app for touch (parity with pen)");
    }

    [TestMethod]
    public async Task PenAndTouch_NoTarget_AppNotFound_BothReturnInvalidArguments()
    {
        // When both the target and the app are missing/invalid, the target-required check fires
        // first (before session resolution), so both pen and touch return invalid_arguments,
        // not missing_app. This verifies the ordering is consistent across the two commands.
        var (_, penStderr, penExit) = await InvokeProgramAsync(
            ["ui", "pen", "--app", "__no_such_app_9b3a__", "--json"]); // no --at/--path/selector
        var (_, touchStderr, touchExit) = await InvokeProgramAsync(
            ["ui", "touch", "--gesture", "tap", "--app", "__no_such_app_9b3a__", "--json"]); // no --at/selector

        Assert.AreEqual(1, penExit, "Pen must exit 1 with no target");
        Assert.AreEqual(1, touchExit, "Touch must exit 1 with no target");

        int penJsonStart = penStderr.IndexOf('{');
        Assert.IsTrue(penJsonStart >= 0, $"pen stderr must have JSON; got: {penStderr}");
        var penError = JsonSerializer.Deserialize<JsonElement>(penStderr.AsSpan(penJsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            penError.GetProperty("error").GetProperty("code").GetString(),
            "Pen with no target must return invalid_arguments (target check before session resolution)");

        int touchJsonStart = touchStderr.IndexOf('{');
        Assert.IsTrue(touchJsonStart >= 0, $"touch stderr must have JSON; got: {touchStderr}");
        var touchError = JsonSerializer.Deserialize<JsonElement>(touchStderr.AsSpan(touchJsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            touchError.GetProperty("error").GetProperty("code").GetString(),
            "Touch with no target must return invalid_arguments (parity with pen)");
    }

    // -------------------------------------------------------------------------
    // M2 (round-10) — single-dash typo bridge is gated by IsUiDescendant
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_NonUiCommand_NoNestedUiSchema()
    {
        // M2 regression: a single-dash typo on a non-UI command with --json must NOT receive
        // the nested UI {"error":{"code":"...","message":"..."}} schema. Before the fix the
        // IsUiDescendant gate was absent from the typo path, so cert info got the wrong schema.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["cert", "info", "file.pfx", "-password", "x", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");

        // Structural check (robust against formatted JSON whitespace differences — M4 fix applied).
        static bool HasNestedUiErrorSchema(string text)
        {
            var idx = text.IndexOf('{');
            if (idx < 0) { return false; }
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(text.AsSpan(idx).TrimEnd());
                return el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("error", out var errProp)
                    && errProp.ValueKind == JsonValueKind.Object
                    && errProp.TryGetProperty("code", out _);
            }
            catch { return false; }
        }

        Assert.IsFalse(HasNestedUiErrorSchema(stdout),
            $"Non-ui single-dash typo must NOT emit nested UI schema on stdout; got: {stdout}");
        Assert.IsFalse(HasNestedUiErrorSchema(stderr),
            $"Non-ui single-dash typo must NOT emit nested UI schema on stderr; got: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_SingleDashTypo_UiCommand_StillGetsNestedSchema()
    {
        // M2 regression guard: a single-dash typo on a ui command must still get the nested schema.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "-pressure", "0.5", "--json"]);

        Assert.AreEqual(1, exitCode, "Single-dash typo must exit 1");
        Assert.AreEqual(string.Empty, stdout.Trim(), "Stdout must be empty");
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0,
            $"ui command single-dash typo must still get nested UI schema; got stderr: {stderr}");
        var error = JsonSerializer.Deserialize<JsonElement>(stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "ui pen single-dash typo must return invalid_arguments in nested schema");
    }

    // -------------------------------------------------------------------------
    // M2 (round-11) — boolean pre-scan: --json=<invalid> must NOT be coerced
    // to true and must NOT trigger the --json/--verbose conflict check.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task JsonBridge_JsonEqualsBogus_Verbose_NoConflict_InvalidValueError()
    {
        // M2 root-cause fix: --json=bogus must NOT trigger the spurious --json/--verbose conflict
        // that the old pre-scan produced (it treated any non-"false" attached value as true and
        // then fired the conflict check). With bool.TryParse the pre-scan returns false for
        // "bogus", so no conflict fires and the command proceeds normally.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--at", "10,10", "--app", "__no_such__", "--json=bogus", "--verbose"]);

        Assert.AreEqual(1, exitCode, "Invalid --json value must exit 1");
        // The critical invariant: no conflict error.
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the --json/--verbose conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the --json/--verbose conflict; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsBogus_WithBadArg_InvalidValueError_NoConflict()
    {
        // Combining --json=bogus with another invalid arg: must produce a parse/error result
        // but NOT a --json/--verbose conflict.
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=bogus", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Invalid args must exit 1");
        // No conflict error (the key invariant).
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json=bogus must NOT trigger the conflict; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task JsonBridge_JsonEqualsFalse_Verbose_Regression_NoConflict()
    {
        // Regression: --json=false (valid bool false) combined with --verbose must still produce
        // no conflict (this was already fixed in round 9; keep it locked).
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "pen", "--json=false", "--verbose", "--pressure", "nope"]);

        Assert.AreEqual(1, exitCode, "Parse error must exit 1");
        Assert.IsFalse(stdout.Contains("Cannot specify both --verbose and --json"),
            $"--json=false must not trigger the conflict; got stdout: {stdout}");
        Assert.IsFalse(stderr.Contains("Cannot specify both --verbose and --json"),
            $"--json=false must not trigger the conflict; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains("\"error\":"),
            $"--json=false must not emit a bridge envelope; got stderr: {stderr}");
    }
}
