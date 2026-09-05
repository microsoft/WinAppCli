// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Host window state captured before <c>wsb connect</c> starts a new client.</summary>
internal sealed record WindowsSandboxWindowSnapshot(
    IReadOnlySet<int> ExistingProcessIds,
    HWND ForegroundWindow);

/// <summary>
/// One live Sandbox client window on the host, identified by more than its handle.
/// </summary>
/// <remarks>
/// A window handle alone is not an identity: Windows recycles handles, and it recycles process IDs
/// too. Pairing them with the process start time is what makes the record stable enough to persist
/// and re-check from a later winapp process, which is the whole reason this is a record rather than
/// a bare <see cref="nint"/>.
/// </remarks>
/// <param name="Handle">Top-level window handle of the remote-session client.</param>
/// <param name="ProcessId">Host process that owns it.</param>
/// <param name="StartTicksUtc">
/// UTC ticks that process started, or 0 when Windows would not report it.
/// </param>
internal sealed record SandboxClientWindow(nint Handle, int ProcessId, long StartTicksUtc);

/// <summary>Keeps the connected Sandbox client non-minimized, off-screen, and non-activating.</summary>
internal interface IWindowsSandboxWindowController
{
    WindowsSandboxWindowSnapshot Capture();

    /// <summary>
    /// Places the client this connect created, and reports which window that was.
    /// </summary>
    /// <returns>
    /// The placed client, or null when no new window appeared in time, or when winapp could not
    /// prove exactly one belonged to this connect — several appeared at once, or a second appeared
    /// while the first was being confirmed. Nothing is moved or recorded in those cases.
    /// </returns>
    Task<SandboxClientWindow?> PlaceConnectedClientAsync(
        WindowsSandboxWindowSnapshot snapshot,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the client window a capture may use, preferring one winapp recorded connecting.
    /// </summary>
    /// <param name="remembered">
    /// The client winapp recorded connecting, read back from persisted target state. Null when there
    /// is no record — an adopted Sandbox, or state written before this field existed.
    /// </param>
    /// <exception cref="ExecutionTargetException">
    /// No client window is open, or several are and <paramref name="remembered"/> is not among them.
    /// </exception>
    SandboxClientWindow ResolveClient(SandboxClientWindow? remembered);
}

/// <summary>Controls only the new remote-session window created by the current connect operation.</summary>
internal sealed class WindowsSandboxWindowController : IWindowsSandboxWindowController
{
    internal const string RemoteSessionProcessName = "WindowsSandboxRemoteSession";
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a lone new client must stay the only new client before winapp claims it.
    /// </summary>
    /// <remarks>
    /// A client process appears a measurable moment before the next one, so the first poll that
    /// sees exactly one new window is not evidence that only one was created — it is evidence that
    /// only one has appeared <em>so far</em>. Claiming it there is what lets a second connect
    /// starting at the same time have its window parked, persisted, and later captured as if it were
    /// winapp's. Watching for a further window before committing turns that silent mistake into the
    /// ambiguity it actually is.
    /// <para>
    /// Sized against what it defends: the whole risk window is the gap between two nearly
    /// simultaneous connects, which is milliseconds, and the cost is a fixed addition to a connect
    /// that already takes seconds.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan ClientStabilizationWindow = TimeSpan.FromMilliseconds(750);

    private readonly Func<IReadOnlyList<SandboxClientWindow>> _listClients;
    private readonly Action<SandboxClientWindow, HWND> _park;

    /// <summary>Creates a controller that reads the real desktop.</summary>
    public WindowsSandboxWindowController()
        : this(ListLiveClients, (client, foreground) => PlaceOffScreen(new HWND(client.Handle), foreground))
    {
    }

    /// <summary>Creates a controller over scripted windows and placement, for tests.</summary>
    internal WindowsSandboxWindowController(
        Func<IReadOnlyList<SandboxClientWindow>> listClients,
        Action<SandboxClientWindow, HWND>? park = null)
    {
        _listClients = listClients;
        _park = park ?? ((client, foreground) => PlaceOffScreen(new HWND(client.Handle), foreground));
    }

    /// <summary>Delay seam, so the stabilization window is exercised without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clock seam, so timeouts are exercised without real time passing.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public WindowsSandboxWindowSnapshot Capture() =>
        new(SnapshotProcessIds(), PInvoke.GetForegroundWindow());

