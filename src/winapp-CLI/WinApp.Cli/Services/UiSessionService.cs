// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class UiSessionService(
    IUiAutomationService uiAutomation,
    ISystemUiQuery systemQuery,
    ILogger<UiSessionService> logger) : IUiSessionService
{
    // The window-metadata reads live on ISystemUiQuery so the resolver's selection logic is unit-
    // testable. GetWindowInfo/GetWindowClassName must stay static (they're called statically by
    // UiListWindowsCommand, UiScreenshotCommand, and UiAutomationService), so those shims route
    // through this shared real implementation while instance callers use the injected seam. Both
    // point at the same OS boundary; tests inject a fake for the instance path.
    private static readonly SystemUiQuery s_sharedQuery = new();

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
                throw new AppNotFoundException($"No running app found matching '{app}'.");
            }
            if (windows.Count > 1)
            {
                return Task.FromResult(AutoSelectWindow(windows, app));
            }
            // Single match
            var match = windows[0];
            return Task.FromResult(CreateSession(match.Pid, match.Hwnd, match.Title));
        }

        var resolved = process.Value;

        // Process found — check for multiple windows
        var processWindows = uiAutomation.FindWindowsByPid(resolved.Id);
        if (processWindows.Count > 1)
        {
            return Task.FromResult(AutoSelectWindow(processWindows, app));
        }

        if (processWindows.Count == 1)
        {
            return Task.FromResult(CreateSession(resolved.Id, processWindows[0].Hwnd, processWindows[0].Title));
        }

        return Task.FromResult(new UiSessionInfo
        {
            ProcessId = resolved.Id,
            ProcessName = resolved.ProcessName,
            WindowTitle = GetMainWindowTitle(resolved)
        });
    }

    /// <summary>
    /// Auto-selects the best window from multiple candidates silently.
    /// Heuristic: prefer foreground window → prefer largest window.
    /// </summary>
    private UiSessionInfo AutoSelectWindow(List<(nint Hwnd, int Pid, string Title)> windows, string app)
    {
        var foregroundHwnd = systemQuery.GetForegroundWindow();
        var foreground = windows.FirstOrDefault(w => w.Hwnd == foregroundHwnd);
        var selected = foreground != default ? foreground : PickLargestWindow(windows);

        var reason = foreground != default ? "foreground" : "largest";
        logger.LogInformation("Auto-selected HWND {Hwnd} ({Reason}) from {Count} windows for '{App}' — pass -w {ExplicitHwnd} to target this window explicitly.", selected.Hwnd, reason, windows.Count, app, selected.Hwnd);

        return CreateSession(selected.Pid, selected.Hwnd, selected.Title);
    }

    /// <summary>Pick the window with the largest area (queried through the OS-boundary seam).</summary>
    private (nint Hwnd, int Pid, string Title) PickLargestWindow(List<(nint Hwnd, int Pid, string Title)> windows)
    {
        var best = windows[0];
        long bestArea = 0;

        foreach (var w in windows)
        {
            var (width, height) = systemQuery.GetWindowSize((long)w.Hwnd);
            var area = (long)width * height;
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
        var (width, height) = s_sharedQuery.GetWindowSize((long)hwnd);
        var ownerHwnd = s_sharedQuery.GetWindowOwner((long)hwnd);

        return new WindowMetadata
        {
            ClassName = className ?? "Unknown",
            Label = label,
            Width = width,
            Height = height,
            OwnerHwnd = ownerHwnd
        };
    }

    internal static string? GetWindowClassName(nint hwnd) => s_sharedQuery.GetWindowClassName((long)hwnd);

    internal static string ClassifyWindow(string? className)
    {
        if (className is null) { return "window"; }
        if (className.Contains("Popup", StringComparison.OrdinalIgnoreCase)) { return "popup"; }
        if (className == "#32770") { return "dialog"; }
        return "window";
    }

    internal record WindowMetadata
    {
        public string ClassName { get; init; } = "Unknown";
        public string Label { get; init; } = "window";
        public int Width { get; init; }
        public int Height { get; init; }
        public nint OwnerHwnd { get; init; }
    }

    private UiSessionInfo ResolveByHwnd(long hwnd)
    {
        var pid = systemQuery.GetProcessIdForWindow(hwnd);
        if (pid == 0)
        {
            throw new AppNotFoundException($"Window HWND {hwnd} not found or not accessible.");
        }

        var session = CreateSession((int)pid, (nint)hwnd, null);
        session.IsExplicitWindow = true;
        RefreshWindowTitle(session);
        return session;
    }

    private UiSessionInfo CreateSession(int pid, nint hwnd, string? title)
    {
        var processName = systemQuery.GetProcessById(pid)?.ProcessName;
        if (string.IsNullOrEmpty(processName)) { processName = "Unknown"; }

        return new UiSessionInfo
        {
            ProcessId = pid,
            ProcessName = processName,
            WindowTitle = title,
            WindowHandle = hwnd
        };
    }

    /// <summary>
    /// Refresh the window title from the session's explicit HWND. Only ever called after
    /// <see cref="ResolveByHwnd"/>, which always sets a non-zero <see cref="UiSessionInfo.WindowHandle"/>.
    /// </summary>
    private void RefreshWindowTitle(UiSessionInfo session)
    {
        var title = systemQuery.GetWindowText(session.WindowHandle);
        if (!string.IsNullOrEmpty(title))
        {
            session.WindowTitle = title;
        }
    }

    private UiProcessInfo? TryResolveProcess(string app)
    {
        // Try as PID
        if (int.TryParse(app, out var pid))
        {
            var byPid = systemQuery.GetProcessById(pid);
            if (byPid is null)
            {
                throw new AppNotFoundException($"No process found with PID {pid}.");
            }
            return byPid;
        }

        // Try exact process name
        var exact = SelectProcess(systemQuery.GetProcessesByName(app), app, partial: false);
        if (exact is not null)
        {
            return exact;
        }

        // Try partial process name match (e.g., "imageresizer" matches "PowerToys.ImageResizer")
        return SelectProcess(systemQuery.GetProcessesMatching(app), app, partial: true);
    }

    /// <summary>
    /// Choose a single process from the candidate snapshots. Returns the match (logging a note for
    /// partial matches), <c>null</c> when there is no usable match, or throws with a disambiguation
    /// listing when multiple windowed processes qualify.
    /// </summary>
    private UiProcessInfo? SelectProcess(IReadOnlyList<UiProcessInfo> candidates, string app, bool partial)
    {
        if (candidates.Count == 1)
        {
            var only = candidates[0];
            if (partial) { LogPartialMatch(app, only); }
            return only;
        }

        if (candidates.Count > 1)
        {
            var withWindow = candidates
                .Where(p => p.MainWindowHandle != 0 && !string.IsNullOrEmpty(p.MainWindowTitle))
                .ToList();

            if (withWindow.Count == 1)
            {
                var single = withWindow[0];
                if (partial) { LogPartialMatch(app, single); }
                return single;
            }

            if (withWindow.Count > 1)
            {
                var listing = string.Join("\n  ",
                    withWindow.Select(p => partial
                        ? $"PID {p.Id} ({p.ProcessName}): \"{p.MainWindowTitle}\""
                        : $"PID {p.Id}: \"{p.MainWindowTitle}\""));
                var header = partial
                    ? $"Multiple processes matching '{app}' found:"
                    : $"Multiple '{app}' windows found:";
                throw new InvalidOperationException(
                    $"{header}\n  {listing}\n" +
                    "Use --app with a PID or a more specific window title.");
            }
        }

        return null;
    }

    private void LogPartialMatch(string input, UiProcessInfo matched)
    {
        // Surface partial-name matches so users notice when a short/typoed --app value
        // resolved to an unrelated process (issue #467).
        logger.LogInformation(
            "Partial process-name match: '{Input}' resolved to '{ProcessName}' (PID {Pid}). Pass the full process name or PID to disambiguate.",
            input, matched.ProcessName, matched.Id);
    }

    private static string? GetMainWindowTitle(UiProcessInfo process)
        => string.IsNullOrEmpty(process.MainWindowTitle) ? null : process.MainWindowTitle;
}
