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
    // Screenshots (Screenshot partial + WgcCapture)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task ScreenshotAsync_Window_ProducesNonBlankImage()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        Foreground(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var (pixels, width, height) = await CaptureNonBlankAsync(fx,
            () => svc.ScreenshotAsync(session, null, captureScreen: false, focus: true, CancellationToken.None));

        AssertNonBlankRenderOrInconclusive(pixels);
        Assert.IsTrue(width > 100 && height > 100, $"unexpected capture size {width}x{height}");
        Assert.AreEqual(width * height * 4, pixels.Length, "pixel buffer size must match dimensions (BGRA)");
    }

    [TestMethod]
    public async Task ScreenshotAsync_CaptureScreen_ProducesNonBlankImage()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        Foreground(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var (pixels, width, height) = await CaptureNonBlankAsync(fx,
            () => svc.ScreenshotAsync(session, null, captureScreen: true, focus: false, CancellationToken.None));

        AssertNonBlankRenderOrInconclusive(pixels);
        Assert.IsTrue(width > 100 && height > 100, $"unexpected capture size {width}x{height}");
        Assert.AreEqual(width * height * 4, pixels.Length);
    }

    [TestMethod]
    public async Task ScreenshotAsync_ElementCrop_ReturnsElementSizedImage()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        Foreground(fx);
        var button = await ResolveAsync(svc, session, "btnInvoke");

        var (pixels, width, height) = await svc.ScreenshotAsync(session, button.Selector ?? "btnInvoke", captureScreen: false, focus: true, CancellationToken.None);

        // The button is ~120x30; the crop should be far smaller than the whole window.
        Assert.IsTrue(width > 10 && width < 300, $"unexpected crop width {width}");
        Assert.IsTrue(height > 5 && height < 200, $"unexpected crop height {height}");
        Assert.AreEqual(width * height * 4, pixels.Length);
    }

    [TestMethod]
    public async Task ScreenshotAsync_NoWindow_Throws()
    {
        var svc = NewService();
        var session = new UiSessionInfo { ProcessId = 0x7FFFFFFE, WindowHandle = 0, IsExplicitWindow = true };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.ScreenshotAsync(session, null, captureScreen: false, focus: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task ScreenshotAsync_MinimizedWindow_RestoresThenCaptures()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        // Minimize the window: ScreenshotAsync detects IsIconic and restores it (SW_RESTORE) before
        // measuring/capturing. Call the service directly (not through the foregrounding retry helper)
        // so the window is still iconic when the service inspects it. After the call the window must
        // no longer be minimized and the frame must be correctly sized.
        fx.OnUiThread(() => fx.Form.WindowState = FormWindowState.Minimized);
        await Task.Delay(200);

        var (pixels, width, height) = await svc.ScreenshotAsync(session, null, captureScreen: false, focus: true, CancellationToken.None);

        Assert.IsTrue(width > 100 && height > 100, $"unexpected capture size {width}x{height}");
        Assert.AreEqual(width * height * 4, pixels.Length);
        Assert.IsFalse(fx.OnUiThread(() => fx.Form.WindowState == FormWindowState.Minimized),
            "the window should have been restored from minimized before capture");
    }

    [TestMethod]
    public async Task ScreenshotAsync_LegacySelectorCrop_ReturnsElementSizedImage()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        Foreground(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        // A bare automation id (not a slug) drives CropToElement's legacy-selector branch:
        // _selectorService.Parse -> BuildCondition -> root.FindFirst, then crop to the element rect.
        var (pixels, width, height) = await svc.ScreenshotAsync(session, "btnInvoke", captureScreen: false, focus: true, CancellationToken.None);

        Assert.IsTrue(width > 10 && width < 300, $"unexpected crop width {width}");
        Assert.IsTrue(height > 5 && height < 200, $"unexpected crop height {height}");
        Assert.AreEqual(width * height * 4, pixels.Length);
    }

    [TestMethod]
    public async Task ScreenshotAsync_MissingElement_ReturnsFullFrame()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        Foreground(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        // A selector that matches nothing makes CropToElement return null, so ScreenshotAsync falls
        // back to returning the full window frame rather than a crop.
        var full = await CaptureNonBlankAsync(fx,
            () => svc.ScreenshotAsync(session, null, captureScreen: false, focus: true, CancellationToken.None));
        var (pixels, width, height) = await svc.ScreenshotAsync(session, "no-such-control-zzz", captureScreen: false, focus: true, CancellationToken.None);

        Assert.AreEqual(full.Width, width, "missing-element crop should yield the full-frame width");
        Assert.AreEqual(full.Height, height, "missing-element crop should yield the full-frame height");
        Assert.AreEqual(width * height * 4, pixels.Length);
    }

    // -----------------------------------------------------------------------------
    // Owned / popup windows (non-explicit session) — GetAllAppWindows,
    // FindElementOnOtherWindows, GetRootElementForHwnd, ResolveComElement-by-hwnd
    // -----------------------------------------------------------------------------

    [TestMethod]
    public async Task SearchAsync_NonExplicitSession_FindsElementOnOwnedWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        fx.OpenOwnedWindow("Fixture Owned Window");
        var session = NonExplicitSession(fx);

        // "btnOwned" exists only on the owned window, so the main-window search misses and the
        // service must enumerate app windows and search the owned one.
        var results = await PollSearchAsync(svc, session, "btnOwned");

        Assert.IsTrue(results.Any(r => r.AutomationId == "btnOwned" && r.Type == "Button"),
            "expected the owned window's button to be found via the popup/owned-window search path");
    }

    [TestMethod]
    public async Task FindSingleElementAsync_NonExplicitSession_FindsControlOnOwnedWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        fx.OpenOwnedWindow("Fixture Owned Window");
        var session = NonExplicitSession(fx);

        var found = await PollFindOtherWindowAsync(svc, session, "btnOwned");

        Assert.IsNotNull(found);
        Assert.AreEqual("btnOwned", found.AutomationId);
        Assert.AreNotEqual(fx.Hwnd, (nint)found.WindowHandle!.Value, "element should carry the owned window's HWND");
    }

    [TestMethod]
    public async Task FindSingleElementAsync_NonExplicitSession_BySubstringOnOwnedWindow()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        fx.OpenOwnedWindow("Fixture Owned Window");
        var session = NonExplicitSession(fx);

        // "Owned Button" is the accessible Name (not the AutomationId) — forces the substring branch
        // of the owned-window search.
        var found = await PollFindOtherWindowAsync(svc, session, "Owned Button");

        Assert.IsNotNull(found);
        Assert.AreEqual("btnOwned", found.AutomationId);
    }

    [TestMethod]
    public async Task InvokeAsync_ElementOnOwnedWindow_ResolvesViaSourceHwnd()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        fx.OpenOwnedWindow("Fixture Owned Window");
        var session = NonExplicitSession(fx);
        var owned = await PollFindOtherWindowAsync(svc, session, "btnOwned");

        // The element's WindowHandle differs from the session window, so ResolveComElement must
        // re-root on the element's source HWND before invoking.
        var pattern = await svc.InvokeAsync(session, owned, CancellationToken.None);

        Assert.AreEqual("InvokePattern", pattern);
    }

    [TestMethod]
    public async Task FindSingleElementAsync_PidOnlySession_ResolvesViaProcessAndTitle()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        // A second same-PID top-level window makes the PID search return >1 element, exercising the
        // title-match disambiguation inside GetRootElement's WindowHandle==0 branch.
        fx.OpenOwnedWindow("Fixture Owned Window");
        var session = PidOnlySession(fx);

        var found = await ResolveAsync(svc, session, "btnInvoke");

        Assert.AreEqual("btnInvoke", found.AutomationId);
    }

}
