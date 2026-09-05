// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.ExecutionTargets.WindowsSandbox;

/// <summary>Host desktop state captured before <c>wsb connect</c> starts a new client.</summary>
/// <param name="ForegroundWindow">
/// What the user was working in, so parking the client can hand focus straight back.
/// </param>
internal sealed record WindowsSandboxWindowSnapshot(HWND ForegroundWindow);

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

/// <summary>A live client window together with the evidence used to attribute it.</summary>
/// <param name="Window">The window itself.</param>
/// <param name="ParentProcessId">
/// The host process that created this client, or null when Windows would not say. This is the
/// evidence: Windows Sandbox spawns the client as a direct child of the <c>wsb connect</c> process
/// that asked for it, so a client whose parent is the launcher winapp started is winapp's, and one
/// whose parent is anything else is somebody else's.
/// </param>
internal sealed record SandboxClientCandidate(SandboxClientWindow Window, int? ParentProcessId);

/// <summary>Keeps the connected Sandbox client non-minimized, off-screen, and non-activating.</summary>
internal interface IWindowsSandboxWindowController
{
    WindowsSandboxWindowSnapshot Capture();

    /// <summary>
    /// Places the client that <paramref name="ownership"/> created, and reports which window it was.
    /// </summary>
    /// <param name="snapshot">Host desktop state from before the connect, for restoring focus.</param>
    /// <param name="ownership">
    /// The <c>wsb connect</c> winapp launched. Null when winapp could not identify it, in which case
    /// nothing is claimed: there is no evidence left to tell winapp's client from anyone else's.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the client window.</param>
    /// <returns>
    /// The placed client, or null when winapp could not prove which window this connect created.
    /// Nothing is moved and nothing is recorded in that case.
    /// </returns>
    Task<SandboxClientWindow?> PlaceConnectedClientAsync(
        WindowsSandboxWindowSnapshot snapshot,
        SandboxConnectOwnership? ownership,
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

/// <summary>Controls only the remote-session window the current connect actually created.</summary>
/// <remarks>
/// <para>
/// Which window belongs to which connect is established by <em>parentage</em>, not by timing or by
/// novelty. Windows Sandbox creates the client as a direct child of the <c>wsb connect</c> process
/// winapp started, so "the client whose parent is my launcher" is a fact about the process tree that
/// stays true no matter how many other connects run at the same moment. The client must also be no
/// older than that launcher, since a process ID Windows recorded as a parent long ago may since have
/// been recycled onto winapp's own connect.
/// </para>
/// <para>
/// That distinction is the whole point. Selecting the new window that happened to appear first would
/// pick a concurrent caller's client whenever theirs arrived first, and no amount of waiting can
/// turn "nothing else has shown up yet" into proof of ownership. When the proof is unavailable —
/// Windows would not report the parent, or the launcher could not be identified — winapp claims
/// nothing: the client is left visible and unrecorded rather than parked off-screen on a guess.
/// </para>
/// </remarks>
internal sealed class WindowsSandboxWindowController : IWindowsSandboxWindowController
{
    internal const string RemoteSessionProcessName = "WindowsSandboxRemoteSession";
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<IReadOnlyList<SandboxClientCandidate>> _listClients;
    private readonly Action<SandboxClientWindow, HWND> _park;

    /// <summary>Creates a controller that reads the real desktop.</summary>
    public WindowsSandboxWindowController()
        : this(ListLiveClients, (client, foreground) => PlaceOffScreen(new HWND(client.Handle), foreground))
    {
    }

    /// <summary>Creates a controller over scripted windows and placement, for tests.</summary>
    internal WindowsSandboxWindowController(
        Func<IReadOnlyList<SandboxClientCandidate>> listClients,
        Action<SandboxClientWindow, HWND>? park = null)
    {
        _listClients = listClients;
        _park = park ?? ((client, foreground) => PlaceOffScreen(new HWND(client.Handle), foreground));
    }

    /// <summary>Delay seam, so waiting for the client is exercised without real waiting.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Clock seam, so timeouts are exercised without real time passing.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    public WindowsSandboxWindowSnapshot Capture() => new(PInvoke.GetForegroundWindow());

    public async Task<SandboxClientWindow?> PlaceConnectedClientAsync(
        WindowsSandboxWindowSnapshot snapshot,
        SandboxConnectOwnership? ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (ownership is null)
        {
            // Without the launcher there is no way to tell winapp's client from a concurrent
            // caller's, and moving the wrong window off-screen is worse than moving none.
            Trace.TraceWarning(
                "winapp could not identify the Windows Sandbox client it launched, so the window was " +
                "left where the Sandbox put it.");
            return null;
        }

        var deadline = UtcNow() + WindowTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (client, ambiguous) = SelectOwnedClient(ownership, _listClients());

            if (ambiguous)
            {
                Trace.TraceWarning(
                    "One 'wsb connect' produced several Windows Sandbox client windows, so winapp " +
                    "cannot tell which one it asked for; leaving them where they are.");
                return null;
            }

            if (client is not null)
            {
                _park(client, snapshot.ForegroundWindow);
                return client;
            }

            if (UtcNow() >= deadline)
            {
                break;
            }

            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        Trace.TraceWarning(
            "The Windows Sandbox client started, but no remote-session window winapp launched " +
            "appeared in time.");
        return null;
    }

    /// <summary>
    /// Picks the client the launcher in <paramref name="ownership"/> created.
    /// </summary>
    /// <remarks>
    /// Selection is by parentage <em>and</em> age. A client another <c>wsb connect</c> created is not
    /// a weaker candidate here, it is not a candidate at all, so this returns the same answer whether
    /// that other client appeared before winapp's, after it, or never.
    /// <para>
    /// The age test is what keeps the parent ID honest. Windows records a client's parent ID once, at
    /// creation, and never revises it — a client outlives its launcher, and once that launcher's ID
    /// is recycled the client goes on naming a number that now belongs to something else, possibly to
    /// winapp's own <c>wsb connect</c>. A window that already existed before winapp launched anything
    /// cannot have been created by it, so requiring the client to be no older than its claimed parent
    /// rejects exactly those stale matches. <c>&gt;=</c> rather than <c>&gt;</c>, because a child
    /// started immediately can share its parent's timestamp at the granularity Windows records, and
    /// the case being excluded is a client that started measurably <em>earlier</em>.
    /// </para>
    /// <para>
    /// A client whose start time Windows will not report (0) is not claimed at all: its age cannot be
    /// compared, so its parentage cannot be trusted either.
    /// </para>
    /// <para>
    /// Two clients parented to one launcher is not something Windows Sandbox does, so it is treated
    /// as the loss of the evidence it is rather than resolved by preference.
    /// </para>
    /// </remarks>
    internal static (SandboxClientWindow? Client, bool Ambiguous) SelectOwnedClient(
        SandboxConnectOwnership ownership,
        IEnumerable<SandboxClientCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(candidates);

        var owned = candidates
            .Where(candidate =>
                candidate.Window.Handle != 0 &&
                candidate.ParentProcessId == ownership.LauncherProcessId &&
                candidate.Window.StartTicksUtc != 0 &&
                candidate.Window.StartTicksUtc >= ownership.StartTicksUtc)
            .ToList();

        return owned.Count switch
        {
            0 => (null, false),
            1 => (owned[0].Window, false),
            _ => (null, true),
        };
    }

    /// <inheritdoc/>
    public SandboxClientWindow ResolveClient(SandboxClientWindow? remembered) =>
        ResolveClient(remembered, [.. _listClients().Select(candidate => candidate.Window)]);

    /// <summary>
    /// Decides which live client window a capture may use.
    /// </summary>
    /// <remarks>
    /// A recorded client is honoured only while it is still one of the windows actually open, which
    /// is what stops a handle persisted by an earlier winapp process from resolving against whatever
    /// owns that number now.
    /// <para>
    /// With no usable record, a single open client is <em>adopted</em>: read where it stands, never
    /// moved, and reported as adopted so a caller can see that winapp recognised the window rather
    /// than created it. Only one Sandbox instance can exist at a time, so a lone client is
    /// necessarily showing this target's desktop. Zero or several fail, because the alternative is
    /// capturing a desktop winapp does not manage and reporting it as this target's.
    /// </para>
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
    private static IReadOnlyList<SandboxClientCandidate> ListLiveClients()
    {
        var clients = new List<SandboxClientCandidate>();

        foreach (var process in Process.GetProcessesByName(RemoteSessionProcessName))
        {
            using (process)
            {
                process.Refresh();

                if (process.MainWindowHandle != 0)
                {
                    clients.Add(new SandboxClientCandidate(
                        new SandboxClientWindow(
                            process.MainWindowHandle,
                            process.Id,
                            TryReadStartTicks(process)),
                        ParentProcessId.TryGet(process.Id)));
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
