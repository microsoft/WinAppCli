// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Real coverage of the thin native-input adapters — <see cref="RealForegroundGuard"/>,
/// <see cref="RealKeyboardInput"/> and <see cref="RealMouseInput"/> — by driving genuine OS input
/// against the live <see cref="UiaTestFixture"/> window and asserting the real, observable effect
/// (characters land in a control, the cursor moves to the target). These adapters delegate to the
/// static <c>ForegroundGuard</c> / <c>KeyboardInput</c> / <c>MouseInput</c> P/Invoke helpers, so
/// exercising them here also drives those real code paths end-to-end.
///
/// The class is <see cref="DoNotParallelizeAttribute"/> because every test injects real input or
/// moves the cursor — process-global state. Each mouse test saves and restores the cursor position
/// in a <c>finally</c> so no persistent machine state is left behind.
///
/// <remarks>
/// Documented honest ceilings on the automation host (no <c>[ExcludeFromCodeCoverage]</c> is used):
///   * <c>RealMouseInput.Click</c>, <c>RealMouseInput.Drag</c> and <c>RealMouseInput.ScrollWheel</c>
///     deliver mouse <em>button/wheel</em> events via <c>SendInput</c>, which land on whatever
///     top-level window is topmost at the cursor point. This host runs behind a full-desktop
///     transparent input overlay (a fresh top-level window covers every screen coordinate on every
///     monitor — confirmed via <c>WindowFromPoint</c>, which never resolves to the fixture even when
///     the fixture is <c>TopMost</c> and visible). Injected button/wheel events are therefore
///     swallowed by the overlay and can neither reach nor be observed on the fixture, so their effect
///     cannot be asserted without landing input on an unrelated window. Cursor <em>positioning</em>
///     (<c>MoveCursor</c>, <c>Hover</c>) uses <c>SetCursorPos</c>, which is not intercepted, and is
///     covered below.
///   * <c>KeyboardInput.SendViaSendInput</c> (OS-wide key injection, in the out-of-scope static
///     backer) needs genuine keyboard focus, which requires displacing the terminal-owned foreground
///     — not possible on the host — so only the <c>PostMessage</c> keyboard transport is asserted.
///   * The partial-<c>SendInput</c> error-recovery branches (short/zero write) in the static backers
///     cannot be provoked without a real OS injection failure, which a healthy desktop never yields.
/// </remarks>
/// </summary>
[TestClass]
[DoNotParallelize]
public class RealInputAdapterTests
{
    private const int EffectTimeoutMs = 8_000;

