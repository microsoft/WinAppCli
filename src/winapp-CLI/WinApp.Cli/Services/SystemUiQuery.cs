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

    private static UiProcessInfo Capture(Process process)
    {
        string name;
        try { name = process.ProcessName; }
        catch { name = string.Empty; }

        nint handle;
        string? title;
        try
        {
            handle = process.MainWindowHandle;
            title = process.MainWindowTitle;
        }
        catch
        {
            handle = 0;
            title = null;
        }

        return new UiProcessInfo(process.Id, name, handle, title);
    }

    public nint GetForegroundWindow() => (nint)Windows.Win32.PInvoke.GetForegroundWindow();

    public uint GetProcessIdForWindow(long hwnd)
    {
        uint pid = 0;
        unsafe
        {
            Windows.Win32.PInvoke.GetWindowThreadProcessId(
                new Windows.Win32.Foundation.HWND((nint)hwnd), &pid);
        }

        return pid;
    }

    public string? GetWindowText(long hwnd)
    {
        try
        {
            var handle = new Windows.Win32.Foundation.HWND((nint)hwnd);
            var buffer = new char[256];
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
        catch
        {
            return null;
        }
    }
}
