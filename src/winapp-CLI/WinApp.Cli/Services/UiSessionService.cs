// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class UiSessionService(
    IUiAutomationService uiAutomation) : IUiSessionService
{

    public Task<UiSessionInfo> ResolveSessionAsync(string? app, long? hwnd, CancellationToken ct)
    {
        // Direct HWND targeting — most stable, used after discovery
        if (hwnd is not null and > 0)
        {
            return Task.FromResult(ResolveByHwnd(hwnd.Value));
        }

        if (string.IsNullOrWhiteSpace(app))
        {
            throw new InvalidOperationException("Specify --app (process name, title, or PID) or --window (HWND).");
        }

        // Try PID or process name first
        var process = TryResolveProcess(app);

        if (process is null)
        {
            // Not a PID or process name — search all windows by title
            var windows = uiAutomation.FindWindowsByTitle(app);
            if (windows.Count == 0)
            {
                throw new InvalidOperationException($"No running app found matching '{app}'.");
            }
            if (windows.Count > 1)
            {
                var listing = string.Join("\n  ",
                    windows.Select(w => $"HWND {w.Hwnd}: \"{w.Title}\" ({GetProcessNameSafe(w.Pid)}, PID {w.Pid})"));
                throw new InvalidOperationException(
                    $"Multiple windows match '{app}':\n  {listing}\n" +
                    "Use -w <HWND> to target a specific window.");
            }
            // Single match
            var match = windows[0];
            return Task.FromResult(CreateSession(match.Pid, match.Hwnd, match.Title));
        }

        // Process found — check for multiple windows
        var processWindows = uiAutomation.FindWindowsByPid(process.Id);
        if (processWindows.Count > 1)
        {
            var listing = string.Join("\n  ",
                processWindows.Select(w => $"HWND {w.Hwnd}: \"{w.Title}\" ({process.ProcessName}, PID {process.Id})"));
            throw new InvalidOperationException(
                $"'{app}' has multiple windows:\n  {listing}\n" +
                "Use -w <HWND> to target a specific window.");
        }

        if (processWindows.Count == 1)
        {
            return Task.FromResult(CreateSession(process.Id, processWindows[0].Hwnd, processWindows[0].Title));
        }

        return Task.FromResult(new UiSessionInfo
        {
            ProcessId = process.Id,
            ProcessName = process.ProcessName,
            WindowTitle = GetMainWindowTitle(process)
        });
    }

    private static UiSessionInfo ResolveByHwnd(long hwnd)
    {
        var pid = GetPidFromHwnd(hwnd);
        if (pid == 0)
        {
            throw new InvalidOperationException($"Window HWND {hwnd} not found or not accessible.");
        }

        var session = CreateSession((int)pid, (nint)hwnd, null);
        RefreshWindowTitle(session);
        return session;
    }

    private static UiSessionInfo CreateSession(int pid, nint hwnd, string? title)
    {
        string processName;
        try { processName = Process.GetProcessById(pid).ProcessName; }
        catch { processName = "Unknown"; }

        return new UiSessionInfo
        {
            ProcessId = pid,
            ProcessName = processName,
            WindowTitle = title,
            WindowHandle = hwnd
        };
    }

    private static string GetProcessNameSafe(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "Unknown"; }
    }

    /// <summary>
    /// Refresh the window title. When a specific HWND is set, reads from that HWND.
    /// </summary>
    private static void RefreshWindowTitle(UiSessionInfo session)
    {
        if (session.WindowHandle != 0)
        {
            try
            {
                var hwnd = new Windows.Win32.Foundation.HWND((nint)session.WindowHandle);
                var title = new char[256];
                int len;
                unsafe
                {
                    fixed (char* pTitle = title)
                    {
                        len = Windows.Win32.PInvoke.GetWindowText(hwnd, pTitle, title.Length);
                    }
                }
                if (len > 0)
                {
                    session.WindowTitle = new string(title, 0, len);
                }
            }
            catch { }
            return;
        }

        try
        {
            var proc = Process.GetProcessById(session.ProcessId);
            var title = proc.MainWindowTitle;
            if (!string.IsNullOrEmpty(title))
            {
                session.WindowTitle = title;
            }
        }
        catch { }
    }

    private static Process? TryResolveProcess(string app)
    {
        // Try as PID
        if (int.TryParse(app, out var pid))
        {
            try
            {
                return Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"No process found with PID {pid}.");
            }
        }

        // Try exact process name
        var byName = Process.GetProcessesByName(app);
        if (byName.Length == 1)
        {
            return byName[0];
        }

        if (byName.Length > 1)
        {
            var withWindow = byName
                .Where(p =>
                {
                    try { return p.MainWindowHandle != 0 && !string.IsNullOrEmpty(p.MainWindowTitle); }
                    catch { return false; }
                })
                .ToArray();

            if (withWindow.Length == 1)
            {
                return withWindow[0];
            }

            if (withWindow.Length > 1)
            {
                var listing = string.Join("\n  ",
                    withWindow.Select(p =>
                    {
                        try { return $"PID {p.Id}: \"{p.MainWindowTitle}\""; }
                        catch { return $"PID {p.Id}"; }
                    }));
                throw new InvalidOperationException(
                    $"Multiple '{app}' windows found:\n  {listing}\n" +
                    "Use --app with a PID or a more specific window title.");
            }
        }

        // Try partial process name match (e.g., "imageresizer" matches "PowerToys.ImageResizer")
        var partialMatches = Process.GetProcesses()
            .Where(p =>
            {
                try { return p.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .ToArray();

        if (partialMatches.Length == 1)
        {
            return partialMatches[0];
        }

        if (partialMatches.Length > 1)
        {
            var withWindow = partialMatches
                .Where(p =>
                {
                    try { return p.MainWindowHandle != 0 && !string.IsNullOrEmpty(p.MainWindowTitle); }
                    catch { return false; }
                })
                .ToArray();

            if (withWindow.Length == 1)
            {
                return withWindow[0];
            }
        }

        return null;
    }

    private static uint GetPidFromHwnd(long hwnd)
    {
        uint pid = 0;
        unsafe
        {
            Windows.Win32.PInvoke.GetWindowThreadProcessId(
                new Windows.Win32.Foundation.HWND((nint)hwnd), &pid);
        }
        return pid;
    }

    private static string? GetMainWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrEmpty(process.MainWindowTitle) ? null : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }
}
