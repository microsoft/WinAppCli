// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

/// <summary>
/// Command-level coverage of cooperative desktop turns (issue #764): coordination is entered only
/// after local validation, each command declares the right mode, desktop-sensitive work happens inside
/// a section, and no command acts on a target it resolved before waiting.
/// </summary>
public partial class UiCommandTests
{
    // ---------------------------------------------- validation must precede coordination (spec §10)

    [TestMethod]
    public async Task Click_MissingApp_NeverEntersCoordination()
    {
        // A malformed command must not open a participant lease, take an arrival ticket, or join an
        // indefinite queue behind another workflow.
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["Button", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count, "preflight must reject before coordination");
    }

    [TestMethod]
    public async Task Click_MissingSelector_NeverEntersCoordination()
    {
        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    [TestMethod]
    public async Task SendKeys_SystemReservedCombo_IsRefusedBeforeCoordination()
    {
        // win+l can never be driven from automation, so it must never wait for the desktop first.
        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["win+l", "-a", "TestApp", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    [TestMethod]
    public async Task Record_InvalidDuration_NeverEntersCoordination()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "-1", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    [TestMethod]
    public async Task Touch_InvalidGesture_NeverEntersCoordination()
    {
        var command = GetRequiredService<UiTouchCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["Button", "-a", "TestApp", "--gesture", "wiggle", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count);
    }

    // ------------------------------------------------------------------- mode classification (§6.1)

    [TestMethod]
    public async Task Inspect_IsAnObservation()
    {
        var command = GetRequiredService<UiInspectCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--json"]);

        Assert.AreEqual(1, _fakeDesktopLock.Runs.Count);
        Assert.AreEqual(UiTurnMode.Observe, _fakeDesktopLock.Runs[0].Mode);
        Assert.AreEqual("ui inspect", _fakeDesktopLock.Runs[0].Operation);
    }

    [TestMethod]
    public async Task SetValue_StaysAnObservationBecauseItIsBackgroundSafe()
    {
        // Spec §6.1: background-safe UIA mutations stay concurrent. This feature prevents desktop
        // interference, not transactional app-state isolation.
        _fakeUia.FindSingleResult = new UiElement { Id = "box", Selector = "box", Name = "Box" };
        var command = GetRequiredService<UiSetValueCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["box", "hello", "-a", "TestApp", "--json"]);

        Assert.AreEqual(UiTurnMode.Observe, _fakeDesktopLock.Runs[0].Mode);
    }

    [TestMethod]
    public async Task Record_IsTurnSharedSoSameOwnerInputCanInterleave()
    {
        var command = GetRequiredService<UiRecordCommand>();
        await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "--output", Path.Combine(_tempDirectory.FullName, "r.mp4"), "--json"]);

        Assert.AreEqual(UiTurnMode.TurnShared, _fakeDesktopLock.Runs[0].Mode);
    }

