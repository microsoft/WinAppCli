// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Helpers for verifying that the window we're about to inject OS-wide input into is actually the
/// one the user targeted. <c>SendInput</c>-based gestures (send-keys via send-input, drag, scroll
/// --wheel) land on whatever window is in the foreground / under the cursor — if
/// <c>SetForegroundWindow</c> silently failed (focus-stealing prevention, a UAC prompt, another app
/// grabbing focus) the input would hit the wrong window.
/// </summary>
internal static class ForegroundGuard
{
    /// <summary>
    /// Returns <see langword="true"/> when the current foreground window is <paramref name="targetHwnd"/>
    /// or belongs to the same process. A <paramref name="targetHwnd"/> of 0 (no resolvable window) is
    /// treated as "can't verify" and returns <see langword="false"/>.
    /// </summary>
    public static unsafe bool ForegroundBelongsTo(long targetHwnd)
    {
        if (targetHwnd == 0)
        {
            return false;
        }

        var foreground = Windows.Win32.PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return false;
        }

        var target = new Windows.Win32.Foundation.HWND((nint)targetHwnd);
        if (foreground == target)
        {
            return true;
        }

        // The foreground is often the top-level ancestor of the resolved element HWND, so compare by
        // owning process rather than requiring an exact handle match.
        uint foregroundPid = 0, targetPid = 0;
        Windows.Win32.PInvoke.GetWindowThreadProcessId(foreground, &foregroundPid);
        Windows.Win32.PInvoke.GetWindowThreadProcessId(target, &targetPid);
        return foregroundPid != 0 && foregroundPid == targetPid;
    }
}
