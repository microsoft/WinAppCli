// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// End-to-end coverage of the real <see cref="UiAutomationService"/> (and its Screenshot /
/// ValueSetStrategy partials) driven against a live in-process WinForms window
/// (<see cref="UiaTestFixture"/>). These are meaningful workflow tests: every method is exercised
/// against genuine UIA providers and asserts a real, observable result — invoking a button flips a
/// text box, setting a value round-trips through UIA, scrolling changes the real scroll offset,
/// screenshots produce non-blank correctly-sized frames, and stale elements raise the real error.
///
/// The whole class is <see cref="DoNotParallelizeAttribute"/> because several tests move the
/// foreground window and/or capture the screen — process-global state that must not race with other
/// tests. Each test owns its fixture window via <c>using</c> so there is no shared mutable state.
/// </summary>
/// <remarks>
/// Host-dependent GPU/Media Foundation ceilings are documented on the innermost native product
/// methods themselves. These tests deliberately avoid line-numbered ceiling notes so future edits
/// cannot leave stale ranges behind.
/// </remarks>
[TestClass]
[DoNotParallelize]
public partial class RealUiAutomationTests
{
    private const int ReadyTimeoutMs = 10_000;
    private const int EffectTimeoutMs = 5_000;

    /// <summary>
    /// Real UIA requires an interactive desktop to host the fixture window and drive providers.
    /// On an interactive host (the coverage host, and any dev box) every test runs for real and
    /// counts toward coverage; on a headless/service (non-interactive) CI agent the whole class
    /// skips cleanly via <see cref="Assert.Inconclusive(string)"/> instead of hard-failing, so it
    /// never blocks CI. This is a safety gate only — it does not suppress or fake any assertion.
    /// </summary>
    [TestInitialize]
    public void RequireInteractiveDesktop()
    {
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("Skipped: real UI Automation needs an interactive desktop session (none present on this host).");
        }
    }

    [TestCleanup]
    public void ResetNativeSeams()
    {
        UiAutomationService.ResetNativeSeams();
        WgcCapture.s_isSupported = Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported;
        WgcCapture.s_startGrabber = (hwnd, logger, fps) => WgcCapture.StartGrabber(hwnd, logger, fps);
        UiAutomationService.s_captureFromWindow = (Func<Windows.Win32.Foundation.HWND, int, int, byte[]>)Delegate.CreateDelegate(
            typeof(Func<Windows.Win32.Foundation.HWND, int, int, byte[]>),
            typeof(UiAutomationService).GetMethod(
                "CaptureFromWindow",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        UiAutomationService.s_captureFromScreenScaled = (Func<int, int, int, int, int, int, byte[]>)Delegate.CreateDelegate(
            typeof(Func<int, int, int, int, int, int, byte[]>),
            typeof(UiAutomationService).GetMethod(
                "CaptureFromScreenScaled",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        Mp4SinkWriterEncoder.s_create = (path, width, height, fps, bitrate) => new Mp4SinkWriterEncoder(path, width, height, fps, bitrate);
    }

    private static UiAutomationService NewService()
        => new(NullLogger<UiAutomationService>.Instance, new SelectorService());

    private static UiSessionInfo SessionFor(UiaTestFixture fx, bool explicitWindow = true) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.Cli.Tests",
        WindowHandle = fx.Hwnd,
        WindowTitle = fx.Title,
        IsExplicitWindow = explicitWindow,
    };

    /// <summary>Polls until the given AutomationId is resolvable via the real service (UIA ready).</summary>
    private static async Task<UiElement> ResolveAsync(UiAutomationService svc, UiSessionInfo session, string automationId)
    {
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var el = await svc.FindSingleElementAsync(session, new SelectorExpression { Query = automationId }, CancellationToken.None);
            if (el is not null)
            {
                return el;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"Element '{automationId}' never became resolvable via UIA.");
    }

    // -----------------------------------------------------------------------------
    // Window enumeration
    // -----------------------------------------------------------------------------

    [TestMethod]
    public void FindWindowsByTitle_FindsFixtureWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        var windows = svc.FindWindowsByTitle(fx.Title);

        Assert.IsTrue(windows.Any(w => w.Hwnd == fx.Hwnd),
            $"Expected FindWindowsByTitle('{fx.Title}') to include the fixture HWND {fx.Hwnd}.");
        Assert.IsTrue(windows.All(w => w.Title.Contains(fx.Title, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void FindWindowsByPid_FindsFixtureWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        var windows = svc.FindWindowsByPid(fx.ProcessId);

        Assert.IsTrue(windows.Any(w => w.Hwnd == fx.Hwnd && w.Pid == fx.ProcessId),
            "Expected FindWindowsByPid to include the fixture window.");
    }

    [TestMethod]
    public void FindWindowsByTitle_EmptyQuery_ReturnsAllVisible()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        var windows = svc.FindWindowsByTitle(string.Empty);

        Assert.IsTrue(windows.Any(w => w.Hwnd == fx.Hwnd),
            "An empty title query must match all visible windows, including the fixture.");
    }

    [TestMethod]
    public void TryGetWindowRect_ReturnsTopLevelBoundsForFixture()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();

        Assert.IsFalse(svc.TryGetWindowRect(0, out _), "zero HWND should be rejected");
        Assert.IsTrue(svc.TryGetWindowRect(fx.Hwnd, out var rect), "fixture HWND should have a window rect");
        Assert.IsTrue(rect.Right > rect.Left);
        Assert.IsTrue(rect.Bottom > rect.Top);
    }

    // -----------------------------------------------------------------------------
    // Inspect
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task InspectAsync_ReturnsKnownControls()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var tree = await svc.InspectAsync(session, null, 4, CancellationToken.None);

        Assert.IsTrue(tree.Any(e => e.Type == "Window" && e.Depth == 0), "root Window expected at depth 0");
        Assert.IsTrue(tree.Any(e => e.AutomationId == "btnInvoke" && e.Type == "Button" && e.Depth == 1));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "txtValue" && e.Type == "Edit" && e.Depth == 1));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "chkToggle" && e.Type == "CheckBox" && e.Depth == 1));
        Assert.IsTrue(tree.Any(e => e.AutomationId == "lstItems" && e.Type == "List"));
        // Nested list items live at depth 2 under the list.
        Assert.IsTrue(tree.Any(e => e.Type == "ListItem" && e.Name == "Item 05" && e.Depth == 2));
    }

    [TestMethod]
    public async Task InspectAsync_DepthZero_SetsHasMoreChildren()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var tree = await svc.InspectAsync(session, null, 0, CancellationToken.None);

        Assert.AreEqual(1, tree.Length, "depth 0 must return only the root element");
        Assert.AreEqual("Window", tree[0].Type);
        Assert.IsTrue(tree[0].HasMoreChildren == true, "root has children, so HasMoreChildren must be set");
    }

    [TestMethod]
    public async Task InspectAsync_ScopedToElement_ReturnsSubtree()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "pnlScroll");

        var tree = await svc.InspectAsync(session, "pnlScroll", 3, CancellationToken.None);

        Assert.AreEqual("Pane", tree[0].Type, "scoped inspect should root at the panel");
        Assert.AreEqual("pnlScroll", tree[0].AutomationId);
        Assert.IsTrue(tree.Any(e => e.AutomationId == "pnlChild00"), "panel children should be present");
        Assert.IsFalse(tree.Any(e => e.AutomationId == "btnInvoke"), "controls outside the panel must be excluded");
    }

    [TestMethod]
    public async Task InspectAncestorsAsync_ReturnsRootToTargetChain()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var chain = await svc.InspectAncestorsAsync(session, "btnInvoke", CancellationToken.None);

        Assert.IsTrue(chain.Length >= 2, "expected at least the window and the button");
        Assert.AreEqual("btnInvoke", chain[^1].AutomationId, "target should be last (deepest) in the chain");
        Assert.IsTrue(chain.Any(e => e.Type == "Window"), "the window ancestor should be present");
    }

    [TestMethod]
    public async Task InspectAncestorsAsync_NotFound_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InspectAncestorsAsync(session, "no-such-element-xyz", CancellationToken.None));
    }

    [TestMethod]
    public async Task InspectAsync_NoWindow_ReturnsEmpty()
    {
        var svc = NewService();
        // A session pointing at a non-existent window/process yields no root -> empty result.
        var session = new UiSessionInfo { ProcessId = 0x7FFFFFFE, WindowHandle = 0, IsExplicitWindow = true };

        var tree = await svc.InspectAsync(session, null, 3, CancellationToken.None);

        Assert.AreEqual(0, tree.Length);
    }

    [TestMethod]
    public async Task InspectAsync_ScopedToSlug_ReturnsSubtreeOfThatElement()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);

        // First discover the scroll panel's slug from a full inspect, then scope a second inspect to
        // that slug. Passing a slug (not a legacy selector) exercises the slug-scope branch of
        // InspectAsync (ParseSlug -> FindElementBySlug -> ResolveComElement).
        var full = await svc.InspectAsync(session, null, 4, CancellationToken.None);
        var panel = full.First(e => e.AutomationId == "pnlScroll");
        Assert.IsNotNull(panel.Selector, "expected a promoted slug/selector on the scroll panel");

        var subtree = await svc.InspectAsync(session, panel.Selector, 3, CancellationToken.None);

        // The scoped walk starts at the panel: its scrollable children are present, but unrelated
        // top-level controls (the invoke button) are not part of this subtree.
        Assert.IsTrue(subtree.Any(e => e.AutomationId != null && e.AutomationId.StartsWith("pnlChild", StringComparison.Ordinal)),
            "scoped subtree should contain the panel's children");
        Assert.IsFalse(subtree.Any(e => e.AutomationId == "btnInvoke"),
            "scoped subtree should not contain controls outside the panel");
    }

    [TestMethod]
    public async Task InspectAsync_NonExplicitSession_IncludesIndependentTopLevelWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = NonExplicitSession(fx);
        fx.OpenOwnedWindow("Fixture Sibling Window");

        // With a non-explicit session and a second independent (non-owned) top-level window open, a
        // full-tree inspect walks the popup/other windows too: it inserts a main-window header
        // separator and a separator + subtree for the sibling window (GetAllAppWindows,
        // GetRootElementForHwnd, the WalkTree over popup elements). Poll until UIA registers the
        // sibling window, then assert both the separator and the sibling's own control are present.
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        UiElement[] tree = [];
        while (Environment.TickCount64 < deadline)
        {
            tree = await svc.InspectAsync(session, null, 6, CancellationToken.None);
            if (tree.Any(e => e.AutomationId == "btnOwned"))
            {
                break;
            }
            await Task.Delay(150);
        }

        Assert.IsTrue(tree.Any(e => e.AutomationId == "btnOwned"),
            "the sibling window's button should appear in the multi-window inspect tree");
        Assert.IsTrue(tree.Any(e => e.Type == "---" && e.Name != null && e.Name.Contains("Fixture Sibling Window", StringComparison.Ordinal)),
            "a separator element naming the sibling window should be inserted");
        Assert.IsTrue(tree.Any(e => e.Type == "---" && e.Id != null && e.Id.StartsWith("--- HWND", StringComparison.Ordinal)),
            "a main-window header separator should be inserted when other windows exist");
    }

    // -----------------------------------------------------------------------------
    // Search / FindSingle
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchAsync_ByAutomationId_FindsButton()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var results = await svc.SearchAsync(session, new SelectorExpression { Query = "btnInvoke" }, 10, CancellationToken.None);

        Assert.AreEqual(1, results.Length);
        Assert.AreEqual("Button", results[0].Type);
        Assert.AreEqual("btnInvoke", results[0].AutomationId);
    }

    [TestMethod]
    public async Task SearchAsync_BySubstring_FindsManyChildren()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "pnlChild00");

        var results = await svc.SearchAsync(session, new SelectorExpression { Query = "Child 1" }, 50, CancellationToken.None);

        // "Child 1" matches Child 10..19 by name (10 controls).
        Assert.IsTrue(results.Length >= 10, $"expected >=10 matches, got {results.Length}");
        Assert.IsTrue(results.All(r => r.Type == "Button"));
    }

    [TestMethod]
    public async Task SearchAsync_NoMatch_ReturnsEmpty()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var results = await svc.SearchAsync(session, new SelectorExpression { Query = "zzz-does-not-exist" }, 10, CancellationToken.None);

        Assert.AreEqual(0, results.Length);
    }

    [TestMethod]
    public async Task SearchAsync_NonInvokableLabel_ReturnsElementWithoutInvokableAncestor()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "lblText");

        // A Label supports no InvokePattern, so the FindAll loop takes the !IsInvokable branch and
        // looks for an invokable ancestor. The label sits directly on the form (no invokable parent),
        // so the element is returned with a null InvokableAncestor.
        var results = await svc.SearchAsync(session, new SelectorExpression { Query = "Hello Label" }, 10, CancellationToken.None);

        var label = results.SingleOrDefault(r => r.AutomationId == "lblText");
        Assert.IsNotNull(label, "expected the non-invokable label to be returned by search");
        Assert.IsNull(label.InvokableAncestor, "a form-level label has no invokable ancestor");
    }

    [TestMethod]
    public async Task SearchAsync_NonExplicitSession_FindsElementByNameOnOwnedWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        fx.OpenOwnedWindow("Fixture Owned Window");
        var session = NonExplicitSession(fx);

        // "Owned Button" is the accessible Name (not the AutomationId), so the exact-AutomationId probe
        // on the owned window misses and the search falls through to the substring BuildCondition
        // branch of the owned-window search loop.
        var results = await PollSearchAsync(svc, session, "Owned Button");

        Assert.IsTrue(results.Any(r => r.AutomationId == "btnOwned"),
            "expected the owned window's button to be found via the substring branch");
    }

    [TestMethod]
    public async Task FindSingleElementAsync_BySlug_ResolvesElement()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        // Get a slug from a list item (list items have no AutomationId, so they get slug selectors).
        await ResolveAsync(svc, session, "lstItems");
        var tree = await svc.InspectAsync(session, "lstItems", 2, CancellationToken.None);
        var item = tree.First(e => e.Type == "ListItem" && e.Name == "Item 07");
        Assert.IsNotNull(item.Selector);

        var found = await svc.FindSingleElementAsync(session, new SelectorExpression { Slug = item.Selector }, CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual("Item 07", found.Name);
    }

    [TestMethod]
    public async Task FindSingleElementAsync_NotFound_ReturnsNull()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var found = await svc.FindSingleElementAsync(session, new SelectorExpression { Query = "nope-xyz-123" }, CancellationToken.None);

        Assert.IsNull(found);
    }

    [TestMethod]
    public async Task FindSingleElementAsync_Ambiguous_Throws()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "pnlChild00");

        // "Child" matches 40 buttons by name; all are invokable so it cannot be disambiguated.
        var ex = await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
            () => svc.FindSingleElementAsync(session, new SelectorExpression { Query = "Child" }, CancellationToken.None));
        StringAssert.Contains(ex.Message, "matched");
    }

    // -----------------------------------------------------------------------------
    // GetProperties
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task GetPropertiesAsync_ReturnsRealProperties()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var check = await ResolveAsync(svc, session, "chkToggle");

        var props = await svc.GetPropertiesAsync(session, check, null, CancellationToken.None);

        Assert.AreEqual("CheckBox", props["ControlType"]);
        Assert.AreEqual("Toggle Me", props["Name"]);
        Assert.AreEqual("chkToggle", props["AutomationId"]);
        Assert.AreEqual("Off", props["ToggleState"]);
        Assert.IsTrue((bool)props["IsEnabled"]!);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_SpecificProperty_ReturnsOnlyThat()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var btn = await ResolveAsync(svc, session, "btnInvoke");

        var props = await svc.GetPropertiesAsync(session, btn, "Name", CancellationToken.None);

        Assert.AreEqual(1, props.Count);
        Assert.AreEqual("Click Me", props["Name"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_UnknownProperty_ReturnsNull()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var btn = await ResolveAsync(svc, session, "btnInvoke");

        var props = await svc.GetPropertiesAsync(session, btn, "NoSuchProperty", CancellationToken.None);

        Assert.AreEqual(1, props.Count);
        Assert.IsNull(props["NoSuchProperty"]);
    }

    [TestMethod]
    public async Task GetPropertiesAsync_ScrollableContainer_ReportsScrollProps()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        var panel = await ResolveAsync(svc, session, "pnlScroll");

        var props = await svc.GetPropertiesAsync(session, panel, null, CancellationToken.None);

        Assert.IsTrue(props.ContainsKey("VerticallyScrollable"));
        Assert.IsTrue(props.ContainsKey("ScrollVerticalPercent"));
        Assert.AreEqual(0.0,
            Convert.ToDouble(props["ScrollVerticalPercent"], System.Globalization.CultureInfo.InvariantCulture),
            0.001,
            "panel should start scrolled to the top");
    }

}
