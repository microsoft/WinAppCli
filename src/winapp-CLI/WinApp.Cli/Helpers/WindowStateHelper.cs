// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Helpers for normalizing a target window's show-state before we read or capture it.
/// </summary>
internal static class WindowStateHelper
{
    /// <summary>
    /// Delay after restoring a minimized window so the framework can re-realize its visual/UIA
    /// tree before we walk or capture it. Minimized WinUI/XAML windows virtualize offscreen
    /// elements, so inspecting them yields a sparser tree than when they're on screen.
    /// </summary>
    private const int RestoreSettleMs = 300;

    /// <summary>
    /// If <paramref name="hwnd"/> is minimized, restores it (SW_RESTORE) and waits briefly for the
    /// UI tree to re-realize. No-op for a null handle or a window that isn't minimized.
    /// </summary>
    /// <returns><see langword="true"/> when a restore was performed.</returns>
    public static bool RestoreIfMinimized(nint hwnd, ILogger? logger = null)
    {
        if (hwnd == 0)
        {
            return false;
        }

        var handle = new Windows.Win32.Foundation.HWND(hwnd);
        if (!Windows.Win32.PInvoke.IsIconic(handle))
        {
            return false;
        }

        logger?.LogDebug("Target window {Hwnd} is minimized; restoring it before reading its UI tree.", hwnd);
        Windows.Win32.PInvoke.ShowWindow(handle, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
        Thread.Sleep(RestoreSettleMs);
        return true;
    }
}
