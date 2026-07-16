// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Helpers for verifying that the window we're about to inject OS-wide input into is actually the
/// one the user targeted. <c>SendInput</c>-based gestures (send-keys via send-input, drag, scroll
/// --wheel, click, hover) land on whatever window is in the foreground / under the cursor — if
/// <c>SetForegroundWindow</c> silently failed (focus-stealing prevention, a UAC prompt, another app
/// grabbing focus, or the session being locked) the input would hit the wrong window or be dropped.
/// </summary>
internal static class ForegroundGuard
{
    /// <remarks>
    /// Native adapter seam for issue #630: the default body reads the live foreground HWND from the
    /// interactive desktop. Tests inject deterministic handles to cover foreground classification
    /// without depending on desktop focus.
    /// </remarks>
    internal static Func<Windows.Win32.Foundation.HWND> s_getForegroundWindow =
        Windows.Win32.PInvoke.GetForegroundWindow;

    /// <remarks>
    /// Native adapter seam for issue #630: the default body walks Win32 HWND ancestry. Tests inject
    /// deterministic roots so no real windows are required.
    /// </remarks>
    internal static Func<Windows.Win32.Foundation.HWND, Windows.Win32.Foundation.HWND> s_getRootAncestor =
        DefaultGetRootAncestor;

    private static Windows.Win32.Foundation.HWND DefaultGetRootAncestor(Windows.Win32.Foundation.HWND hwnd) =>
        Windows.Win32.PInvoke.GetAncestor(hwnd, Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);