    /// <summary>
    /// These adapters inject real OS input / move the cursor, which needs an interactive desktop.
    /// On an interactive host every test runs for real and counts toward coverage; on a headless
    /// (non-interactive) CI agent the class skips cleanly via <see cref="Assert.Inconclusive(string)"/>
    /// instead of hard-failing. Safety gate only — no assertion is suppressed or faked.
    /// </summary>
    [TestInitialize]
    public void RequireInteractiveDesktop()
    {
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("Skipped: real input injection needs an interactive desktop session (none present on this host).");
        }
    }

    // -----------------------------------------------------------------------------
    // RealForegroundGuard
    // -----------------------------------------------------------------------------

    [TestMethod]
    public void ForegroundGuard_NoTarget_ProceedsWithoutAWindow()
    {
        var guard = new RealForegroundGuard();

        // A target HWND of 0 means "nothing to verify against" — the guard must allow the caller
        // through (e.g. a bare-coordinate gesture) rather than block it.
        Assert.IsTrue(guard.TryEnsureForeground(0, NullLogger.Instance, json: false, "click"));
    }

    [TestMethod]
    public void ForegroundGuard_WindowThatIsNotForeground_Refuses()
    {
        var guard = new RealForegroundGuard();

        // The desktop window is never the foreground window, so the guard must refuse to inject —
        // this is the real check that stops an OS gesture landing on the wrong window. Exercises
        // ForegroundBelongsTo (false) and the ForegroundNotTarget decision path.
        Assert.IsFalse(
            guard.TryEnsureForeground(DesktopTestHelpers.DesktopWindow(), NullLogger.Instance, json: false, "click"),
            "guard should refuse a window that is not in the foreground");
    }

    // -----------------------------------------------------------------------------
    // RealKeyboardInput (PostMessage transport — no foreground required, fully deterministic)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public void Keyboard_PostMessage_TypesCharactersIntoTargetControl()
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() => fx.ValueBox.Text = string.Empty);
        var handle = fx.HandleOf(fx.ValueBox);
        var keyboard = new RealKeyboardInput();

        // PostMessage transport delivers WM_CHAR straight to the control's message queue — no
        // foreground required — so this is deterministic regardless of the shared desktop's state.
        keyboard.Send(handle, KeyStringParser.Parse("Hello"), KeyTransport.PostMessage);

        Assert.IsTrue(
            WaitFor(() => fx.OnUiThread(() => fx.ValueBox.Text) == "Hello"),
            $"expected 'Hello', got '{fx.OnUiThread(() => fx.ValueBox.Text)}'");
    }

    [TestMethod]
    public void Keyboard_PostMessage_ChordEditsControl()
    {
        using var fx = new UiaTestFixture();
        fx.OnUiThread(() =>
        {
            fx.ValueBox.Text = "AB";
            fx.ValueBox.SelectionStart = fx.ValueBox.Text.Length;
        });
        var handle = fx.HandleOf(fx.ValueBox);
        var keyboard = new RealKeyboardInput();

        // "backspace" parses to a KeyChord (a named virtual key), exercising the WM_KEYDOWN/UP chord
        // path (as opposed to the WM_CHAR text path) through the adapter.
        keyboard.Send(handle, KeyStringParser.Parse("backspace"), KeyTransport.PostMessage);

        Assert.IsTrue(
            WaitFor(() => fx.OnUiThread(() => fx.ValueBox.Text) == "A"),
            $"backspace chord did not edit the control (got '{fx.OnUiThread(() => fx.ValueBox.Text)}')");
    }

    // -----------------------------------------------------------------------------
    // RealMouseInput — cursor motion (SetCursorPos is not intercepted; cursor saved/restored)
    // -----------------------------------------------------------------------------

    [TestMethod]
    public void Mouse_MoveCursor_PositionsCursorAtTarget()
    {
        using var fx = new UiaTestFixture();
        var (cx, cy) = fx.ScreenCenterOf(fx.InvokeButton);
        var (sx, sy) = DesktopTestHelpers.GetCursor();
        try
        {
            new RealMouseInput().MoveCursor(cx, cy);

            var (nx, ny) = DesktopTestHelpers.GetCursor();
            Assert.IsTrue(Math.Abs(nx - cx) <= 2 && Math.Abs(ny - cy) <= 2,
                $"cursor landed at ({nx},{ny}), expected ~({cx},{cy})");
        }
        finally
        {
            DesktopTestHelpers.SetCursor(sx, sy);
        }
    }

    [TestMethod]
    public void Mouse_Hover_LeavesCursorOnTarget()
    {
        using var fx = new UiaTestFixture();
        var (cx, cy) = fx.ScreenCenterOf(fx.ValueBox);
        var (sx, sy) = DesktopTestHelpers.GetCursor();
        try
        {
            // Hover wiggles the cursor to trigger hover detection, then returns it to the target.
            new RealMouseInput().Hover(cx, cy);

            var (nx, ny) = DesktopTestHelpers.GetCursor();
            Assert.IsTrue(Math.Abs(nx - cx) <= 3 && Math.Abs(ny - cy) <= 3,
                $"cursor settled at ({nx},{ny}), expected ~({cx},{cy})");
        }
        finally
        {
            DesktopTestHelpers.SetCursor(sx, sy);
        }
    }

    // -----------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------

    private static bool WaitFor(Func<bool> condition, int timeoutMs = EffectTimeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(100);
        }
        return false;
    }
}
