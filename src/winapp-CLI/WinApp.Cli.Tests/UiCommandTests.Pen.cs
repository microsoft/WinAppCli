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
    public async Task Pen_InvalidPressure_Rejected_NoInjection()
    {
        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--at", "100,100", "--pressure", "1.5", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
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
    public async Task Pen_PathOutsideWindow_Rejected_NoInjection()
    {
        _fakeSession.SessionResult.WindowHandle = 6600;
        _fakeUia.WindowRect = new PointerRect(0, 0, 800, 600);

        var command = GetRequiredService<UiPenCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            ["-a", "TestApp", "--path", "10,10 9000,9000", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePointer.PenCalls.Count);
        Assert.IsTrue(_fakeUia.WindowRectCalls.Count >= 1);
        Assert.AreEqual(0, _fakeForeground.Calls.Count);
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
}
