// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class UiSessionService(
    IWinappDirectoryService winappDirectoryService,
    IUiAutomationService uiAutomation,
    ILogger<UiSessionService> logger) : IUiSessionService
{

    public async Task<UiSessionInfo> ResolveSessionAsync(string? app, long? hwnd, string? forceMode, CancellationToken ct)
    {
        // Direct HWND targeting — most stable, used after discovery
        if (hwnd is not null and > 0)
        {
            return await ResolveByHwndAsync(hwnd.Value, forceMode, ct);
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
            return await CreateSessionForWindow(match.Hwnd, match.Pid, match.Title, app, forceMode, ct);
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

        // Single window or no windows — use existing session logic
        var existing = await LoadSessionAsync(process.Id, ct);
        if (existing is not null && IsProcessAlive(existing.ProcessId))
        {
            existing.AppQuery = app;
            if (processWindows.Count == 1)
            {
                existing.WindowHandle = processWindows[0].Hwnd;
                existing.WindowTitle = processWindows[0].Title;
            }
            RefreshWindowTitle(existing);
            return existing;
        }

        var session = await CreateSessionAsync(process, app, forceMode, ct);
        if (processWindows.Count == 1)
        {
            session.WindowHandle = processWindows[0].Hwnd;
            session.WindowTitle = processWindows[0].Title;
        }
        await SaveSessionAsync(session, ct);
        return session;
    }

    private async Task<UiSessionInfo> ResolveByHwndAsync(long hwnd, string? forceMode, CancellationToken ct)
    {
        // Get PID from HWND
        var pid = GetPidFromHwnd(hwnd);
        if (pid == 0)
        {
            throw new InvalidOperationException($"Window HWND {hwnd} not found or not accessible.");
        }

        var process = Process.GetProcessById((int)pid);

        // Check session cache
        var existing = await LoadSessionAsync(process.Id, ct);
        if (existing is not null && IsProcessAlive(existing.ProcessId))
        {
            existing.WindowHandle = hwnd;
            RefreshWindowTitle(existing);
            return existing;
        }

        return await CreateSessionForWindow((nint)hwnd, (int)pid, null, hwnd.ToString(), forceMode, ct);
    }

    private async Task<UiSessionInfo> CreateSessionForWindow(nint hwnd, int pid, string? title, string appQuery, string? forceMode, CancellationToken ct)
    {
        var process = Process.GetProcessById(pid);
        var session = await CreateSessionAsync(process, appQuery, forceMode, ct);
        session.WindowHandle = hwnd;
        if (title is not null)
        {
            session.WindowTitle = title;
        }
        await SaveSessionAsync(session, ct);
        return session;
    }

    private static string GetProcessNameSafe(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "Unknown"; }
    }

    public async Task SaveSessionAsync(UiSessionInfo session, CancellationToken ct)
    {
        var path = GetSessionPath(session.ProcessId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(session, UiJsonContext.Default.UiSessionInfo);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), ct);
    }

    private async Task<UiSessionInfo> CreateSessionAsync(Process process, string appQuery, string? forceMode, CancellationToken ct)
    {
        var pipeName = $"winapp-winui-{process.Id}";
        var mode = "uia";
        string? connectedPipeName = null;

        if (forceMode is "uia")
        {
            // Forced UIA — skip all detection
            mode = "uia";
        }
        else if (NamedPipeExists(pipeName))
        {
            mode = "devtools";
            connectedPipeName = pipeName;
            logger.LogDebug("DevTools pipe found: {PipeName}", pipeName);
        }
        else if (HasDevToolsDll(process))
        {
            throw new InvalidOperationException(
                $"WinApp.WinUI detected in '{process.ProcessName}' but DevTools pipe is not ready. " +
                "Ensure window.UseWinAppTools() is called in your app startup. " +
                "Or use --mode uia to force UIA mode.");
        }

        var session = new UiSessionInfo
        {
            ProcessId = process.Id,
            ProcessName = process.ProcessName,
            WindowTitle = GetMainWindowTitle(process),
            AppQuery = appQuery,
            PipeName = connectedPipeName,
            Mode = mode,
            ConnectedAt = DateTime.UtcNow,
            Elements = new Dictionary<string, CachedElement>()
        };

        await SaveSessionAsync(session, ct);
        return session;
    }

    /// <summary>
    /// Refresh the window title from the live process (it changes with tab switches).
    /// </summary>
    private static void RefreshWindowTitle(UiSessionInfo session)
    {
        // Don't overwrite title if we have a specific HWND (title from UIA is more accurate)
        if (session.WindowHandle != 0)
        {
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

    private async Task<UiSessionInfo?> LoadSessionAsync(int pid, CancellationToken ct)
    {
        var path = GetSessionPath(pid);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize(json, UiJsonContext.Default.UiSessionInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string GetSessionPath(int pid)
    {
        var globalDir = winappDirectoryService.GetGlobalWinappDirectory();
        return Path.Combine(globalDir.FullName, "sessions", $"ui-session-{pid}.json");
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
            // Filter to those with visible windows for better disambiguation
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

            // Return null — let the UIA title search + window enumeration handle disambiguation
        }

        // Not found by PID or process name — return null to trigger UIA title search
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

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
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

    private static bool NamedPipeExists(string pipeName)
    {
        return File.Exists($@"\\.\pipe\{pipeName}");
    }

    private static bool HasDevToolsDll(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (module.ModuleName is not null &&
                    module.ModuleName.Contains("WinApp.WinUI", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Access denied or process exited — can't enumerate modules
        }

        return false;
    }
}
