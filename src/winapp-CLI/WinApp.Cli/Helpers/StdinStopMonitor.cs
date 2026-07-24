// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Watches stdin on a background thread and fires a stop callback when a newline is
/// received or EOF arrives. The stop is gated on a caller-supplied readiness task so
/// that a stop signal detected before the encoder is initialized is latched and applied
/// only once the encoder is live — preventing the "cancel before encoder exists" race.
/// Designed so that programmatic callers (piped stdin) can gracefully finalize a
/// recording by writing a newline or closing their end of the pipe, while interactive
/// users simply use Ctrl+C (the monitor is only started when stdin is redirected).
/// </summary>
internal static class StdinStopMonitor
{
    /// <summary>
    /// Starts the monitor on a background thread. Reads stdin eagerly; when a newline or
    /// EOF arrives, waits for <paramref name="readyTask"/> and then invokes
    /// <paramref name="stop"/> exactly once. The thread is a daemon (IsBackground=true)
    /// so it never prevents process exit.
    /// </summary>
    /// <param name="readyTask">
    /// Completes when the encoder is initialized and the first frame has been captured.
    /// Any stop signal that arrives before this point is latched and applied as soon as
    /// the task completes, so the encoder always exists when <paramref name="stop"/> runs.
    /// </param>
    /// <param name="stop">Called at most once when a stop condition is met.</param>
    public static void Start(TextReader stdin, Task readyTask, Action stop)
    {
        var t = new Thread(() => MonitorCore(stdin, readyTask, stop))
        {
            IsBackground = true,
            Name = "StdinStopMonitor",
        };
        t.Start();
    }

    /// <summary>
    /// Core logic, separated for unit-testing with a controllable <paramref name="readyTask"/>
    /// instead of a real background thread.
    /// </summary>
    internal static void MonitorCore(TextReader stdin, Task readyTask, Action stop)
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

        // A newline (even empty) or EOF both signal a stop. Gate on readiness first so the
        // encoder is initialized before we request finalization — avoids the race where
        // Cancel() fires before the encoder object exists, which caused internal_error in
        // round 2. The stop is detected eagerly; only the application is deferred.
        readyTask.GetAwaiter().GetResult();
        stop();
    }
}