    public async Task<SandboxClientWindow?> PlaceConnectedClientAsync(
        WindowsSandboxWindowSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var deadline = UtcNow() + WindowTimeout;
        while (UtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (client, ambiguous) = SelectNewClient(snapshot.ExistingProcessIds, _listClients());

            if (ambiguous)
            {
                WarnAmbiguous();
                return null;
            }

            if (client is not null)
            {
                var settled = await SettleAsync(snapshot, client, cancellationToken).ConfigureAwait(false);
                if (settled is null)
                {
                    // Either a second client appeared while watching, or the candidate went away.
                    // Both are reasons to claim nothing rather than guess.
                    return null;
                }

                _park(settled, snapshot.ForegroundWindow);
                return settled;
            }

            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        Trace.TraceWarning(
            "The Windows Sandbox client started, but its remote-session window was not found in time.");
        return null;
    }

    /// <summary>
    /// Watches a lone new client for <see cref="ClientStabilizationWindow"/> and returns it only if
    /// it stays the only new one.
    /// </summary>
    /// <remarks>
    /// Nothing is parked or persisted until this returns non-null, so a client that turns out to
    /// belong to a concurrent <c>wsb connect</c> is never moved off-screen by winapp and never
    /// recorded as winapp's own.
    /// </remarks>
    private async Task<SandboxClientWindow?> SettleAsync(
        WindowsSandboxWindowSnapshot snapshot,
        SandboxClientWindow candidate,
        CancellationToken cancellationToken)
    {
        var settleUntil = UtcNow() + ClientStabilizationWindow;
        while (UtcNow() < settleUntil)
        {
            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var (recheck, ambiguous) = SelectNewClient(snapshot.ExistingProcessIds, _listClients());

            if (ambiguous || (recheck is not null && recheck != candidate))
            {
                WarnAmbiguous();
                return null;
            }

            if (recheck is null)
            {
                // The window winapp was about to claim closed again. Report nothing rather than
                // persist a handle that is already gone.
                Trace.TraceWarning(
                    "The new Windows Sandbox client window disappeared before winapp could claim it.");
                return null;
            }
        }

        return candidate;
    }

    private static void WarnAmbiguous() =>
        Trace.TraceWarning(
            "Several new Windows Sandbox client windows appeared at once, so winapp cannot tell " +
            "which one it connected; leaving them where they are.");

    /// <inheritdoc/>
    public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) =>
        ResolveClient(remembered, _listClients());

    /// <summary>
    /// Picks the client this connect created out of everything running now.
    /// </summary>
    /// <remarks>
    /// Selection is by <em>absence from the pre-connect snapshot</em>, never "the first process with
    /// that name": Windows routinely leaves earlier remote-session processes behind, and one of
    /// those is exactly as likely to be first. When two windows are new at once there is nothing to
    /// tell them apart, so neither is chosen.
    /// </remarks>
    internal static (SandboxClientWindow? Client, bool Ambiguous) SelectNewClient(
        IReadOnlySet<int> existingProcessIds,
        IEnumerable<SandboxClientWindow> candidates)
    {
        ArgumentNullException.ThrowIfNull(existingProcessIds);
        ArgumentNullException.ThrowIfNull(candidates);

        var appeared = candidates
            .Where(candidate =>
                !existingProcessIds.Contains(candidate.ProcessId) &&
                candidate.Handle != 0)
            .ToList();

        return appeared.Count switch
        {
            0 => (null, false),
            1 => (appeared[0], false),
            _ => (null, true),
        };
    }

    /// <summary>
    /// Decides which live client window a capture may use.
    /// </summary>
    /// <remarks>
    /// A recorded client is honoured only while it is still one of the windows actually open, which
    /// is what stops a handle persisted by an earlier winapp process from resolving against whatever
    /// owns that number now. With no usable record, exactly one open client is unambiguous and is
    /// adopted; zero or several fail, because the alternative is capturing a desktop winapp does not
    /// manage and reporting it as this target's.
    /// </remarks>
    internal static SandboxClientWindow ResolveClient(
        SandboxClientWindow? remembered,
        IReadOnlyList<SandboxClientWindow> live)
    {
        ArgumentNullException.ThrowIfNull(live);

        if (remembered is not null && live.Contains(remembered))
        {
            return remembered;
        }

        if (live.Count == 1)
        {
            return live[0];
        }

        if (live.Count == 0)
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.NoInteractiveSession,
                "The Windows Sandbox window is not open on this machine, so there is nothing to capture.",
                userAction:
                    "Run a command that needs the Sandbox desktop, such as 'winapp run . --on sandbox', so " +
                    "winapp reconnects the window, then retry.",
                example: "winapp target screenshot sandbox -o .\\sandbox.png");
        }

        throw ExecutionTargetException.Create(
            ExecutionTargetErrorCodes.TargetAmbiguous,
            $"{live.Count} Windows Sandbox windows are open, and winapp cannot prove which one it manages.",
            userAction:
                "Close the Windows Sandbox windows winapp did not open, then run a command that reconnects " +
                "its own, such as 'winapp run . --on sandbox'.",
            context: new Dictionary<string, string>
            {
                ["clientProcessIds"] = string.Join(
                    ',',
                    live.Select(client => client.ProcessId.ToString(CultureInfo.InvariantCulture))),
            });
    }

    /// <summary>Every remote-session client window open on this desktop right now.</summary>
    private static IReadOnlyList<SandboxClientWindow> ListLiveClients()
    {
        var clients = new List<SandboxClientWindow>();

        foreach (var process in Process.GetProcessesByName(RemoteSessionProcessName))
        {
            using (process)
            {
                process.Refresh();

                if (process.MainWindowHandle != 0)
                {
                    clients.Add(new SandboxClientWindow(
                        process.MainWindowHandle,
                        process.Id,
                        TryReadStartTicks(process)));
                }
            }
        }

        return clients;
    }

    /// <summary>UTC start ticks, or 0 when Windows will not say.</summary>
    /// <remarks>
    /// Zero is a legitimate answer rather than a failure: it only weakens the record to handle plus
    /// process ID, which still has to match a live window before it is used.
    /// </remarks>
    private static long TryReadStartTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
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
