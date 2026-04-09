// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class UiSessionService(
    IUiAutomationService uiAutomation,
    ILogger<UiSessionService> logger) : IUiSessionService
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
                return Task.FromResult(AutoSelectWindow(windows, app));
            }
            // Single match
            var match = windows[0];
            return Task.FromResult(CreateSession(match.Pid, match.Hwnd, match.Title));
        }

        // Process found — check for multiple windows
        var processWindows = uiAutomation.FindWindowsByPid(process.Id);
        if (processWindows.Count > 1)
        {
            return Task.FromResult(AutoSelectWindow(processWindows, app));
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

    /// <summary>
    /// Auto-selects the best window from multiple candidates silently.
    /// Heuristic: prefer foreground window → prefer largest window.
    /// </summary>
    private UiSessionInfo AutoSelectWindow(List<(nint Hwnd, int Pid, string Title)> windows, string app)
    {
        var foregroundHwnd = Windows.Win32.PInvoke.GetForegroundWindow();
        var foreground = windows.FirstOrDefault(w => w.Hwnd == (nint)foregroundHwnd);
        var selected = foreground != default ? foreground : PickLargestWindow(windows);

        var reason = foreground != default ? "foreground" : "largest";
        logger.LogDebug("Auto-selected HWND {Hwnd} ({Reason}) from {Count} windows for '{App}'", selected.Hwnd, reason, windows.Count, app);

        return CreateSession(selected.Pid, selected.Hwnd, selected.Title);
    }

    /// <summary>Pick the window with the largest area.</summary>
    private static (nint Hwnd, int Pid, string Title) PickLargestWindow(List<(nint Hwnd, int Pid, string Title)> windows)
    {
        var best = windows[0];
        long bestArea = 0;

        foreach (var w in windows)
        {
            var info = GetWindowInfo(w.Hwnd);
            var area = (long)info.Width * info.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = w;
            }
        }

        return best;
    }

    /// <summary>Get metadata for a window: class name, label, size, owner.</summary>
    internal static WindowMetadata GetWindowInfo(nint hwnd)
    {
        var className = GetWindowClassName(hwnd);
        var label = ClassifyWindow(className);
        var (width, height) = GetWindowSize(hwnd);
        var ownerHwnd = GetWindowOwner(hwnd);

        return new WindowMetadata
        {
            ClassName = className ?? "Unknown",
            Label = label,
            Width = width,
            Height = height,
            OwnerHwnd = ownerHwnd
        };
    }

    internal static string? GetWindowClassName(nint hwnd)
    {
        try
        {
            var buffer = new char[256];
            int len;
            unsafe
            {
                fixed (char* pClass = buffer)
                {
                    len = Windows.Win32.PInvoke.GetClassName(
                        new Windows.Win32.Foundation.HWND(hwnd), pClass, 256);
                }
            }
            return len > 0 ? new string(buffer, 0, len) : null;
        }
        catch { return null; }
    }

    private static string ClassifyWindow(string? className)
    {
        if (className is null) { return "window"; }
        if (className.Contains("Popup", StringComparison.OrdinalIgnoreCase)) { return "popup"; }
        if (className == "#32770") { return "dialog"; }
        return "window";
    }

    private static (int Width, int Height) GetWindowSize(nint hwnd)
    {
        try
        {
            Windows.Win32.Foundation.RECT rect;
            unsafe
            {
                Windows.Win32.PInvoke.GetWindowRect(
                    new Windows.Win32.Foundation.HWND(hwnd), &rect);
            }
            return (rect.right - rect.left, rect.bottom - rect.top);
        }
        catch { return (0, 0); }
    }

    private static nint GetWindowOwner(nint hwnd)
    {
        try
        {
            var owner = Windows.Win32.PInvoke.GetWindow(
                new Windows.Win32.Foundation.HWND(hwnd),
                Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);
            return (nint)owner;
        }
        catch { return 0; }
    }

    internal record WindowMetadata
    {
        public string ClassName { get; init; } = "Unknown";
        public string Label { get; init; } = "window";
        public int Width { get; init; }
        public int Height { get; init; }
        public nint OwnerHwnd { get; init; }
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
        try
        {
            if (byName.Length == 1)
            {
                var result = byName[0];
                byName = []; // prevent disposal of the returned process
                return result;
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
                    var result = withWindow[0];
                    byName = byName.Where(p => p != result).ToArray(); // dispose all except the returned one
                    return result;
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
        }
        finally
        {
            foreach (var p in byName) { p.Dispose(); }
        }

        // Try partial process name match (e.g., "imageresizer" matches "PowerToys.ImageResizer")
        var partialMatches = Process.GetProcesses()
            .Where(p =>
            {
                try { return p.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .ToArray();

        try
        {
            if (partialMatches.Length == 1)
            {
                var result = partialMatches[0];
                partialMatches = []; // prevent disposal
                return result;
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
                    var result = withWindow[0];
                    partialMatches = partialMatches.Where(p => p != result).ToArray();
                    return result;
                }
            }
        }
        finally
        {
            foreach (var p in partialMatches) { p.Dispose(); }
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
