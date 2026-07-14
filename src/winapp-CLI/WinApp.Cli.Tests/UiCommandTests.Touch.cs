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
}
