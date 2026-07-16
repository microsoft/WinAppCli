// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // pen — synthetic pen/stylus taps and ink strokes at a selector center,
    // explicit --at, or an explicit --path. Exercised via FakePointerInput
    // (records strokes), FakeForegroundGuard and FakeUiAutomationService (window rect).
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_Tap_SelectorCenter_InjectsPenPoint()
    {
        // Element center = (50 + 60/2, 60 + 40/2) = (80, 80).
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Image", Selector = "canvas-1234",
            X = 50, Y = 60, Width = 60, Height = 40, WindowHandle = 9001
        };

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["canvas-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.PenCalls.Count);
        var call = _fakePointer.PenCalls[0];
        Assert.AreEqual(1, call.Path.Count);
        Assert.AreEqual(new PointerPoint(80, 80), call.Path[0]);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("tap", result.GetProperty("action").GetString());
        Assert.AreEqual(9001, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task Pen_Path_MultiPoint_InjectsStroke()
    {
        _fakeSession.SessionResult.WindowHandle = 3300;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--path", "10,10 20,30 40,50", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.PenCalls.Count);
        var call = _fakePointer.PenCalls[0];
        Assert.AreEqual(3, call.Path.Count);
        Assert.AreEqual(new PointerPoint(10, 10), call.Path[0]);
        Assert.AreEqual(new PointerPoint(20, 30), call.Path[1]);
        Assert.AreEqual(new PointerPoint(40, 50), call.Path[2]);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("draw", result.GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task Pen_DurationMs_PassedThrough()
    {
        // Verify --duration-ms is forwarded to the pointer input as-is.
        _fakeSession.SessionResult.WindowHandle = 3301;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--path", "10,10 100,100", "--duration-ms", "800", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.PenCalls.Count);
        Assert.AreEqual(800, _fakePointer.PenCalls[0].DurationMs);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(800, result.GetProperty("durationMs").GetInt32());
    }

    [TestMethod]
    public async Task Pen_DurationMs_DefaultIsZero()
    {
        // Default --duration-ms (0) means ~10 ms per segment; verify 0 is passed through.
        _fakeSession.SessionResult.WindowHandle = 3302;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "50,50", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(0, _fakePointer.PenCalls[0].DurationMs);
    }

    [TestMethod]
    public async Task Pen_DurationMsAboveMax_RejectedWithInvalidArguments()
    {
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--path", "10,10 20,20", "--duration-ms", "60001", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--duration-ms");
        StringAssert.Contains(ConsoleStdErr.ToString(), "60000");
    }

    [TestMethod]
    public async Task Pen_PathWithAt_RejectedWithInvalidArguments()
    {
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--path", "10,10 20,20", "--at", "15,15", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "--at");
        StringAssert.Contains(stderr, "--path");
    }

    [TestMethod]
    public async Task Pen_DurationMsWithoutPath_RejectedWithInvalidArguments()
    {
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "50,50", "--duration-ms", "250", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
        AssertJsonErrorCode(UiJsonError.CodeInvalidArguments);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "--duration-ms");
        StringAssert.Contains(stderr, "--path");
    }

    [TestMethod]
    public async Task Pen_PressureNaN_Rejected_NoInjection()
    {
        // NaN passes `float.TryParse` but is caught by the !float.IsFinite guard in the handler.
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--pressure", "NaN", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count, "Pen must not be injected for NaN pressure");
        // Stdout must be empty.
        Assert.AreEqual(string.Empty, TestAnsiConsole.Output.Trim(),
            "Stdout must be empty — no success envelope for invalid pressure");
        // The structured JSON error written to stderr must carry code == "invalid_arguments".
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'invalid_arguments' for NaN pressure");
    }

    [TestMethod]
    public async Task Pen_TiltXNonInteger_Rejected_NoInjection()
    {
        // --tilt-x is Option<int>; a non-integer value is rejected at SCL parse time — the handler
        // is never called, so no injection occurs. Non-JSON path: help banner + exit 1 (unchanged).
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--tilt-x", "NaN"]);
        Assert.AreNotEqual(0, exitCode, "Non-integer --tilt-x must fail with a non-zero exit code");
        Assert.AreEqual(0, _fakePointer.PenCalls.Count, "Pen must not be injected for non-parseable --tilt-x");
    }


    [TestMethod]
    public async Task Pen_InvalidPressure_Rejected_NoInjection()
    {
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--pressure", "1.5", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
    }

    [TestMethod]
    public async Task Pen_PressureOutOfRange_NoApp_EmitsInvalidArguments_NotMissingApp()
    {
        // M5 regression: a PARSEABLE but OUT-OF-RANGE pressure value (5 > 1.0) must produce
        // invalid_arguments, not missing_app, because the semantic range check in the handler
        // runs before the missing-app guard.  This complements the parse-time typo test (which
        // uses "nope") by pinning the handler-level ordering with a valid-but-OOR value.
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["--at", "1,1", "--pressure", "5", "--json"]); // no -a, pressure OOR
        Assert.AreEqual(1, exitCode, "Out-of-range pressure must fail with exit code 1");
        Assert.AreEqual(0, _fakePointer.PenCalls.Count, "No injection for out-of-range pressure");
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Must return invalid_arguments (not missing_app) for a parseable but OOR pressure with no app");
    }

    [TestMethod]
    public async Task Pen_InjectThrowsInvalidOperation_ReturnsNonZeroWithStructuredError()
    {
        // If the pointer-injection path throws InvalidOperationException (e.g. pen device creation
        // or InjectSyntheticPointerInput fails), the command must catch it at the injection call site
        // and return non-zero with a structured JSON error (injection_unsupported), not crash or
        // report success.
        _fakeSession.SessionResult.WindowHandle = 4401;
        _fakePointer.ThrowException = new InvalidOperationException("CreateSyntheticPointerDevice(PT_PEN) failed — locked desktop");

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode,
            "Command must return non-zero when the inject call throws InvalidOperationException");
        Assert.AreEqual(0, _fakePointer.PenCalls.Count,
            "No successful injection was recorded (the fake threw before recording)");
        // Stdout must be empty — no success envelope must be emitted.
        Assert.AreEqual(string.Empty, TestAnsiConsole.Output.Trim(),
            "Stdout must be empty on injection failure — success envelope must not be emitted");
        // Stderr must contain a structured JSON error with code == injection_unsupported.
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error object; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInjectionUnsupported,
            error.GetProperty("error").GetProperty("code").GetString(),
            "JSON error.code must be 'injection_unsupported' when injection throws InvalidOperationException");
    }

    [TestMethod]
    public async Task Pen_Eraser_SetsFlag()
    {
        _fakeSession.SessionResult.WindowHandle = 4400;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "120,120", "--eraser", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.PenCalls.Count);
        Assert.IsTrue(_fakePointer.PenCalls[0].Eraser);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("erase", result.GetProperty("action").GetString());
        Assert.IsTrue(result.GetProperty("eraser").GetBoolean());
    }

    [TestMethod]
    public async Task Pen_PathOutsideWindow_WarnsAndInjects()
    {
        // #661: an out-of-window ink point is a non-fatal advisory, not a hard failure. Pen injects
        // anyway (consistent with the mouse verbs) and surfaces a warning, rather than rejecting with
        // invalid_arguments.
        _fakeSession.SessionResult.WindowHandle = 6600;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--path", "10,10 9000,9000", "--json"]);

        Assert.AreEqual(0, exitCode, "Out-of-window point is advisory, not fatal — injection proceeds");
        Assert.AreEqual(1, _fakePointer.PenCalls.Count, "Injection must still happen");
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.IsTrue(_fakeForeground.Calls.Count >= 1);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("warnings", out var warnings),
            "Out-of-window injection must surface a warnings[] advisory");
        Assert.AreEqual(1, warnings.GetArrayLength());
        StringAssert.Contains(warnings[0].GetString(), "outside the target window",
            "Warning must name the out-of-window condition");
    }

    [TestMethod]
    public async Task Pen_ZeroTargetHwnd_Rejected_NoInjection()
    {
        _fakeSession.SessionResult.WindowHandle = 0;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
        Assert.AreEqual(0, _fakeUia.WindowRectCalls.Count);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Pen_WindowRectUnreadable_Rejected_NoInjection()
    {
        // Nonzero handle resolves but its rect can't be read → no verifiable target (no_target).
        _fakeSession.SessionResult.WindowHandle = 6700;
        _fakeUia.WindowRectAllow = false;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Pen_NonDefaultPressureAndTilt_PassedThroughToInjector()
    {
        // M5 success-path coverage: non-default --pressure and --tilt-x/--tilt-y values must
        // reach the injector unchanged and appear in the success JSON envelope. Safe to run
        // without a live injection target because FakePointerInput records the call rather than
        // issuing real synthetic-pointer input.
        _fakeSession.SessionResult.WindowHandle = 5500;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100",
             "--pressure", "0.8", "--tilt-x", "30", "--tilt-y", "-15",
             "--json"]);

        Assert.AreEqual(0, exitCode, "Non-default pressure/tilt values must succeed");
        Assert.AreEqual(1, _fakePointer.PenCalls.Count, "Exactly one pen call must be recorded");

        var call = _fakePointer.PenCalls[0];
        Assert.AreEqual(0.8f, call.Pressure, 0.001f, "Non-default --pressure must reach the injector");
        Assert.AreEqual(30, call.TiltX, "--tilt-x must reach the injector");
        Assert.AreEqual(-15, call.TiltY, "--tilt-y must reach the injector");

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("tap", result.GetProperty("action").GetString(),
            "Single-point pen action must be 'tap'");
        Assert.AreEqual(0.8f, result.GetProperty("pressure").GetSingle(), 0.001f,
            "Success JSON must carry the effective pressure");
        Assert.AreEqual(30, result.GetProperty("tiltX").GetInt32(),
            "Success JSON must carry the effective tiltX");
        Assert.AreEqual(-15, result.GetProperty("tiltY").GetInt32(),
            "Success JSON must carry the effective tiltY");
    }

    // -------------------------------------------------------------------------
    // M1 (round-11) — typed AppNotFoundException: app-not-found → missing_app;
    // selector-ambiguity (plain IOE) → invalid_arguments, NOT missing_app.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_SessionService_ThrowsAppNotFound_ReturnsMissingApp()
    {
        // When the session service throws AppNotFoundException (app not running), the outer
        // catch must map it to missing_app, not internal_error.
        _fakeSession.ThrowException = new WinApp.Cli.Services.AppNotFoundException(
            "No running app found matching '__test_nonexistent__'.");

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "__test_nonexistent__", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode, "App-not-found must exit 1");
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeMissingApp,
            error.GetProperty("error").GetProperty("code").GetString(),
            "AppNotFoundException from session service must map to missing_app");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInternalError),
            $"AppNotFoundException must NOT produce internal_error; got stderr: {stderr}");
    }

    [TestMethod]
    public async Task Pen_UiaService_ThrowsSelectorAmbiguity_ReturnsInvalidArguments_NotMissingApp()
    {
        // When FindSingleElementAsync throws plain InvalidOperationException (selector matched
        // multiple elements), the outer catch must map it to invalid_arguments, NOT missing_app.
        _fakeSession.SessionResult = new WinApp.Cli.Models.UiSessionInfo
        {
            ProcessId = 1, ProcessName = "TestApp", WindowHandle = 1234
        };
        _fakeUia.FindSingleElementThrowException = new InvalidOperationException(
            "Selector matched 3 elements: ...");

        var command = GetRequiredService<UiPenCommand>();
        // Use a bare selector (no --at / --path) so FindSingleElementAsync is invoked.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "btn-ok", "--json"]);

        Assert.AreEqual(1, exitCode, "Ambiguous selector must exit 1");
        var stderr = ConsoleStdErr.ToString();
        int jsonStart = stderr.IndexOf('{');
        Assert.IsTrue(jsonStart >= 0, $"stderr must contain a JSON error; got: {stderr}");
        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderr.AsSpan(jsonStart).TrimEnd());
        Assert.AreEqual(UiJsonError.CodeInvalidArguments,
            error.GetProperty("error").GetProperty("code").GetString(),
            "Selector-ambiguity IOE must map to invalid_arguments, not missing_app");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeMissingApp),
            $"Selector-ambiguity must NOT produce missing_app; got stderr: {stderr}");
        Assert.IsFalse(stderr.Contains(UiJsonError.CodeInternalError),
            $"Selector-ambiguity must NOT produce internal_error; got stderr: {stderr}");
    }

    // -------------------------------------------------------------------------
    // id27/id28 — remote-session (RDP) delivery-uncertainty advisory. Synthetic
    // pen injection especially does not route over Remote Desktop yet reports
    // success, so the result must carry an honest warnings[] advisory.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Pen_RemoteSession_EmitsDeliveryWarning()
    {
        _fakeSession.SessionResult.WindowHandle = 8300;
        _fakeForeground.IsRemoteSessionResult = true;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(0, exitCode, "Injection still succeeds; the warning is advisory, not fatal");
        Assert.AreEqual(1, _fakePointer.PenCalls.Count);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("warnings", out var warnings),
            "Remote session must surface a warnings[] advisory (pen especially does not route over RDP)");
        Assert.AreEqual(1, warnings.GetArrayLength());
        StringAssert.Contains(warnings[0].GetString(), "pen",
            "Warning must name the pen input kind");
    }

    [TestMethod]
    public async Task Pen_LocalSession_NoDeliveryWarning()
    {
        _fakeSession.SessionResult.WindowHandle = 8301;

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsFalse(result.TryGetProperty("warnings", out _),
            "A local console session must not emit the remote-delivery warning");
    }
}
