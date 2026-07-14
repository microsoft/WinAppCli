// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Watches stdin on a background thread and fires a stop callback when a newline is
/// received or EOF arrives after an initial grace window. Designed so that programmatic
/// callers (piped stdin) can gracefully finalize a recording by writing a newline or
/// closing their end of the pipe, while interactive users simply use Ctrl+C.
/// </summary>
internal static class StdinStopMonitor
{
    private static readonly TimeSpan DefaultGrace = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Starts the monitor on a background thread. The thread is a daemon (IsBackground=true)
    /// so it never prevents process exit.
    /// </summary>
    /// <param name="stop">Called at most once when a stop condition is met.</param>
    public static void Start(TextReader stdin, Action stop)
        => Start(stdin, DefaultGrace, stop);

    /// <param name="grace">Ignore an immediate EOF that arrives within this window with no
    /// data — protects against the "no stdin attached → instant EOF → 0-frame file" footgun.</param>
    public static void Start(TextReader stdin, TimeSpan grace, Action stop)
    {
        var sw = Stopwatch.StartNew();
        var t = new Thread(() => MonitorCore(stdin, grace, () => sw.Elapsed, stop))
        {
            IsBackground = true,
            Name = "StdinStopMonitor",
        };
        t.Start();
    }

    /// <summary>
    /// Core logic, separated for unit-testing with a controllable <paramref name="getElapsed"/>
    /// clock instead of a real <see cref="Stopwatch"/>.
    /// </summary>
    internal static void MonitorCore(TextReader stdin, TimeSpan grace, Func<TimeSpan> getElapsed, Action stop)
    {
        string? line;
        try
        {
            line = stdin.ReadLine();
        }
        catch
        {
            // IO error on stdin — treat as EOF.
            line = null;
        }

        if (line is not null)
        {
            // A newline (even an empty one) always triggers a stop.
            stop();
            return;
        }

        // EOF: only stop if we are past the grace window.
        // An immediate EOF with no data read likely means stdin is not attached
        // (e.g. the process was launched without a pipe) — ignore it so the caller
        // can still use Ctrl+C or --duration-sec to stop.
        if (getElapsed() >= grace)
        {
            stop();
        }
    }
}
