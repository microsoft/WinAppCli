// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Services;

/// <summary>
/// Real <see cref="ISystemUiQuery"/> backed by <see cref="Process"/> and Win32 PInvoke. This is
/// the thin OS-boundary layer extracted out of <see cref="UiSessionService"/> so the resolver's
/// decision logic becomes unit-testable. It contains only native/OS calls and is therefore left
/// uncovered by design — the same honest-ceiling category as <c>RealForegroundGuard</c> and
/// <c>RealOwnedWindowFinder</c>. Behavior is unchanged from the code that previously lived inline.
/// </summary>
internal sealed class SystemUiQuery : ISystemUiQuery
{
    /// <remarks>
    /// Native adapter seam for issue #630: the default body reads the live foreground HWND. Tests
    /// replace it to cover the adapter method without depending on desktop focus.
    /// </remarks>
    internal static Func<nint> s_getForegroundWindow = NativeGetForegroundWindow;

    /// <remarks>
    /// Native adapter seam for issue #630: the default body calls <c>GetWindowThreadProcessId</c> for
    /// a live HWND. Tests replace it with deterministic values.
    /// </remarks>
    internal static Func<long, uint> s_getProcessIdForWindow = NativeGetProcessIdForWindow;

    internal static nint NativeGetForegroundWindow()
        => (nint)Windows.Win32.PInvoke.GetForegroundWindow();

    internal static uint NativeGetProcessIdForWindow(long hwnd)
    {
        uint pid = 0;
        unsafe
        {
            Windows.Win32.PInvoke.GetWindowThreadProcessId(
                new Windows.Win32.Foundation.HWND((nint)hwnd), &pid);
        }

        return pid;
    }

    /// <remarks>
    /// Native adapter seam for issue #630: the default body calls <c>GetWindowText</c> against a live
    /// HWND. Tests replace it to cover null/empty/non-empty translation without a real window.
    /// </remarks>
    internal static Func<long, string?> s_getWindowText = NativeGetWindowText;

    internal static string? NativeGetWindowText(long hwnd)
    {
        var handle = new Windows.Win32.Foundation.HWND((nint)hwnd);
        var buffer = new char[512];
        int len;
        unsafe
        {
            fixed (char* pTitle = buffer)
            {
                len = Windows.Win32.PInvoke.GetWindowText(handle, pTitle, buffer.Length);
            }
        }

        return len > 0 ? new string(buffer, 0, len) : null;
    }

    /// <remarks>
    /// Native adapter seam for issue #630: the default body calls <c>GetClassName</c> against a live
    /// HWND. Tests replace it to cover adapter behavior without a real window.
    /// </remarks>
    internal static Func<long, string?> s_getWindowClassName = NativeGetWindowClassName;

    internal static string? NativeGetWindowClassName(long hwnd)
    {
        var buffer = new char[256];
        int len;
        unsafe
        {
            fixed (char* pClass = buffer)
            {
                len = Windows.Win32.PInvoke.GetClassName(
                    new Windows.Win32.Foundation.HWND((nint)hwnd), pClass, 256);
            }
        }
        return len > 0 ? new string(buffer, 0, len) : null;
    }

    /// <remarks>
    /// Native adapter seam for issue #630: the default body calls <c>GetWindowRect</c> against a live
    /// HWND. Tests replace it to cover adapter behavior without a real window.
    /// </remarks>
    internal static Func<long, (int Width, int Height)> s_getWindowSize = NativeGetWindowSize;

    internal static (int Width, int Height) NativeGetWindowSize(long hwnd)
    {
        Windows.Win32.Foundation.RECT rect;
        unsafe
        {
            Windows.Win32.PInvoke.GetWindowRect(
                new Windows.Win32.Foundation.HWND((nint)hwnd), &rect);
        }
        return (rect.right - rect.left, rect.bottom - rect.top);
    }

    /// <remarks>
    /// Native adapter seam for issue #630: the default body calls <c>GetWindow(GW_OWNER)</c> against
    /// a live HWND. Tests replace it to cover adapter behavior without a real window.
    /// </remarks>
    internal static Func<long, nint> s_getWindowOwner = NativeGetWindowOwner;

    internal static nint NativeGetWindowOwner(long hwnd)
    {
        var owner = Windows.Win32.PInvoke.GetWindow(
            new Windows.Win32.Foundation.HWND((nint)hwnd),
            Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);
        return (nint)owner;
    }

