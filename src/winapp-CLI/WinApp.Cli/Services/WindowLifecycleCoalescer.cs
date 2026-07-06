// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// De-duplicates window open/close notifications delivered by the WinEvent hook.
/// </summary>
/// <remarks>
/// A single logical window open produces multiple WinEvents — <c>EVENT_OBJECT_CREATE</c> and
/// <c>EVENT_OBJECT_SHOW</c> — and a close produces <c>EVENT_OBJECT_DESTROY</c> and
/// <c>EVENT_OBJECT_HIDE</c>. Both members of each pair map to the same logical event
/// (<c>window-open</c> / <c>window-close</c>) for the same HWND, so emitting on each would
/// double-count. This coalescer collapses same-kind events for the same HWND that arrive within a
/// short coalescing window into a single emission, while still allowing a genuine
/// open → close → open sequence to emit each transition. It is not thread-safe by itself; the
/// watcher only calls it from its single pump thread.
/// </remarks>
internal sealed class WindowLifecycleCoalescer
{
    /// <summary>Default coalescing window: CREATE+SHOW / DESTROY+HIDE pairs arrive within a few ms.</summary>
    private const long DefaultWindowTicks = TimeSpan.TicksPerMillisecond * 500;

    /// <summary>Prune stale per-HWND entries once the map grows past this, to bound memory on long watches.</summary>
    private const int MaxTrackedWindows = 256;

    private readonly long _windowTicks;
    private readonly Dictionary<long, (bool IsOpen, long Ticks)> _last = new();

    public WindowLifecycleCoalescer(long? windowTicks = null)
        => _windowTicks = windowTicks ?? DefaultWindowTicks;

    /// <summary>
    /// Decide whether a window lifecycle event should be emitted.
    /// </summary>
    /// <param name="hwnd">The window handle the event refers to.</param>
    /// <param name="isOpen"><see langword="true"/> for an open (CREATE/SHOW), <see langword="false"/> for a close (DESTROY/HIDE).</param>
    /// <param name="nowTicks">A monotonic timestamp in ticks (e.g. <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> normalized, or <see cref="DateTime.UtcNow"/> ticks).</param>
    /// <returns><see langword="true"/> to emit; <see langword="false"/> to suppress as a duplicate.</returns>
    public bool ShouldEmit(long hwnd, bool isOpen, long nowTicks)
    {
        // Same logical transition for the same window within the coalescing window is a duplicate member
        // of the CREATE+SHOW / DESTROY+HIDE pair. Refreshing the timestamp keeps a rapid burst collapsed.
        var suppress = _last.TryGetValue(hwnd, out var prev) &&
                       prev.IsOpen == isOpen &&
                       nowTicks - prev.Ticks <= _windowTicks;

        _last[hwnd] = (isOpen, nowTicks);

        // A process that spawns many transient windows would otherwise grow _last unbounded over a long
        // watch. Once past the cap, drop entries well older than the coalescing window (they can never
        // suppress anything again).
        if (_last.Count > MaxTrackedWindows)
        {
            PruneStale(nowTicks);
        }

        return !suppress;
    }

    private void PruneStale(long nowTicks)
    {
        var cutoff = _windowTicks * 8;
        List<long>? stale = null;
        foreach (var kv in _last)
        {
            if (nowTicks - kv.Value.Ticks > cutoff)
            {
                (stale ??= []).Add(kv.Key);
            }
        }
        if (stale is not null)
        {
            foreach (var key in stale) { _last.Remove(key); }
        }
    }
}
