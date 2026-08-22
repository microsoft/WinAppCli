// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Host window state captured before <c>wsb connect</c> starts a new client.</summary>
internal sealed record WindowsSandboxWindowSnapshot(
    IReadOnlySet<int> ExistingProcessIds,
    HWND ForegroundWindow);

/// <summary>Keeps the connected Sandbox client non-minimized, off-screen, and non-activating.</summary>
internal interface IWindowsSandboxWindowController
{
    WindowsSandboxWindowSnapshot Capture();

    Task PlaceConnectedClientAsync(
        WindowsSandboxWindowSnapshot snapshot,
        CancellationToken cancellationToken);
}

/// <summary>Controls only the new remote-session window created by the current connect operation.</summary>
internal sealed class WindowsSandboxWindowController : IWindowsSandboxWindowController
{
    internal const string RemoteSessionProcessName = "WindowsSandboxRemoteSession";
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public WindowsSandboxWindowSnapshot Capture() =>
        new(SnapshotProcessIds(), PInvoke.GetForegroundWindow());

    public async Task PlaceConnectedClientAsync(
        WindowsSandboxWindowSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var deadline = DateTimeOffset.UtcNow + WindowTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (FindNewWindow(snapshot.ExistingProcessIds) is { } window)
            {
                PlaceOffScreen(window, snapshot.ForegroundWindow);
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        Trace.TraceWarning(
            "The Windows Sandbox client started, but its remote-session window was not found in time.");
    }

    internal static nint? SelectNewWindow(
        IReadOnlySet<int> existingProcessIds,
        IEnumerable<(int ProcessId, nint WindowHandle)> candidates) =>
        candidates
            .Where(candidate =>
                !existingProcessIds.Contains(candidate.ProcessId) &&
                candidate.WindowHandle != 0)
            .Select(candidate => (nint?)candidate.WindowHandle)
            .FirstOrDefault();

    private static HWND? FindNewWindow(IReadOnlySet<int> existingProcessIds)
    {
        var candidates = new List<(int ProcessId, nint WindowHandle)>();

        foreach (var process in Process.GetProcessesByName(RemoteSessionProcessName))
        {
            using (process)
            {
                process.Refresh();
                candidates.Add((process.Id, process.MainWindowHandle));
            }
        }

        return SelectNewWindow(existingProcessIds, candidates) is { } handle
            ? new HWND(handle)
            : null;
    }

    private static HashSet<int> SnapshotProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(RemoteSessionProcessName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    private static void PlaceOffScreen(HWND window, HWND previousForeground)
    {
        PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        var virtualLeft = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var virtualTop = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var offScreenX = virtualLeft - 32_000;

        _ = PInvoke.SetWindowPos(
            window,
            new HWND(1), // HWND_BOTTOM
            offScreenX,
            virtualTop,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

        if (!previousForeground.IsNull && PInvoke.GetForegroundWindow() == window)
        {
            RestoreForeground(previousForeground, window);
        }
    }

    private static unsafe void RestoreForeground(HWND previousForeground, HWND sandboxWindow)
    {
        if (!PInvoke.IsWindow(previousForeground))
        {
            return;
        }

        var currentThread = PInvoke.GetCurrentThreadId();
        var sandboxThread = PInvoke.GetWindowThreadProcessId(sandboxWindow, null);
        var previousThread = PInvoke.GetWindowThreadProcessId(previousForeground, null);
        var attachedSandbox = sandboxThread != 0 &&
            PInvoke.AttachThreadInput(currentThread, sandboxThread, true);
        var attachedPrevious = previousThread != 0 &&
            PInvoke.AttachThreadInput(currentThread, previousThread, true);

        try
        {
            _ = PInvoke.BringWindowToTop(previousForeground);

            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (PInvoke.SetForegroundWindow(previousForeground) &&
                    PInvoke.GetForegroundWindow() == previousForeground)
                {
                    return;
                }

                Thread.Sleep(25);
            }
        }
        finally
        {
            if (attachedPrevious)
            {
                _ = PInvoke.AttachThreadInput(currentThread, previousThread, false);
            }

            if (attachedSandbox)
            {
                _ = PInvoke.AttachThreadInput(currentThread, sandboxThread, false);
            }
        }
    }
}