    /// <remarks>
    /// Native adapter seam (issue #655): the default body reads the keyboard-focus HWND of the thread
    /// owning a live window via <c>GetGUIThreadInfo</c>. Tests replace it with deterministic values so
    /// the focused-child post-message targeting is exercisable without a real desktop.
    /// </remarks>
    internal static Func<long, long> s_getFocusedWindow = NativeGetFocusedWindow;

    internal static long NativeGetFocusedWindow(long hwnd)
    {
        Windows.Win32.UI.WindowsAndMessaging.GUITHREADINFO info = default;
        unsafe
        {
            uint threadId = Windows.Win32.PInvoke.GetWindowThreadProcessId(
                new Windows.Win32.Foundation.HWND((nint)hwnd), null);
            if (threadId == 0)
            {
                return 0;
            }

            info.cbSize = (uint)sizeof(Windows.Win32.UI.WindowsAndMessaging.GUITHREADINFO);

            // hwndFocus is only populated when the thread owns the keyboard focus (i.e. it is the
            // foreground thread). The caller foregrounds the target first; if that didn't take, we
            // return 0 and the caller falls back to the passed HWND.
            return Windows.Win32.PInvoke.GetGUIThreadInfo(threadId, ref info)
                ? (long)(nint)info.hwndFocus
                : 0;
        }
    }

    internal static void ResetNativeSeams()
    {
        s_getForegroundWindow = NativeGetForegroundWindow;
        s_getProcessIdForWindow = NativeGetProcessIdForWindow;
        s_getWindowText = NativeGetWindowText;
        s_getWindowClassName = NativeGetWindowClassName;
        s_getWindowSize = NativeGetWindowSize;
        s_getWindowOwner = NativeGetWindowOwner;
        s_getFocusedWindow = NativeGetFocusedWindow;
    }

    public UiProcessInfo? GetProcessById(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return Capture(process);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public IReadOnlyList<UiProcessInfo> GetProcessesByName(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return Array.ConvertAll(processes, Capture);
        }
        finally
        {
            foreach (var p in processes) { p.Dispose(); }
        }
    }

    public IReadOnlyList<UiProcessInfo> GetProcessesMatching(string substring)
    {
        var processes = Process.GetProcesses();
        try
        {
            var result = new List<UiProcessInfo>();
            foreach (var p in processes)
            {
                bool matches;
                try { matches = p.ProcessName.Contains(substring, StringComparison.OrdinalIgnoreCase); }
                catch { matches = false; }

                if (matches) { result.Add(Capture(p)); }
            }

            return result;
        }
        finally
        {
            foreach (var p in processes) { p.Dispose(); }
        }
    }

    internal static UiProcessInfo Capture(
        int id,
        Func<string> getProcessName,
        Func<nint> getMainWindowHandle,
        Func<string?> getMainWindowTitle)
    {
        string name;
        try { name = getProcessName(); }
        catch { name = string.Empty; }

        nint handle;
        string? title;
        try
        {
            handle = getMainWindowHandle();
            title = getMainWindowTitle();
        }
        catch
        {
            handle = 0;
            title = null;
        }

        return new UiProcessInfo(id, name, handle, title);
    }

    private static UiProcessInfo Capture(Process process)
        => Capture(process.Id, () => process.ProcessName, () => process.MainWindowHandle, () => process.MainWindowTitle);

    public nint GetForegroundWindow() => s_getForegroundWindow();

    public uint GetProcessIdForWindow(long hwnd)
    {
        return s_getProcessIdForWindow(hwnd);
    }

    public string? GetWindowText(long hwnd)
    {
        try
        {
            return s_getWindowText(hwnd);
        }
        catch
        {
            return null;
        }
    }

    public string? GetWindowClassName(long hwnd)
    {
        try
        {
            return s_getWindowClassName(hwnd);
        }
        // Native guard: GetClassName does not throw for invalid handles, so this catch is an
        // honest ceiling — only a genuine marshalling failure would reach it.
        catch { return null; }
    }

    public (int Width, int Height) GetWindowSize(long hwnd)
    {
        try
        {
            return s_getWindowSize(hwnd);
        }
        // Native guard: GetWindowRect does not throw for invalid handles — honest ceiling.
        catch { return (0, 0); }
    }

    public nint GetWindowOwner(long hwnd)
    {
        try
        {
            return s_getWindowOwner(hwnd);
        }
        // Native guard: GetWindow does not throw for invalid handles — honest ceiling.
        catch { return 0; }
    }

    public long GetFocusedWindow(long hwnd)
    {
        try
        {
            return s_getFocusedWindow(hwnd);
        }
        // Native guard: GetGUIThreadInfo does not throw for invalid handles — honest ceiling.
        catch { return 0; }
    }
}
