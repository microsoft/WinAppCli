// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Production <see cref="IOwnedWindowFinder"/> — enumerates the real desktop for visible top-level
/// windows owned by one of the given app windows. This is a thin, un-runnable-in-CI native (Win32)
/// wrapper; the interface exists so <see cref="Commands.UiScreenshotCommand"/>'s discovery logic is
/// testable without a live desktop. Moved verbatim from the command; behavior is unchanged.
/// </summary>
internal sealed class RealOwnedWindowFinder : IOwnedWindowFinder
{
    public List<(nint Hwnd, int Pid, string Title)> FindOwnedWindows(List<(nint Hwnd, int Pid, string Title)> appWindows)
    {
        var appHwnds = new HashSet<nint>(appWindows.Select(w => w.Hwnd));
        var owned = new List<(nint Hwnd, int Pid, string Title)>();

        // Enumerate all visible windows and check ownership
        var hwnd = Windows.Win32.Foundation.HWND.Null;
        while (true)
        {
            hwnd = Windows.Win32.PInvoke.FindWindowEx(
                Windows.Win32.Foundation.HWND.Null, hwnd, null, (string?)null);
            if (hwnd.IsNull) { break; }
            if (!Windows.Win32.PInvoke.IsWindowVisible(hwnd)) { continue; }

            // Skip windows already in the list
            if (appHwnds.Contains((nint)hwnd)) { continue; }

            // Check if this window is owned by one of our app windows
            var owner = Windows.Win32.PInvoke.GetWindow(hwnd,
                Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);
            if (!owner.IsNull && appHwnds.Contains((nint)owner))
            {
                unsafe
                {
                    uint pid = 0;
                    Windows.Win32.PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                    var titleChars = new char[512];
                    fixed (char* buffer = titleChars)
                    {
                        var len = Windows.Win32.PInvoke.GetWindowText(hwnd, buffer, 512);
                        var title = len > 0 ? new string(buffer, 0, len) : "";
                        owned.Add(((nint)hwnd, (int)pid, title));
                    }
                }
            }
        }

        return owned;
    }
}