    [TestMethod]
    public async Task Invoke_IsDesktopExclusive()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "btn", Selector = "btn", Name = "Button" };
        var command = GetRequiredService<UiInvokeCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);
    }

    [TestMethod]
    public async Task Scroll_ClassifiesByTransport()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "list", Selector = "list", Name = "List", X = 10, Y = 10, Width = 100, Height = 100,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        // --direction uses the UIA ScrollPattern, which works in the background.
        var command = GetRequiredService<UiScrollCommand>();
        await ParseAndInvokeWithCaptureAsync(command, ["list", "-a", "TestApp", "--direction", "down", "--json"]);
        Assert.AreEqual(UiTurnMode.Observe, _fakeDesktopLock.Runs[0].Mode);

        // --wheel injects OS-wide mouse input at the cursor.
        _fakeDesktopLock.Runs.Clear();
        await ParseAndInvokeWithCaptureAsync(command, ["list", "-a", "TestApp", "--wheel", "3", "--json"]);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);
    }

    [TestMethod]
    public async Task Screenshot_ClassifiesByWhetherItNeedsTheForeground()
    {
        _fakeUia.ScreenshotResult = (new byte[4 * 4 * 4], 4, 4);
        var output = Path.Combine(_tempDirectory.FullName, "shot.png");
        var command = GetRequiredService<UiScreenshotCommand>();

        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--output", output, "--json"]);
        Assert.AreEqual(UiTurnMode.Observe, _fakeDesktopLock.Runs[0].Mode);

        _fakeDesktopLock.Runs.Clear();
        await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--focus", "--output", output, "--json"]);
        Assert.AreEqual(UiTurnMode.DesktopExclusive, _fakeDesktopLock.Runs[0].Mode);
    }

    // ------------------------------------------------- desktop-section placement and revalidation

    [TestMethod]
    public async Task Click_ForegroundsAndInjectsInsideADesktopSection()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "the foreground request and the click must run inside one desktop section");
        Assert.AreEqual(0, _fakeDesktopLock.OpenDesktopSections,
            "the section must be released before the result is formatted");
        CollectionAssert.Contains(_fakeDesktopForeground.ForegroundRequests, 4242L);
    }

    [TestMethod]
    public async Task Click_RefreshesTheTargetWindowFromTheReResolvedElement()
    {
        // The window a queued command acts on must come from the read taken inside the section, not
        // from the advisory read taken before the wait (spec §10.5).
        var stale = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 1111,
        };
        var current = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 2222,
        };
        // A per-selector read sequence: the advisory read taken before the section sees the old window,
        // every read inside the section sees the current one.
        _fakeUia.MovingResults["btn"] = new Queue<UiElement?>([stale, current, current, current, current]);
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        CollectionAssert.Contains(_fakeDesktopForeground.ForegroundRequests, 2222L);
        CollectionAssert.DoesNotContain(_fakeDesktopForeground.ForegroundRequests, 1111L,
            "the pre-wait window handle must never be foregrounded");
    }

    [TestMethod]
    public async Task Click_RefusesWhenTheTargetWindowClosedWhileQueued()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        // 0 means "no such window": the target closed while this command waited for the desktop.
        _fakeSystemQuery.ProcessIdForWindowResult = 0;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count, "no input may be injected at a dead target");
    }

    [TestMethod]
    public async Task Click_RefusesWhenTheWindowHandleWasRecycledByAnotherProcess()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "btn", Selector = "btn", Name = "Button",
            X = 10, Y = 20, Width = 40, Height = 30, WindowHandle = 4242,
        };
        // The session resolves PID 1234; a different PID means Windows reused the handle.
        _fakeSystemQuery.ProcessIdForWindowResult = 9999;

        var command = GetRequiredService<UiClickCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.AreEqual(0, _fakeMouse.ClickCalls.Count);
    }

    [TestMethod]
    public async Task Invoke_ReResolvesTheElementInsideTheDesktopSection()
    {
        var stale = new UiElement { Id = "btn", Selector = "btn", Name = "Stale", WindowHandle = 4242 };
        var current = new UiElement { Id = "btn", Selector = "btn", Name = "Current", WindowHandle = 4242 };
        _fakeUia.MovingResults["btn"] = new Queue<UiElement?>([stale, current]);
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters);
        Assert.AreSame(current, _fakeUia.LastInvokedElement,
            "invoke must act on the element resolved after the queue wait, not before it");
    }

    [TestMethod]
    public async Task Invoke_RefusesWhenTheTargetWindowClosedWhileQueued()
    {
        _fakeUia.FindSingleResult = new UiElement { Id = "btn", Selector = "btn", Name = "Button", WindowHandle = 4242 };
        _fakeSystemQuery.ProcessIdForWindowResult = 0;

        var command = GetRequiredService<UiInvokeCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["btn", "-a", "TestApp", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertJsonErrorCode("stale_element");
        Assert.IsNull(_fakeUia.LastInvokedElement, "no pattern may be invoked on a dead target");
    }

    [TestMethod]
    public async Task Focus_ReResolvesTheElementInsideTheDesktopSection()
    {
        var stale = new UiElement { Id = "box", Selector = "box", Name = "Stale", WindowHandle = 4242 };
        var current = new UiElement { Id = "box", Selector = "box", Name = "Current", WindowHandle = 4242 };
        _fakeUia.MovingResults["box"] = new Queue<UiElement?>([stale, current]);
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;

        var command = GetRequiredService<UiFocusCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["box", "-a", "TestApp", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters);
        Assert.AreSame(current, _fakeUia.LastFocusedElement);
    }

    // --------------------------------------------------------------- send-keys ordering fix (§13)

    [TestMethod]
    public async Task SendKeys_DoesNotFocusTheTargetWhenForegroundValidationFails()
    {
        // Spec §13: a command that fails foreground validation must not first apply avoidable focus to
        // its target — that alone would dismiss another workflow's transient UI.
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = false;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "send-input", "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsNull(_fakeUia.LastFocusedElement,
            "focus must be applied only after the foreground has been verified");
        Assert.AreEqual(0, _fakeKeyboard.SendCalls.Count);
    }

    [TestMethod]
    public async Task SendKeys_FocusesTheTargetOnlyAfterForegroundIsVerified()
    {
        _fakeUia.FindSingleResult = new UiElement
        {
            Id = "box", Selector = "box", Name = "Box", WindowHandle = 4242,
        };
        _fakeSystemQuery.ProcessIdForWindowResult = 1234;
        _fakeForeground.Allow = true;

        var command = GetRequiredService<UiSendKeysCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["hello", "-a", "TestApp", "--target", "box", "--via", "send-input", "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsNotNull(_fakeUia.LastFocusedElement);
        Assert.AreEqual(1, _fakeKeyboard.SendCalls.Count);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters,
            "revalidate, foreground, focus and send belong to one desktop section");
    }
}
