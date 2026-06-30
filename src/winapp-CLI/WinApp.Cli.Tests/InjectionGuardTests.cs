// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the pure decision logic behind the input-injection guards. The branch selection
/// is extracted from the PInvoke-backed guards so the locked-desktop vs. wrong-window choice
/// (<see cref="ForegroundGuard.Classify"/>) and the WM_CHAR drop warning gate
/// (<see cref="UiSendKeysCommand.Handler.ShouldWarnPostMessageTextDropped"/>) can be verified without
/// a live desktop or a real XAML window.
/// </summary>
[TestClass]
public class InjectionGuardTests
{
    // -----------------------------------------------------------------
    // ForegroundGuard.Classify — no_interactive_desktop vs foreground_not_target (M5 / N3)
    // -----------------------------------------------------------------
    // The internal ForegroundCheck enum is referenced only inside method bodies (not in a public
    // signature / DataRow param) to avoid CS0051 inconsistent-accessibility against the public class.

    [TestMethod]
    public void Classify_NoTargetToVerify_Proceeds()
    {
        // A bare x,y coordinate gesture has no window to verify → proceed regardless of the desktop.
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: false, targetIsForeground: false, anyForegroundWindow: false));
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: false, targetIsForeground: false, anyForegroundWindow: true));
    }

    [TestMethod]
    public void Classify_TargetIsForeground_Proceeds()
    {
        // Target already holds the foreground → proceed (the anyForegroundWindow input is irrelevant).
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: true, anyForegroundWindow: true));
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.Proceed, ForegroundGuard.Classify(hasTarget: true, targetIsForeground: true, anyForegroundWindow: false));
    }

    [TestMethod]
    public void Classify_AnotherWindowForeground_ReportsForegroundNotTarget()
    {
        // Target exists but isn't foreground while another window is → wrong window, not a locked desktop.
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.ForegroundNotTarget,
            ForegroundGuard.Classify(hasTarget: true, targetIsForeground: false, anyForegroundWindow: true));
    }

    [TestMethod]
    public void Classify_NoForegroundWindowAtAll_ReportsNoInteractiveDesktop()
    {
        // Target exists, isn't foreground, and there's NO foreground window → locked / secure desktop.
        // This is the M5 distinction: don't blame "wrong window" (or elevation) for a simply-locked session.
        Assert.AreEqual(ForegroundGuard.ForegroundCheck.NoInteractiveDesktop,
            ForegroundGuard.Classify(hasTarget: true, targetIsForeground: false, anyForegroundWindow: false));
    }

    // -----------------------------------------------------------------
    // UiSendKeysCommand.ShouldWarnPostMessageTextDropped — WM_CHAR drop warning gate (M6 / N6)
    // -----------------------------------------------------------------

    [TestMethod]
    // Warn only when posting WM_CHAR AND there is literal text AND the target looks like XAML.
    [DataRow(true, true, true, true)]
    // send-input transport delivers real keystrokes → no WM_CHAR drop → no warning.
    [DataRow(false, true, true, false)]
    // Only named keys / combos (no literal text) → KeyDown is posted regardless → no warning.
    [DataRow(true, false, true, false)]
    // Non-XAML target (Win32 / WPF / Electron consume WM_CHAR) → no warning. This is the N6 gate:
    // before FrameworkHint, post-message + text warned on EVERY app, false-alarming the majority.
    [DataRow(true, true, false, false)]
    [DataRow(false, false, false, false)]
    public void ShouldWarnPostMessageTextDropped_OnlyForXamlPostMessageText(
        bool isPostMessage, bool hasLiteralText, bool targetLooksXaml, bool expected)
    {
        Assert.AreEqual(expected,
            UiSendKeysCommand.Handler.ShouldWarnPostMessageTextDropped(isPostMessage, hasLiteralText, targetLooksXaml));
    }
}
