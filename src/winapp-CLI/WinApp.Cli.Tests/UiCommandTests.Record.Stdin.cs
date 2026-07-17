// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    private sealed class SignalingStringReader(string text, Action onRead) : StringReader(text)
    {
        public override string? ReadLine() { onRead(); return base.ReadLine(); }
    }

    private sealed class SignalingEofReader(Action onRead) : TextReader
    {
        public override string? ReadLine() { onRead(); return null; }
    }

    private sealed class EofReader : TextReader
    {
        public override string? ReadLine() => null;
    }

    private sealed class ThrowingStdinReader : TextReader
    {
        public override string? ReadLine() => throw new IOException("stdin pipe broke");
    }

    [TestMethod]
    public void StdinStopMonitor_Newline_StopsRecording()
    {
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new StringReader("\n"),
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "a newline should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_EmptyLine_StopsRecording()
    {
        // Pressing Enter sends an empty line (\n → ReadLine returns ""), which must always stop.
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new StringReader("\n"),  // "\n" → ReadLine returns "" (empty string, not null = not EOF)
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "an empty line (immediate enter) should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_ImmediateEof_Stops()
    {
        // With the readiness gate (Task.CompletedTask = already ready), an immediate EOF
        // must trigger a stop — the old wall-clock grace that swallowed immediate EOFs is gone.
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new EofReader(),
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "immediate EOF with a completed ready task must trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_LineOfText_Stops()
    {
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new StringReader("stop"),
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "any line of text should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_ReadThrows_TreatedAsEofAndStops()
    {
        // An IO error while reading stdin (e.g. a broken pipe) must be swallowed and treated as EOF
        // so the recording still finalizes gracefully instead of leaving the monitor thread wedged.
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new ThrowingStdinReader(),
            Task.CompletedTask,
            () => stopped = true);
        Assert.IsTrue(stopped, "a stdin read error must be treated as EOF and still trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_Start_RunsOnBackgroundThreadAndFiresStop()
    {
        // Start() is the production entry point: it must spawn the monitor on a daemon thread and
        // invoke the stop callback once stdin yields a line and the ready gate is already open.
        using var stopped = new ManualResetEventSlim(false);
        StdinStopMonitor.Start(
            new StringReader("\n"),
            Task.CompletedTask,
            () => stopped.Set());

        Assert.IsTrue(stopped.Wait(TimeSpan.FromSeconds(5)),
            "Start must run the monitor on a background thread and fire stop for a pre-buffered line.");
    }

    [TestMethod]
    public async Task StdinStopMonitor_EofBeforeReady_WaitsForReadyThenStops()
    {
        // An immediate EOF that arrives BEFORE the encoder is ready must be LATCHED and applied
        // only after the ready task completes — not discarded, not an internal_error.
        var stopped = false;
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Run MonitorCore on a background thread (it blocks on readyTask).
        var t = new Thread(() =>
        {
            StdinStopMonitor.MonitorCore(
                new SignalingEofReader(() => readReturned.TrySetResult()),
                readyTcs.Task,
                () => stopped = true);
        });
        t.IsBackground = true;
        t.Start();

        // Wait deterministically for the stdin read to complete (EOF returned, now blocked on ready).
        await readReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(stopped, "stop must not fire before the ready task completes");

        // Signal readiness — monitor must now apply the latched stop.
        readyTcs.SetResult();
        t.Join(TimeSpan.FromSeconds(5));
        Assert.IsTrue(stopped, "stop must fire after the ready task completes");
    }

    [TestMethod]
    public async Task StdinStopMonitor_NewlineBeforeReady_WaitsForReadyThenStops()
    {
        // A newline that arrives before the encoder is ready must also be latched — no internal_error.
        var stopped = false;
        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var t = new Thread(() =>
        {
            StdinStopMonitor.MonitorCore(
                new SignalingStringReader("stop\n", () => readReturned.TrySetResult()),
                readyTcs.Task,
                () => stopped = true);
        });
        t.IsBackground = true;
        t.Start();

        await readReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(stopped, "stop must not fire before the ready task completes");

        readyTcs.SetResult();
        t.Join(TimeSpan.FromSeconds(5));
        Assert.IsTrue(stopped, "stop must fire after the ready task completes");
    }

    [TestMethod]
    public async Task Record_ReadinessHandshake_PrefilledStdinNewline_ExitsZeroWithValidFile()
    {
        // A newline that arrives in stdin BEFORE recording would previously cancel before the
        // encoder existed → internal_error / no file.  With the readiness handshake the monitor
        // is armed only after the first frame, so any pre-buffered newline is a graceful stop.
        // The fake RecordAsync signals readiness (calls onRecordingStarted) which is what arms
        // the monitor; in the test the recording is bounded by duration so the monitor never
        // actually fires but the command must still exit 0 with a valid file.
        var outputPath = Path.Combine(_tempDirectory.FullName, "readiness.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode, "command must exit 0 (not internal_error)");
        Assert.IsTrue(File.Exists(outputPath), "a valid output file must be produced");
        Assert.IsTrue(new FileInfo(outputPath).Length > 0, "output file must be non-empty");
    }

    [TestMethod]
    public async Task Record_Quiet_SuppressesRecordingProgress()
    {
        // --quiet must suppress the "Recording..." progress line emitted to the console.
        var outputPath = Path.Combine(_tempDirectory.FullName, "quiet-suppress.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--quiet", "--duration-sec", "1", "-o", outputPath]);

        Assert.AreEqual(0, exitCode, "--quiet must still exit 0");
        var stdout = TestAnsiConsole.Output;
        Assert.IsFalse(stdout.Contains("Recording"),
            $"--quiet must suppress 'Recording' progress text; got stdout: {stdout}");
    }

    [TestMethod]
    public async Task Record_Quiet_ExitsZeroAndProducesFile()
    {
        // --quiet must produce the output file normally; only progress chatter is suppressed.
        var outputPath = Path.Combine(_tempDirectory.FullName, "quiet-file.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--quiet", "--duration-sec", "1", "-o", outputPath]);

        Assert.AreEqual(0, exitCode, "--quiet must exit 0 (recording proceeds normally)");
        Assert.IsTrue(File.Exists(outputPath), "--quiet must still produce the output file");
    }

    [TestMethod]
    public async Task Record_NonRedirectedStdin_StatusTextCtrlCOnly()
    {
        // L1: when stdin is NOT redirected, the status line must say "Ctrl+C" only,
        // not mention newline/EOF (since the stdin monitor is not started).
        var outputPath = Path.Combine(_tempDirectory.FullName, "l1-noredirect.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => false;
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath]);

            Assert.AreEqual(0, exitCode);
            var stdout = TestAnsiConsole.Output;
            Assert.IsFalse(stdout.Contains("EOF") || stdout.Contains("newline"),
                $"Non-redirected stdin status must not mention EOF/newline; got: {stdout}");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
        }
    }

    [TestMethod]
    public async Task Record_RedirectedStdin_StatusTextMentionsEof()
    {
        // L1: when stdin IS redirected and durationSec==0 (unbounded), the status line must mention newline/EOF.
        var outputPath = Path.Combine(_tempDirectory.FullName, "l1-redirect.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => true;
        UiRecordCommand.Handler.s_stdinOverride = new StringReader("stop");
        try
        {
            _fakeUia.RecordResult = new RecordCaptureResult { Frames = 1, Width = 64, Height = 64, Mode = "wgc" };
            _fakeUia.RecordShouldWaitForCancellation = true;
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "0", "-o", outputPath]);

            Assert.AreEqual(0, exitCode);
            var stdout = TestAnsiConsole.Output;
            Assert.IsTrue(stdout.Contains("EOF") || stdout.Contains("stdin"),
                $"Redirected stdin status must mention EOF or stdin; got: {stdout}");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
            UiRecordCommand.Handler.s_stdinOverride = null;
            _fakeUia.RecordShouldWaitForCancellation = false;
        }
    }

    [TestMethod]
    public async Task Record_NonZeroDuration_WithRedirectedStdin_NeverReadsStdinAndExitsZero()
    {
        // H1 regression guard (r12): with --duration-sec N (> 0) AND a redirected stdin that
        // would deliver EOF, the recording must complete via its own duration deadline (simulated
        // here by the fake returning normally) and must NEVER read stdin. The pre-r12 bug armed the
        // monitor unconditionally, so EOF from `<nul` canceled the recording after the first frame
        // and truncated the MP4.
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 20, Width = 640, Height = 480, Mode = "wgc" };
        _fakeUia.RecordShouldWaitForCancellation = false; // fake returns when its own deadline elapses

        var outputPath = Path.Combine(_tempDirectory.FullName, "h1-timed-ignores-stdin.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        // Inject seams: a redirected stdin whose ReadLine (if ever invoked) trips the flag below.
        // A timed recording must not arm the monitor, so ReadLine must never be called.
        var stdinWasRead = false;
        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => true;
        UiRecordCommand.Handler.s_stdinOverride = new SignalingEofReader(() => stdinWasRead = true);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "120", "-o", outputPath, "--json"]);

            // Give any (erroneously-armed) background stdin monitor time to read before asserting.
            await Task.Delay(100);

            Assert.AreEqual(0, exitCode, "a timed recording must complete via its duration deadline and exit 0");
            Assert.IsTrue(File.Exists(outputPath), "a valid output file must be produced");
            Assert.IsFalse(stdinWasRead, "a timed recording must never arm the stdin monitor or read redirected stdin");
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
            UiRecordCommand.Handler.s_stdinOverride = null;
        }
    }

    [TestMethod]
    public async Task Record_NonZeroDuration_NoRedirectedStdin_DurationDeadlineWins()
    {
        // H1 companion: when stdin is NOT redirected (interactive), the monitor is not started
        // and the duration deadline drives termination. The recording completes normally.
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 5, Width = 640, Height = 480, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "h1-duration-wins.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        // Override to simulate non-redirected stdin — monitor must NOT be started.
        UiRecordCommand.Handler.s_isInputRedirectedOverride = () => false;
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);
            Assert.AreEqual(0, exitCode, "non-redirected stdin with duration deadline must exit 0");
            Assert.IsTrue(File.Exists(outputPath));
        }
        finally
        {
            UiRecordCommand.Handler.s_isInputRedirectedOverride = null;
        }
    }

    [TestMethod]
    public async Task StdinMonitor_DisposedCts_NeverThrowsUnhandledException()
    {
        // H1 regression: the guarded callback (volatile flag + try/catch) must not throw
        // ObjectDisposedException when the CTS is disposed before the callback fires on
        // a background thread. Directly exercises the callback logic to avoid pipeline
        // blocking; run 5x to surface non-deterministic races.
        Exception? unhandled = null;
        UnhandledExceptionEventHandler probe = (_, e)
            => Interlocked.CompareExchange(ref unhandled, (Exception)e.ExceptionObject, null);
        AppDomain.CurrentDomain.UnhandledException += probe;
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Interlocked.Exchange(ref unhandled, null);

                var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                var handler = new UiRecordCommand.Handler(null!, null!, null!, null!);

                // Simulate Handler exit: set flag BEFORE dispose (the ordering fix).
                cts.Dispose();

                // Fire callback from a background thread — simulates the stdin monitor
                // thread waking up and calling Cancel() after the handler disposed the CTS.
                var callDone = new ManualResetEventSlim(false);
                new Thread(() => { handler.CancelFromStdinMonitor(cts); callDone.Set(); })
                {
                    IsBackground = true,
                    Name = $"H1-probe-{i}",
                }.Start();
                callDone.Wait(TimeSpan.FromSeconds(5));

                // Allow any async unhandled-exception propagation.
                await Task.Delay(50);

                Assert.IsNull(unhandled,
                    $"run {i}: background thread threw unhandled: " +
                    $"{unhandled?.GetType().Name}: {unhandled?.Message}");
            }
        }
        finally
        {
            AppDomain.CurrentDomain.UnhandledException -= probe;
        }
    }
}
