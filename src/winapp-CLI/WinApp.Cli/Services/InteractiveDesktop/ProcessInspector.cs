// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Process facts the coordinator needs: the immediate parent of this process (for parent-derived owner
/// identity, spec §5.2) and whether a recorded participant is still alive (for pruning, spec §10.1).
/// </summary>
/// <remarks>
/// Extracted behind an interface because every fact here is unavailable or unstable in a unit test:
/// the parent of the test host is the test runner, and liveness answers change under the test. The
/// production implementation is <see cref="ProcessInspector"/>.
/// </remarks>
internal interface IProcessInspector
{
    /// <summary>This process's id.</summary>
    int CurrentProcessId { get; }

    /// <summary>
    /// This process's <c>Process.StartTime.ToUniversalTime().Ticks</c>, which pairs with the PID to form
    /// a reuse-proof participant identity.
    /// </summary>
    long CurrentProcessStartTicksUtc { get; }

    /// <summary>The Windows session this process runs in. Coordination is scoped per session.</summary>
    int CurrentSessionId { get; }

    /// <summary>
    /// The immediate parent process id, or <see langword="null"/> when it cannot be read. Never walks
    /// farther up the tree — a higher ancestor may be shared by unrelated workflows (spec §5).
    /// </summary>
    int? TryGetParentProcessId();

    /// <summary>
    /// A process's start ticks, or <see langword="null"/> when the process is gone or its start time
    /// cannot be read (for example a protected or higher-integrity process).
    /// </summary>
    long? TryGetProcessStartTicksUtc(int processId);

    /// <summary>
    /// Whether <paramref name="processId"/> is running <em>and</em> started at
    /// <paramref name="startTicksUtc"/>. Returns <see langword="null"/> when liveness cannot be
    /// determined, which callers must treat as "assume alive" rather than as death.
    /// </summary>
    bool? IsProcessAlive(int processId, long startTicksUtc);
}

/// <summary>
/// Production <see cref="IProcessInspector"/>. Parent discovery uses a Toolhelp process snapshot,
/// which needs no special privileges and, unlike <c>NtQueryInformationProcess</c>, is a documented
/// stable API.
/// </summary>
internal sealed class ProcessInspector : IProcessInspector
{
    private readonly int _currentProcessId;
    private readonly long _currentStartTicks;
    private readonly int _sessionId;

    public ProcessInspector()
    {
        using var current = Process.GetCurrentProcess();
        _currentProcessId = current.Id;
        _currentStartTicks = current.StartTime.ToUniversalTime().Ticks;
        _sessionId = current.SessionId;
    }

    public int CurrentProcessId => _currentProcessId;

    public long CurrentProcessStartTicksUtc => _currentStartTicks;

    public int CurrentSessionId => _sessionId;

    /// <remarks>
    /// Coverage ceiling (issue #630): the Toolhelp snapshot walk is a native enumeration of live
    /// processes. Tests drive callers through <see cref="IProcessInspector"/> instead.
    /// </remarks>
    public int? TryGetParentProcessId()
    {
        try
        {
            return TryGetParentProcessIdCore(_currentProcessId);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Snapshot creation can fail under low resources or a restricted token. Spec §5.3: fall back
            // to an anonymous one-command owner rather than guessing at an ancestor.
            return null;
        }
    }

    private static unsafe int? TryGetParentProcessIdCore(int processId)
    {
        using var snapshot = Windows.Win32.PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            Windows.Win32.System.Diagnostics.ToolHelp.CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid)
        {
            return null;
        }

        var entry = new Windows.Win32.System.Diagnostics.ToolHelp.PROCESSENTRY32
        {
            dwSize = (uint)sizeof(Windows.Win32.System.Diagnostics.ToolHelp.PROCESSENTRY32),
        };

        if (!Windows.Win32.PInvoke.Process32First(snapshot, ref entry))
        {
            return null;
        }

        do
        {
            if (entry.th32ProcessID == (uint)processId)
            {
                var parent = (int)entry.th32ParentProcessID;
                return parent > 0 ? parent : null;
            }
        }
        while (Windows.Win32.PInvoke.Process32Next(snapshot, ref entry));

        return null;
    }

    public long? TryGetProcessStartTicksUtc(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ArgumentException: no such process. InvalidOperationException: exited between calls.
            // Win32Exception: start time unreadable (protected / higher integrity). All mean "unknown".
            return null;
        }
    }

    public bool? IsProcessAlive(int processId, long startTicksUtc)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            // A matching start time proves this is the same process, not a recycled PID.
            return process.StartTime.ToUniversalTime().Ticks == startTicksUtc;
        }
        catch (ArgumentException)
        {
            // No process with that id is running — definitively dead.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited between lookup and property read — definitively dead.
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process exists but its start time is unreadable. Spec §5.2/§10.1: an unreadable
            // liveness answer must not be treated as death, or a live owner could be evicted.
            return null;
        }
    }

    /// <summary>
    /// Formats process start ticks the way lease filenames and every start-time comparison require:
    /// invariant-culture signed 64-bit decimal with no sign for positives, no grouping and no
    /// locale-specific digits (spec §8).
    /// </summary>
    public static string FormatStartTicks(long startTicksUtc)
        => startTicksUtc.ToString(CultureInfo.InvariantCulture);
}
