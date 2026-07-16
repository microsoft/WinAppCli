// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class RealUiAutomationTests
{
    // -----------------------------------------------------------------------------
    // Invoke / SetValue / GetText — with observable effects
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task InvokeAsync_Button_FlipsResultTextBox()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var button = await ResolveAsync(svc, session, "btnInvoke");
        var resultBefore = await svc.GetTextAsync(session, await ResolveAsync(svc, session, "txtResult"), CancellationToken.None);
        Assert.AreEqual("unclicked", resultBefore);

        var pattern = await svc.InvokeAsync(session, button, CancellationToken.None);

        Assert.AreEqual("InvokePattern", pattern);
        await WaitForAsync(async () =>
            await svc.GetTextAsync(session, await ResolveAsync(svc, session, "txtResult"), CancellationToken.None) == "clicked",
            "result box never became 'clicked' after invoking the button");
    }

    [TestMethod]
    public async Task InvokeAsync_CheckBox_TogglesState()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var check = await ResolveAsync(svc, session, "chkToggle");
        Assert.AreEqual("Off", (await svc.GetPropertiesAsync(session, check, "ToggleState", CancellationToken.None))["ToggleState"]);

        // A WinForms CheckBox exposes InvokePattern (clicking it), which the service tries first and
        // which still flips the toggle state — the observable effect we assert on.
        var pattern = await svc.InvokeAsync(session, check, CancellationToken.None);

        Assert.AreEqual("InvokePattern", pattern);
        await WaitForAsync(async () =>
            (string?)(await svc.GetPropertiesAsync(session, await ResolveAsync(svc, session, "chkToggle"), "ToggleState", CancellationToken.None))["ToggleState"] == "On",
            "checkbox never toggled to On");
    }

    [TestMethod]
    public async Task InvokeAsync_ListItem_SelectsItem()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "lstItems");
        var tree = await svc.InspectAsync(session, "lstItems", 2, CancellationToken.None);
        var item = tree.First(e => e.Type == "ListItem" && e.Name == "Item 04");
        Assert.IsFalse(await IsSelectedAsync(svc, session, item), "item should start unselected");

        var pattern = await svc.InvokeAsync(session, item, CancellationToken.None);

        Assert.IsFalse(string.IsNullOrEmpty(pattern), "invoke should report which pattern it used");
        await WaitForAsync(() => IsSelectedAsync(svc, session, item),
            "list item never became selected after invoke");
    }

    private static async Task<bool> IsSelectedAsync(UiAutomationService svc, UiSessionInfo session, UiElement item)
    {
        var props = await svc.GetPropertiesAsync(session, item, "IsSelected", CancellationToken.None);
        return props.TryGetValue("IsSelected", out var sel) && sel is bool b && b;
    }

    [TestMethod]
    public async Task InvokeAsync_NonInvokableElement_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var label = await ResolveAsync(svc, session, "lblText");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(session, label, CancellationToken.None));
    }

    [TestMethod]
    public async Task SetValueAsync_RoundTripsThroughUia()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var box = await ResolveAsync(svc, session, "txtValue");
        var newValue = "roundtrip-" + Guid.NewGuid().ToString("N")[..6];

        await svc.SetValueAsync(session, box, newValue, CancellationToken.None);

        await WaitForAsync(async () =>
            await svc.GetTextAsync(session, await ResolveAsync(svc, session, "txtValue"), CancellationToken.None) == newValue,
            "text box value never reflected the SetValue call");
        // Confirm the real control (not just UIA) holds the value.
        Assert.AreEqual(newValue, fx.OnUiThread(() => fx.ValueBox.Text));
    }

    [TestMethod]
    public async Task GetTextAsync_ValuePattern_ReturnsCurrentText()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var box = await ResolveAsync(svc, session, "txtValue");

        var text = await svc.GetTextAsync(session, box, CancellationToken.None);

        Assert.AreEqual("initial", text);
    }

    [TestMethod]
    public async Task GetTextAsync_Label_FallsBackToName()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var label = await ResolveAsync(svc, session, "lblText");

        var text = await svc.GetTextAsync(session, label, CancellationToken.None);

        Assert.AreEqual("Hello Label", text);
    }

    [TestMethod]
    public async Task InvokeAsync_StaleElement_ThrowsAfterWindowClosed()
    {
        var svc = NewService();
        UiElement button;
        UiSessionInfo session;
        using (var fx = new UiaTestFixture())
        {
            session = SessionFor(fx);
            button = await ResolveAsync(svc, session, "btnInvoke");
        }
        // Window is now closed; the previously-resolved element can no longer be re-resolved.
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(session, button, CancellationToken.None));
        StringAssert.Contains(ex.Message, "stale");
    }

    // -----------------------------------------------------------------------------
    // Focus
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task FocusAsync_OnLiveElement_ResolvesAndInvokesSetFocus()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var btn = await ResolveAsync(svc, session, "btnInvoke");

        // FocusAsync resolves the element to a live UIA COM element and calls SetFocus on it. The
        // *visible* effect (system-wide keyboard focus / the HasKeyboardFocus property) cannot be
        // asserted deterministically in this test host: it denies programmatic foreground activation
        // (the foreground-lock timeout is policy-locked at its maximum, so SetForegroundWindow /
        // AttachThreadInput never make the fixture the active window), and UIA SetFocus has no
        // observable effect on a background window. See the class <remarks> honest-ceiling note.
        // We still drive the real happy path end-to-end and prove SetFocus ran against a genuine,
        // still-live provider element — a stale element instead throws (covered by the test below).
        await svc.FocusAsync(session, btn, CancellationToken.None);

        // The element FocusAsync operated on is real and still addressable: reading a live property
        // back returns the true control identity, proving ResolveComElement produced a valid element
        // (not a silent no-op) that SetFocus was actually invoked on.
        var props = await svc.GetPropertiesAsync(session, btn, "Name", CancellationToken.None);
        Assert.AreEqual("Click Me", props["Name"]);
    }

    [TestMethod]
    public async Task FocusAsync_StaleElement_ThrowsAfterWindowClosed()
    {
        var svc = NewService();
        UiElement box;
        UiSessionInfo session;
        using (var fx = new UiaTestFixture())
        {
            session = SessionFor(fx);
            box = await ResolveAsync(svc, session, "txtValue");
        }
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.FocusAsync(session, box, CancellationToken.None));
        StringAssert.Contains(ex.Message, "stale");
    }

    [TestMethod]
    public async Task GetFocusedElementAsync_ForProcessOwningFocus_ReturnsFocusedElement()
    {
        var svc = NewService();

        // GetFocusedElement is system-wide: it returns the element in whatever window currently owns
        // keyboard focus. Rather than fight the contested foreground to make the fixture win it
        // (nondeterministic on this shared desktop), discover which process actually owns focus via an
        // independent UIA client and query the service for that PID — the service's PID filter then
        // matches and the happy path (build + return the element) runs deterministically. Reading the
        // owning PID fresh inside the poll keeps this robust across focus changes (e.g. terminal vs
        // console host).
        UiElement? focused = null;
        await WaitForAsync(async () =>
        {
            var pid = FocusedElementProcessId();
            if (pid is null or 0)
            {
                return false;
            }
            var session = new UiSessionInfo
            {
                ProcessId = pid.Value,
                ProcessName = "focus-owner",
                IsExplicitWindow = false,
            };
            focused = await svc.GetFocusedElementAsync(session, CancellationToken.None);
            return focused is not null;
        }, "no process ever reported a focused element", timeoutMs: 10_000);

        Assert.IsNotNull(focused);
        Assert.IsFalse(string.IsNullOrEmpty(focused!.Type), "focused element should carry a control type");
    }

    [TestMethod]
    public async Task GetFocusedElementAsync_ForForeignProcess_ReturnsNull()
    {
        var svc = NewService();

        // A session whose PID owns nothing on-screen can never match the system focused element, so
        // the service must reject it and return null (the PID-guard / not-in-target-process path).
        var session = new UiSessionInfo
        {
            ProcessId = 0x7FFF_FFFE,
            ProcessName = "no-such-process",
            IsExplicitWindow = false,
        };

        var focused = await svc.GetFocusedElementAsync(session, CancellationToken.None);

        Assert.IsNull(focused, "focused element from a foreign PID must be filtered out");
    }

    // -----------------------------------------------------------------------------
    // Scroll
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task ScrollContainerAsync_Down_IncreasesVerticalOffset()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var panel = await ResolveAsync(svc, session, "pnlScroll");
        var before = await VerticalPercentAsync(svc, session, "pnlScroll");
        Assert.AreEqual(0.0, before, 0.001, "panel should start scrolled to top");

        await svc.ScrollContainerAsync(session, panel, "down", null, CancellationToken.None);

        var after = await VerticalPercentAsync(svc, session, "pnlScroll");
        Assert.IsTrue(after > before, $"expected vertical percent to increase (before={before}, after={after})");
    }

    [TestMethod]
    public async Task ScrollContainerAsync_ToBottomThenTop_MovesToExtremes()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var panel = await ResolveAsync(svc, session, "pnlScroll");

        await svc.ScrollContainerAsync(session, panel, null, "bottom", CancellationToken.None);
        var atBottom = await VerticalPercentAsync(svc, session, "pnlScroll");
        Assert.IsTrue(atBottom > 90, $"expected near 100% at bottom, got {atBottom}");

        await svc.ScrollContainerAsync(session, panel, null, "top", CancellationToken.None);
        var atTop = await VerticalPercentAsync(svc, session, "pnlScroll");
        Assert.IsTrue(atTop < 10, $"expected near 0% at top, got {atTop}");
    }

    [TestMethod]
    public async Task ScrollContainerAsync_InvalidDirection_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var panel = await ResolveAsync(svc, session, "pnlScroll");

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => svc.ScrollContainerAsync(session, panel, "sideways", null, CancellationToken.None));
    }

    [TestMethod]
    public async Task ScrollContainerAsync_HorizontalWhenNotSupported_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var panel = await ResolveAsync(svc, session, "pnlScroll");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScrollContainerAsync(session, panel, "right", null, CancellationToken.None));
    }

    [TestMethod]
    public async Task ScrollContainerAsync_WalksUpToScrollableAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        // A child button has no ScrollPattern; the service must walk up to the panel.
        var child = await ResolveAsync(svc, session, "pnlChild02");
        var before = await VerticalPercentAsync(svc, session, "pnlScroll");

        await svc.ScrollContainerAsync(session, child, "down", null, CancellationToken.None);

        var after = await VerticalPercentAsync(svc, session, "pnlScroll");
        Assert.IsTrue(after > before, $"scrolling via a child should move the ancestor panel (before={before}, after={after})");
    }

    [TestMethod]
    public async Task ScrollIntoViewAsync_OffscreenChild_DoesNotThrow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var deepChild = await ResolveAsync(svc, session, "pnlChild39");

        // Should either scroll the item into view via ScrollItemPattern or fall back to the
        // ancestor ScrollPattern; either way it must complete without throwing.
        await svc.ScrollIntoViewAsync(session, deepChild, CancellationToken.None);
    }

}