    /// <summary>
    /// Restores every native seam to its production delegate. Test cleanup calls this so a faked
    /// seam never leaks into a later test that reads the live foreground window (issue #630).
    /// </summary>
    internal static void ResetNativeSeams()
    {
        s_getForegroundWindow = Windows.Win32.PInvoke.GetForegroundWindow;
        s_getRootAncestor = DefaultGetRootAncestor;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the current foreground window is <paramref name="targetHwnd"/>
    /// or the top-level root window that owns it. A <paramref name="targetHwnd"/> of 0 (no resolvable
    /// window) is treated as "can't verify" and returns <see langword="false"/>.
    /// </summary>
    public static bool ForegroundBelongsTo(long targetHwnd)
    {
        if (targetHwnd == 0)
        {
            return false;
        }

        var foreground = s_getForegroundWindow();
        if (foreground.IsNull)
        {
            return false;
        }

        var target = new Windows.Win32.Foundation.HWND((nint)targetHwnd);
        if (foreground == target)
        {
            return true;
        }

        // The resolved element HWND is frequently a child / host window (a WinUI 3 input-site bridge,
        // a control HWND); the window that actually holds the foreground is its top-level root. Accept
        // only when the target's root window IS the foreground window. Compare by window ancestry, not
        // by owning process: a PID match would also accept a *different* top-level window of the same
        // process (common in multi-window apps) that merely happens to be foreground, which would let
        // the injection land on the wrong window.
        var targetRoot = s_getRootAncestor(target);
        return !targetRoot.IsNull && targetRoot == foreground;
    }

    /// <summary>
    /// Returns <see langword="true"/> when there is no foreground window at all — the signature of a
    /// locked workstation or a secure desktop (LogonUI / UAC), where a user-session process cannot
    /// inject input. Distinguishes "session locked" from "wrong window" / "elevated target".
    /// </summary>
    public static bool NoInteractiveDesktop()
        => s_getForegroundWindow().IsNull;

    /// <summary>
    /// Returns <see langword="true"/> when this process is running inside a remote session (Remote
    /// Desktop / Terminal Services), detected via <c>GetSystemMetrics(SM_REMOTESESSION)</c>. Synthetic
    /// pointer injection (<c>ui touch</c> / <c>ui pen</c> via <c>InjectSyntheticPointerInput</c>) is
    /// frequently accepted by the API — the call reports success — yet not routed to applications over
    /// the remote-desktop transport (pen in particular). Callers use this to attach an honest
    /// "delivery not guaranteed" advisory so a reported success is not mistaken for confirmed delivery.
    /// </summary>
    public static bool IsRemoteSession()
        => Windows.Win32.PInvoke.GetSystemMetrics(
               Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_REMOTESESSION) != 0;

    /// <summary>
    /// Pure composition of the remote-session delivery advisory for synthetic pointer injection, or
    /// <see langword="null"/> when none is warranted (a local, physically-attached session). Kept
    /// side-effect-free (no PInvoke) so the message is unit-testable without a live remote session.
    /// </summary>
    /// <param name="isRemoteSession">Whether the current session is remote (see <see cref="IsRemoteSession"/>).</param>
    /// <param name="inputKind">Human word for the injected input, e.g. "touch" or "pen".</param>
    public static string? RemoteInjectionWarning(bool isRemoteSession, string inputKind)
        => isRemoteSession
            ? $"Injected in a remote/RDP session — synthetic {inputKind} input is often not delivered to the target " +
              "application over Remote Desktop (pen especially), so this success does not guarantee the gesture " +
              "reached the app. Verify the effect (e.g. 'ui screenshot' or 'ui inspect'). Delivery is reliable on a " +
              "local, physically-attached session."
            : null;

    /// <summary>The outcome of the pre-injection foreground check.</summary>
    internal enum ForegroundCheck
    {
        /// <summary>No target to verify, or the target already holds the foreground — inject.</summary>
        Proceed,

        /// <summary>No foreground window exists at all — the session is locked / on a secure desktop.</summary>
        NoInteractiveDesktop,

        /// <summary>A different window holds the foreground — refuse to avoid injecting into it.</summary>
        ForegroundNotTarget,
    }

    /// <summary>
    /// Pure decision behind <see cref="TryEnsureForeground"/>: given whether there is a target window
    /// to verify, whether that target currently holds the foreground, and whether any foreground
    /// window exists at all, choose the outcome. Side-effect-free (no PInvoke) so the locked-desktop
    /// (<c>no_interactive_desktop</c>) vs. wrong-window (<c>foreground_not_target</c>) selection is
    /// unit-testable without a live desktop.
    /// </summary>
    internal static ForegroundCheck Classify(bool hasTarget, bool targetIsForeground, bool anyForegroundWindow)
    {
        if (!hasTarget || targetIsForeground)
        {
            return ForegroundCheck.Proceed;
        }

        return anyForegroundWindow ? ForegroundCheck.ForegroundNotTarget : ForegroundCheck.NoInteractiveDesktop;
    }

    /// <summary>
    /// Verifies the target is foreground before an OS-wide injection and emits the appropriate error
    /// when it isn't. Returns <see langword="true"/> to proceed, <see langword="false"/> to abort.
    /// A <paramref name="targetHwnd"/> of 0 means we have no window to verify against (e.g. a bare
    /// coordinate target) and is allowed through. On failure it picks a precise reason via
    /// <see cref="Classify"/>: a locked / secure desktop (<c>no_interactive_desktop</c>) vs. another
    /// window holding the foreground (<c>foreground_not_target</c>), so callers never report the
    /// misleading "target may be elevated" cause for a simply-locked session.
    /// </summary>
    /// <param name="action">Verb used in the message, e.g. "click", "drag", "scroll --wheel".</param>
    public static bool TryEnsureForeground(long targetHwnd, ILogger logger, bool json, string action)
    {
        bool hasTarget = targetHwnd != 0;
        var outcome = Classify(
            hasTarget,
            targetIsForeground: hasTarget && ForegroundBelongsTo(targetHwnd),
            anyForegroundWindow: !NoInteractiveDesktop());

        switch (outcome)
        {
            case ForegroundCheck.Proceed:
                return true;

            case ForegroundCheck.NoInteractiveDesktop:
                logger.LogError(
                    "{Symbol} No interactive desktop is available — the session is locked or on a secure desktop, so input can't be injected. Unlock the session and retry, or use a UIA-pattern verb (invoke, set-value, scroll --direction/--to) which doesn't need the desktop.",
                    UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeNoInteractiveDesktop,
                    "No interactive desktop is available (session locked or on a secure desktop) — cannot inject input. Unlock the session, or use a UIA-pattern verb.");
                return false;

            case ForegroundCheck.ForegroundNotTarget:
            default:
                logger.LogError(
                    "{Symbol} Target window is not in the foreground — refusing to {Action} to avoid acting on the wrong window. Focus or click the window first.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                    $"Target window is not in the foreground — refusing to {action} to avoid injecting into the wrong window. Bring it to the foreground first.");
                return false;
        }
    }
}
