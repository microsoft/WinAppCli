// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // touch — synthetic touch gestures (tap/swipe/pinch/…) at a selector
    // center or explicit app x,y coordinates. Exercised via FakePointerInput
    // (records contacts), FakeForegroundGuard (proceeds) and
    // FakeUiAutomationService (supplies the bounds rect via TryGetWindowRect).
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_Tap_SelectorCenter_InjectsTouch()
    {
        // Element center = (100 + 40/2, 100 + 20/2) = (120, 110). Window handle non-zero so the
        // command has a real target to bounds-check and foreground-verify against.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "e0", Type = "Button", Selector = "btn-ok-1234",
            X = 100, Y = 100, Width = 40, Height = 20, WindowHandle = 4242
        };

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn-ok-1234", "-a", "TestApp", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Tap, call.Gesture);
        Assert.AreEqual(1, call.ContactPaths.Count);
        Assert.AreEqual(new PointerPoint(120, 110), call.ContactPaths[0][0]);

        // Bounds-check + foreground gate both ran against the resolved window handle.
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.IsTrue(_fakeForeground.Calls.Count >= 1);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("tap", result.GetProperty("gesture").GetString());
        Assert.AreEqual(4242, result.GetProperty("hwnd").GetInt64());
    }

    [TestMethod]
    public async Task Touch_Swipe_ExplicitPoints_InjectsGlidePath()
    {
        _fakeSession.SessionResult.WindowHandle = 5150;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "100,100", "--to-point", "300,120", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Swipe, call.Gesture);
        Assert.AreEqual(new PointerPoint(100, 100), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(300, 120), call.ContactPaths[0][^1]);
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Right_ComputesEndPoint()
    {
        _fakeSession.SessionResult.WindowHandle = 5151;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "100,200", "--direction", "right", "--distance", "150", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.Swipe, call.Gesture);
        Assert.AreEqual(new PointerPoint(100, 200), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(250, 200), call.ContactPaths[0][^1]); // +150 on X
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Left_ComputesEndPoint()
    {
        _fakeSession.SessionResult.WindowHandle = 5152;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "400,200", "--direction", "left", "--distance", "150", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(400, 200), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(250, 200), call.ContactPaths[0][^1]); // -150 on X
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Up_ComputesEndPoint()
    {
        _fakeSession.SessionResult.WindowHandle = 5153;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "200,300", "--direction", "up", "--distance", "100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(200, 300), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(200, 200), call.ContactPaths[0][^1]); // -100 on Y
    }

    [TestMethod]
    public async Task Touch_Swipe_Direction_Down_ComputesEndPoint()
    {
        _fakeSession.SessionResult.WindowHandle = 5154;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "200,100", "--direction", "down", "--distance", "100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(200, 100), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(200, 200), call.ContactPaths[0][^1]); // +100 on Y
    }

    [TestMethod]
    public async Task Touch_Swipe_DistanceOnly_DefaultsToRightDirection()
    {
        // Backward-compat: --distance without --direction still moves right (the old behavior).
        _fakeSession.SessionResult.WindowHandle = 5155;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "swipe", "--at", "50,50", "--distance", "200", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(new PointerPoint(50, 50), call.ContactPaths[0][0]);
        Assert.AreEqual(new PointerPoint(250, 50), call.ContactPaths[0][^1]); // right = +X
    }

    [TestMethod]
    public async Task Touch_LongPress_NoHoldMs_DefaultsTo500ms()
    {
        _fakeSession.SessionResult.WindowHandle = 5156;

        var command = GetRequiredService<UiTouchCommand>();
        // No --hold-ms specified → should default to 500 ms for long-press.
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--json"]);
        Assert.AreEqual(0, exitCode);

        Assert.AreEqual(1, _fakePointer.TouchCalls.Count);
        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(TouchGesture.LongPress, call.Gesture);
        Assert.AreEqual(500, call.HoldMs); // long-press default
    }

    [TestMethod]
    public async Task Touch_LongPress_ExplicitHoldMs_UsesProvidedValue()
    {
        _fakeSession.SessionResult.WindowHandle = 5157;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--hold-ms", "1200", "--json"]);
        Assert.AreEqual(0, exitCode);

        var call = _fakePointer.TouchCalls[0];
        Assert.AreEqual(1200, call.HoldMs); // explicit value preserved
    }

    [TestMethod]
    public async Task Touch_LongPress_JsonOutputIncludesEffectiveHoldMs()
    {
        // Verify that the JSON result carries the effective holdMs so agents can observe it.
        _fakeSession.SessionResult.WindowHandle = 5158;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        // Default long-press holdMs = 500.
        Assert.IsTrue(result.TryGetProperty("holdMs", out var holdMsProp),
            "JSON output must contain 'holdMs' property");
        Assert.AreEqual(500, holdMsProp.GetInt32(), "holdMs must equal the effective long-press default (500)");
    }

    [TestMethod]
    public async Task Touch_LongPress_ExplicitHoldMs_JsonOutputIncludesExplicitValue()
    {
        _fakeSession.SessionResult.WindowHandle = 5159;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--hold-ms", "1500", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("holdMs", out var holdMsProp));
        Assert.AreEqual(1500, holdMsProp.GetInt32(), "holdMs must reflect the explicit --hold-ms value");
    }

    [TestMethod]
    public async Task Touch_Tap_JsonOutput_HoldMsIsZero()
    {
        // For a plain tap, holdMs=0 and must appear in JSON as 0 (not missing).
        _fakeSession.SessionResult.WindowHandle = 5160;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "tap", "--at", "100,100", "--json"]);
        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.IsTrue(result.TryGetProperty("holdMs", out var holdMsProp));
        Assert.AreEqual(0, holdMsProp.GetInt32(), "Tap gesture should report holdMs=0");
    }

    [TestMethod]
    public async Task Touch_InvalidGesture_Rejected_NoInjection()
    {
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "frobnicate", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
    }

    [TestMethod]
    public async Task Touch_FingersAboveMax_Rejected_NoInjection()
    {
        // 11 > MaxContacts (10) must be rejected up front, before planning/injecting.
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--fingers", "11", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // Rejected before any window resolution.
        Assert.AreEqual(0, _fakeUia.WindowRectCalls.Count);
    }

    [TestMethod]
    public async Task Touch_ExplicitPointOutsideWindow_Rejected_NoInjection()
    {
        _fakeSession.SessionResult.WindowHandle = 7000;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "5000,5000", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // The window rect WAS consulted (bounds check ran) but the foreground gate never fired.
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Touch_ZeroTargetHwnd_Rejected_NoInjection()
    {
        // Session has no window handle (0) and an explicit --at (no element resolves a handle),
        // so there is no verifiable target — the command must refuse to inject.
        _fakeSession.SessionResult.WindowHandle = 0;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // Refused before the rect lookup / foreground gate.
        Assert.AreEqual(0, _fakeUia.WindowRectCalls.Count);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Touch_WindowRectUnreadable_Rejected_NoInjection()
    {
        // A nonzero handle resolves, but the window rect can't be read → no verifiable
        // target, so the command must refuse (no_target) before the foreground gate.
        _fakeSession.SessionResult.WindowHandle = 7100;
        _fakeUia.WindowRectAllow = false;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count);
        // The rect lookup WAS attempted, but injection/foreground never happened.
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
    }

    [TestMethod]
    public async Task Touch_LongPress_ExplicitHoldMsZero_RejectedWithInvalidArguments()
    {
        // Explicit --hold-ms 0 with long-press is a degenerate combination that must be
        // rejected with a structured invalid_arguments error, NOT silently rewritten to 500.
        _fakeSession.SessionResult.WindowHandle = 5161;

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--gesture", "long-press", "--at", "100,100", "--hold-ms", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count,
            "No injection should occur when --hold-ms 0 is explicitly given with long-press");
    }

    [TestMethod]
    public async Task Touch_InjectThrowsInvalidOperation_ReturnsNonZeroWithStructuredError()
    {
        // If the pointer-injection path throws InvalidOperationException (e.g. UP-frame failure
        // now surfacing on the normal path), the command must catch it and return non-zero with a
        // structured JSON error (injection_unsupported), not crash or report success.
        _fakeSession.SessionResult.WindowHandle = 5162;
        _fakePointer.ThrowException = new InvalidOperationException("UP frame injection failed — pointer stuck");

        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--json"]);

        Assert.AreEqual(1, exitCode,
            "Command must return non-zero when the inject call throws InvalidOperationException");
        Assert.AreEqual(0, _fakePointer.TouchCalls.Count,
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

    // -------------------------------------------------------------------------
    // M1 (round-11) — typed AppNotFoundException: app-not-found → missing_app;
    // selector-ambiguity (plain IOE) → invalid_arguments, NOT missing_app.
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task Touch_SessionService_ThrowsAppNotFound_ReturnsMissingApp()
    {
        // When the session service throws AppNotFoundException (app not running), the outer
        // catch must map it to missing_app, not internal_error.
        _fakeSession.ThrowException = new WinApp.Cli.Services.AppNotFoundException(
            "No running app found matching '__test_nonexistent__'.");

        var command = GetRequiredService<UiTouchCommand>();
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
    public async Task Touch_UiaService_ThrowsSelectorAmbiguity_ReturnsInvalidArguments_NotMissingApp()
    {
        // When FindSingleElementAsync throws plain InvalidOperationException (selector matched
        // multiple elements), the outer catch must map it to invalid_arguments, NOT missing_app.
        _fakeSession.SessionResult = new WinApp.Cli.Models.UiSessionInfo
        {
            ProcessId = 1, ProcessName = "TestApp", WindowHandle = 1234
        };
        _fakeUia.FindSingleElementThrowException = new InvalidOperationException(
            "Selector matched 3 elements: ...");

        var command = GetRequiredService<UiTouchCommand>();
        // Use a bare selector (no --at) so FindSingleElementAsync is invoked.
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
}
