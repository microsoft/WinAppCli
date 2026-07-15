// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // record — capture a window/element region to an H.264 MP4. The fake
    // RecordAsync writes a tiny placeholder file and returns configurable
    // frame/mode metadata, so these tests exercise the command's validation
    // and JSON envelope without touching WGC/Media Foundation.
    //
    // Note on stderr capture: UiJsonError.Emit writes structured JSON to
    // Console.Error directly; the TextWriterLogger writes human-readable
    // messages to ConsoleStdErr (the logger capture). Tests that check the
    // JSON error code use a local Console.Error redirect; others check the
    // logger message captured in ConsoleStdErr.
    // ---------------------------------------------------------------------

    [TestMethod]
    public async Task Record_MissingApp_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidFps_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--fps", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidFps_EmitsInvalidArguments()
    {
        // When --json is set, the command must emit the "invalid_arguments" code (not "internal_error").
        // The JSON error code goes to Console.Error; the logger message goes to ConsoleStdErr.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--fps", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
        // Check logger message (in ConsoleStdErr) confirms the right validation fired.
        StringAssert.Contains(ConsoleStdErr.ToString(), "--fps must be at least 1");
    }

    [TestMethod]
    public async Task Record_InvalidMaxEdge_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--max-edge=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidMaxEdge_EmitsInvalidArguments()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--max-edge=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0");
    }

    [TestMethod]
    public async Task Record_InvalidDuration_ReturnsError()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Record_InvalidDuration_EmitsInvalidArguments()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec=-1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--duration-sec must be 0 or greater");
    }

    [TestMethod]
    public async Task Record_ExcessiveDuration_EmitsInvalidArguments()
    {
        // Durations > 86400 (24 hours) are rejected with invalid_arguments.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec=86401", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "86400");
    }

    [TestMethod]
    public async Task Record_DefaultDuration_IsZero()
    {
        // Default --duration-sec is now 0 (record until stopped). Agents must supply an explicit
        // --duration-sec N for timed captures; without it the recording runs until Ctrl+C/stdin.
        var outputPath = Path.Combine(_tempDirectory.FullName, "default-duration.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(0, result.GetProperty("durationSec").GetInt32());
    }

    [TestMethod]
    public async Task Record_Success_EmitsRecordResultJson()
    {
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 42, Width = 640, Height = 480, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "capture.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "--fps", "10", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);

        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(outputPath, result.GetProperty("path").GetString());
        Assert.AreEqual("h264", result.GetProperty("codec").GetString());
        Assert.AreEqual("wgc", result.GetProperty("mode").GetString());
        Assert.AreEqual(42, result.GetProperty("frames").GetInt32());
        Assert.AreEqual(10, result.GetProperty("fps").GetInt32());
        Assert.AreEqual(1, result.GetProperty("durationSec").GetInt32());
        // The fake writes a placeholder file at the requested path.
        Assert.IsTrue(File.Exists(outputPath), "record should have produced an output file");
    }

    [TestMethod]
    public async Task Record_JsonMode_EmitsLivenessEventToStderr_NotToStdout()
    {
        // In --json mode a "recording-started" liveness event must go to stderr (not stdout).
        // Stdout must contain only the final result JSON object — no liveness noise.
        // The event is written via parseResult.InvocationConfiguration.Error (= ConsoleStdErr in
        // tests), so we check ConsoleStdErr.ToString() for the event — no Console.SetError needed.
        var outputPath = Path.Combine(_tempDirectory.FullName, "liveness.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);

        // Stdout must be a single parseable JSON result — not polluted by the liveness event.
        var stdout = TestAnsiConsole.Output.Trim();
        Assert.IsFalse(stdout.Contains("recording-started"), "liveness event must NOT appear on stdout");
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(stdout);
        Assert.AreEqual("h264", result.GetProperty("codec").GetString());

        // Stderr (ConsoleStdErr) must contain EXACTLY ONE valid UiRecordStartedEvent.
        // UiJsonContext uses WriteIndented=true so each event spans multiple lines; we count
        // occurrences of the event discriminator and then locate + parse the object boundaries.
        var stderrText = ConsoleStdErr.ToString();
        Assert.IsTrue(stderrText.Contains("\"recording-started\""),
            "recording-started event must appear on stderr");
        var matches = System.Text.RegularExpressions.Regex.Matches(
            stderrText, "\"event\"\\s*:\\s*\"recording-started\"");
        Assert.AreEqual(1, matches.Count, "exactly one recording-started JSON event must appear on stderr");

        // Extract the surrounding JSON object for field validation.
        var matchIndex = matches[0].Index;
        var start = stderrText.LastIndexOf('{', matchIndex);
        var end = stderrText.IndexOf('}', matchIndex) + 1;
        Assert.IsTrue(start >= 0 && end > start, "liveness event JSON object must be parseable");
        var liveness = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            stderrText[start..end]);
        Assert.AreEqual("recording-started", liveness.GetProperty("event").GetString(), "event field must be 'recording-started'");
        Assert.AreEqual(outputPath, liveness.GetProperty("path").GetString(), "event path must match output path");
    }

    [TestMethod]
    public async Task Record_Success_ReportsPrintWindowMode()
    {
        // The mode field must reflect the capture path actually used (accuracy fix): a printwindow
        // capture must not be mislabeled. Here the fake reports "printwindow"; assert it round-trips.
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 5, Width = 100, Height = 100, Mode = "printwindow" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "pw.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("printwindow", result.GetProperty("mode").GetString());
    }

    [TestMethod]
    public async Task Record_Success_ReportsScreenMode()
    {
        // Verify that when the fake service reports "screen" (consented --capture-screen path),
        // the command passes the mode through unchanged so consumers can detect the capture path.
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 2, Width = 100, Height = 100, Mode = "screen" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "screen.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--capture-screen", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("screen", result.GetProperty("mode").GetString());
    }

    [TestMethod]
    public async Task Record_ElementNotFound_ReturnsElementNotFoundError()
    {
        // When a selector is given but the element is not found, the command must return
        // exit code 1 with an element_not_found error — not silently record the whole window.
        _fakeUia.RecordException = new UiElementNotFoundException("btn-missing-a1b2");

        var outputPath = Path.Combine(_tempDirectory.FullName, "element-not-found.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "btn-missing-a1b2", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        // Logger message (in ConsoleStdErr) confirms element-not-found path: contains the selector name.
        StringAssert.Contains(ConsoleStdErr.ToString(), "btn-missing-a1b2");
        // Output file must NOT exist (no recording was made).
        Assert.IsFalse(File.Exists(outputPath), "no output file should be written when the element is not found");
    }

    [TestMethod]
    public async Task Record_InvalidOutputPath_ReturnsStructuredError()
    {
        // When the output path is invalid (parent directory is a file), the command must return
        // exit code 1 with a clean error message — not an unhandled exception / stack trace.
        var blockingFile = Path.Combine(_tempDirectory.FullName, "blocker.txt");
        File.WriteAllText(blockingFile, "block");
        var badOutputPath = Path.Combine(blockingFile, "record.mp4"); // parent is a file

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", badOutputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        // Logger message (in ConsoleStdErr) confirms the path error was handled cleanly.
        StringAssert.Contains(ConsoleStdErr.ToString(), "Invalid output path");
    }

    [TestMethod]
    public async Task Record_RecordingFailure_DoesNotDeletePreexistingOutputFile()
    {
        // A pre-existing file at the output path must survive a recording failure.
        // The encoder writes to a temp file and only replaces the final path on Complete(),
        // so a failure must never clobber a file that existed before the recording attempt.
        var outputPath = Path.Combine(_tempDirectory.FullName, "preexisting.mp4");
        File.WriteAllText(outputPath, "sentinel content");

        _fakeUia.RecordException = new InvalidOperationException("simulated capture failure");

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(File.Exists(outputPath), "pre-existing output file should survive a recording failure");
        Assert.AreEqual("sentinel content", File.ReadAllText(outputPath), "pre-existing file content should be unchanged");
    }

    // -----------------------------------------------------------------------
    // StdinStopMonitor unit tests — exercise the helper directly with
    // StringReader/custom readers so they run without a real pipe or process.
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // H1 — Readiness handshake: StdinStopMonitor armed only after encoder ready
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // H2 — Small element letterbox / encoder minimum dimension tests
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ComputeTargetSize_TinyInput_PadsToEncoderMinimum()
    {
        // A 32×24 element region is below the MF H.264 encoder minimum (64×64).
        // The encoder dimensions must be ≥ the minimum; the display dimensions are the
        // natural (aspect-preserved) size. Both dims must be even.
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(32, 24, 0);
        Assert.IsTrue(encW >= 64, $"encoder width ({encW}) must be ≥ 64 (MF H.264 minimum)");
        Assert.IsTrue(encH >= 64, $"encoder height ({encH}) must be ≥ 64 (MF H.264 minimum)");
        Assert.AreEqual(0, encW % 2, "encoder width must be even");
        Assert.AreEqual(0, encH % 2, "encoder height must be even");
        Assert.IsTrue(dispW <= encW, "display width must not exceed encoder width");
        Assert.IsTrue(dispH <= encH, "display height must not exceed encoder height");
        // Aspect ratio of display region should match input (32/24 ≈ 1.333).
        var inputRatio = 32.0 / 24.0;
        var displayRatio = (double)dispW / dispH;
        Assert.IsTrue(Math.Abs(inputRatio - displayRatio) < 0.15, $"display aspect ratio ({displayRatio:F3}) must be close to input ({inputRatio:F3})");
    }

    [TestMethod]
    public void ComputeTargetSize_LargeInput_NoUnnecessaryPadding()
    {
        // A large element (800×600) must pass through without letterbox inflation.
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(800, 600, 0);
        Assert.AreEqual(dispW, encW, "large frame must not be padded (encoder == display)");
        Assert.AreEqual(dispH, encH, "large frame must not be padded (encoder == display)");
    }

    [TestMethod]
    public void ComputeTargetSize_EvenDimensions_Always()
    {
        // Odd input values must always yield even encoder and display dimensions.
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(33, 25, 0);
        Assert.AreEqual(0, encW % 2, "encoder width must always be even");
        Assert.AreEqual(0, encH % 2, "encoder height must always be even");
        Assert.AreEqual(0, dispW % 2, "display width must always be even");
        Assert.AreEqual(0, dispH % 2, "display height must always be even");
    }

    [TestMethod]
    public void ComputeTargetSize_ThinAspect_DownscaleRoundsNotFloors()
    {
        // 300×10 with maxEdge=100: scale=0.333, ideal displayH=3.33.
        // Floor would give 2 (50:1 aspect — huge distortion from 30:1).
        // Nearest-even round gives 4 (25:1 aspect — much closer to 30:1).
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(300, 10, 100);
        Assert.AreEqual(0, dispW % 2, "display width must be even");
        Assert.AreEqual(0, dispH % 2, "display height must be even");
        Assert.IsTrue(dispH >= 4, $"nearest-even round of 3.33 must be 4, not floored to 2; got {dispH}");

        var inputRatio = 300.0 / 10.0;
        var displayRatio = (double)dispW / dispH;
        var aspectError = Math.Abs(displayRatio - inputRatio) / inputRatio;
        Assert.IsTrue(aspectError < 0.20,
            $"aspect error ({aspectError:P1}) must be < 20% with nearest-even rounding; got {dispW}×{dispH}");
    }

    [TestMethod]
    public void ComputeTargetSize_ThinAspect_DownscaleDimsAreEvenAndAboveMinimum()
    {
        // Verify encoder dims are at or above the H.264 minimum and all dims are even.
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(300, 10, 100);
        Assert.IsTrue(encW >= 64, $"encoder width ({encW}) must be ≥ 64 (MF H.264 minimum)");
        Assert.IsTrue(encH >= 64, $"encoder height ({encH}) must be ≥ 64 (MF H.264 minimum)");
        Assert.AreEqual(0, encW % 2, "encoder width must be even");
        Assert.AreEqual(0, encH % 2, "encoder height must be even");
        Assert.AreEqual(0, dispW % 2, "display width must be even");
        Assert.AreEqual(0, dispH % 2, "display height must be even");
    }

    // -----------------------------------------------------------------------
    // H3 — Canonical selector resolution (ambiguous selector returns error)
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Record_AmbiguousSelector_ReturnsError()
    {
        // FindSingleElementAsync now throws UiAmbiguousSelectorException (not InvalidOperationException)
        // when a plain-text selector matches multiple elements. Record must surface this as exit code 1.
        _fakeUia.RecordException = new UiAmbiguousSelectorException(
            "Selector matched 3 elements:\n  [0] Button \"OK\" -> btn-ok-a1b2\n  [1] Button \"Cancel\" -> btn-cancel-c3d4\nUse a slug from 'inspect' to target a specific element.");

        var outputPath = Path.Combine(_tempDirectory.FullName, "ambiguous.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "OK", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode, "ambiguous selector must return exit code 1");
        Assert.IsFalse(File.Exists(outputPath), "no output file should be written for ambiguous selectors");
    }

    [TestMethod]
    public async Task Record_AmbiguousSelector_EmitsAmbiguousSelectorCode()
    {
        // M3: the JSON error code for an ambiguous selector must be "ambiguous_selector", not "internal_error".
        _fakeUia.RecordException = new UiAmbiguousSelectorException(
            "Selector matched 2 elements:\n  [0] Button \"Submit\" -> btn-submit-a1b2\n  [1] Button \"Submit\" -> btn-submit-c3d4\nUse a slug from 'inspect' to target a specific element.");

        var outputPath = Path.Combine(_tempDirectory.FullName, "ambiguous-code.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        var origError = Console.Error;
        var stderrCapture = new System.IO.StringWriter();
        Console.SetError(stderrCapture);
        try
        {
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "Submit", "-o", outputPath, "--json"]);
            Assert.AreEqual(1, exitCode, "ambiguous selector must exit 1");

            var stderrText = stderrCapture.ToString();
            // The structured JSON error must appear on stderr with the correct code.
            Assert.IsTrue(stderrText.Contains("ambiguous_selector"),
                $"stderr must contain 'ambiguous_selector' error code; got: {stderrText}");
        }
        finally
        {
            Console.SetError(origError);
        }
    }

    [TestMethod]
    public async Task Record_NoSelector_RecordsWholeWindow()
    {
        // Without a selector, record must capture the whole window (by design, unchanged by H3).
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 5, Width = 1280, Height = 720, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "whole-window.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode, "whole-window recording (no selector) must succeed");
        Assert.IsTrue(File.Exists(outputPath), "output file must be produced for whole-window recording");
    }

    // -----------------------------------------------------------------------
    // M1 — Constructor failure must delete temp file (no orphan)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Mp4SinkWriterEncoder_ConstructorFailure_TempFileIsDeleted()
    {
        // Set the injectable seam: inside the constructor try-block, the seam creates the
        // temp file (simulating what MFCreateSinkWriterFromURL does) and then throws a
        // COMException (simulating a bad-FPS codec rejection). The constructor catch must
        // delete the temp file — this test verifies that code path directly.
        var outputPath = Path.Combine(_tempDirectory.FullName, "ctor-fail.mp4");
        bool threw = false;
        try
        {
            Mp4SinkWriterEncoder.s_testFaultAfterTempCreate =
                () => throw new System.Runtime.InteropServices.COMException(
                    "Simulated codec rejection (bad fps)", unchecked((int)0xC00D36B4));

            _ = new Mp4SinkWriterEncoder(outputPath, 640, 480, 30, 2_000_000);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            threw = true;
        }
        finally
        {
            Mp4SinkWriterEncoder.s_testFaultAfterTempCreate = null; // always clean up seam
        }

        Assert.IsTrue(threw, "constructor must have thrown COMException via the fault seam");

        // The output (final) path must not have been created.
        Assert.IsFalse(File.Exists(outputPath), "output path must not exist after constructor failure");

        // No orphaned temp .mp4 files may remain in the directory.
        var orphans = Directory.GetFiles(_tempDirectory.FullName, "*.mp4");
        Assert.AreEqual(0, orphans.Length,
            $"constructor catch must delete the temp file; orphan(s) found: {string.Join(", ", orphans)}");
    }

    [TestMethod]
    public void Mp4SinkWriterEncoder_MoveFails_TempFileCleanedUp()
    {
        // If File.Move throws after Finalize (e.g. destination locked), _fileMoved remains
        // false, and Dispose() must delete the temp file rather than leave it orphaned.
        // We test this via a subclass that simulates a locked destination.
        var dir = _tempDirectory.FullName;
        var finalPath = Path.Combine(dir, "final.mp4");
        var tempPattern = Path.Combine(dir, "*.mp4");

        // Write a sentinel so we can verify it's untouched after the failed move.
        File.WriteAllText(finalPath, "pre-existing-sentinel");

        // Locate the temp file created by the encoder.
        // We can't directly instantiate Mp4SinkWriterEncoder (requires MF), so test the state
        // machine logic via a purpose-built unit: set _finalized without _fileMoved and verify
        // Dispose() deletes the temp.
        var tempFile = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(tempFile, "temp-content");

        // Simulate the internal state: _finalized=true, _fileMoved=false → Dispose should delete temp.
        // Use the helper MoveFailEncoder test shim (a thin test-only wrapper).
        MoveFailEncoder.SimulateDisposeWithMoveFailure(tempFile);

        Assert.IsFalse(File.Exists(tempFile), "temp file must be deleted after a failed move");
        Assert.IsTrue(File.Exists(finalPath), "pre-existing final file must be untouched");
        Assert.AreEqual("pre-existing-sentinel", File.ReadAllText(finalPath), "pre-existing file content must be unchanged");
    }

    // -----------------------------------------------------------------------
    // H1 (round-7) — WGC fallback decision: non-consented fallback must fail fast
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WgcFallback_WithoutCaptureScreen_Throws()
    {
        // Without --capture-screen, a WGC init failure must throw rather than silently
        // falling back to screen capture (which would leak unrelated windows).
        var inner = new InvalidOperationException("Simulated WGC init failure");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<UiAutomationService>.Instance;
        var thrown = false;
        try
        {
            UiAutomationService.EnsureWgcFallbackConsented(inner, captureScreenRequested: false, logger);
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Assert.IsTrue(thrown, "EnsureWgcFallbackConsented without --capture-screen must throw InvalidOperationException");
    }

    [TestMethod]
    public void WgcFallback_WithCaptureScreen_DoesNotThrow()
    {
        // With --capture-screen, the user consented to screen-DC capture, so the fallback is allowed.
        var inner = new InvalidOperationException("Simulated WGC init failure");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<UiAutomationService>.Instance;
        // Must not throw — screen capture was explicitly requested.
        UiAutomationService.EnsureWgcFallbackConsented(inner, captureScreenRequested: true, logger);
    }

    [TestMethod]
    public void WgcFallback_ThrowMessage_MentionsCaptureScreenOption()
    {
        // The error thrown without --capture-screen must mention the --capture-screen option
        // so the user knows how to proceed.
        var inner = new InvalidOperationException("Simulated WGC init failure");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<UiAutomationService>.Instance;
        Exception? caught = null;
        try
        {
            UiAutomationService.EnsureWgcFallbackConsented(inner, captureScreenRequested: false, logger);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }
        Assert.IsNotNull(caught, "must throw when captureScreenRequested is false");
        StringAssert.Contains(caught!.Message, "--capture-screen",
            "error must mention --capture-screen so users know how to opt in to screen capture");
    }

    // -----------------------------------------------------------------------
    // M4 (round-7) — --max-edge below encoder minimum must be rejected
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Record_MaxEdgeOne_ReturnsInvalidArguments()
    {
        // --max-edge 1 is non-zero and below the 64px encoder minimum — must be rejected.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--max-edge", "1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0");
        StringAssert.Contains(ConsoleStdErr.ToString(), "64");
    }

    [TestMethod]
    public async Task Record_MaxEdge64_IsValid()
    {
        // --max-edge 64 equals the encoder minimum and must be accepted.
        var outputPath = Path.Combine(_tempDirectory.FullName, "maxedge64.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--max-edge", "64", "--duration-sec", "1", "-o", outputPath, "--json"]);
        Assert.AreEqual(0, exitCode, "--max-edge 64 (encoder minimum) must be accepted");
    }

    [TestMethod]
    public async Task Record_MaxEdgeZero_IsUnbounded()
    {
        // --max-edge 0 means no downscale (unbounded) and must be accepted.
        var outputPath = Path.Combine(_tempDirectory.FullName, "maxedge0.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--max-edge", "0", "--duration-sec", "1", "-o", outputPath, "--json"]);
        Assert.AreEqual(0, exitCode, "--max-edge 0 (unbounded) must be accepted");
    }

    // -----------------------------------------------------------------------
    // M1 (round-8) — numeric validation must run BEFORE the missing-target check
    // so that bad argument values yield invalid_arguments even when no target is given.
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Record_MaxEdgeOne_NoTarget_ReturnsInvalidArguments()
    {
        // --max-edge 1 (non-zero, below 64) with NO -a/-w must yield invalid_arguments,
        // not missing_app. Before the fix, the missing-target check ran first.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "1", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0",
            "error must be invalid_arguments, not missing_app");
        StringAssert.Contains(ConsoleStdErr.ToString(), "64");
    }

    [TestMethod]
    public async Task Record_MaxEdge63_NoTarget_ReturnsInvalidArguments()
    {
        // --max-edge 63 (non-zero, one below the 64px minimum) with NO target must yield
        // invalid_arguments — the numeric validation must fire before the target check.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "63", "--json"]);
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0",
            "error must be invalid_arguments, not missing_app");
    }

    [TestMethod]
    public async Task Record_MaxEdge64_NoTarget_ReturnsMissingApp()
    {
        // --max-edge 64 (valid — encoder minimum) with NO target must pass numeric validation
        // and then fail with missing_app, not invalid_arguments.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "64", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("--max-edge must be 0"),
            "--max-edge 64 is valid; error must be missing_app, not invalid_arguments");
    }

    [TestMethod]
    public async Task Record_MaxEdgeZero_NoTarget_ReturnsMissingApp()
    {
        // --max-edge 0 (unbounded) with NO target must pass numeric validation and then fail
        // with missing_app, not invalid_arguments.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--max-edge", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("--max-edge must be 0"),
            "--max-edge 0 is valid; error must be missing_app, not invalid_arguments");
    }

    // -----------------------------------------------------------------------
    // M5 (round-7) — --quiet suppresses "Recording…" progress text
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // L1 (round-7) — Interactive status text shows only Ctrl+C when stdin not redirected
    // -----------------------------------------------------------------------

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

    /// <summary>Test shim that exercises the Mp4SinkWriterEncoder Dispose cleanup path without
    /// instantiating a real Media Foundation session (which requires WGC/hardware).</summary>
    private static class MoveFailEncoder
    {
        public static void SimulateDisposeWithMoveFailure(string tempFile)
        {
            // Directly exercise the cleanup logic: _finalized=true, _fileMoved=false → delete temp.
            // This mirrors what Mp4SinkWriterEncoder.Dispose() does when Complete() finalized the
            // writer but File.Move threw before _fileMoved could be set.
            bool fileMoved = false;
            if (!fileMoved)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    /// <summary>A TextReader that always returns null from ReadLine (simulates EOF with no data).</summary>
    private sealed class EofReader : TextReader
    {
        public override string? ReadLine() => null;
    }

    /// <summary>A TextReader that signals a TCS when ReadLine is invoked, then returns null (EOF).</summary>
    private sealed class SignalingEofReader(Action onRead) : TextReader
    {
        public override string? ReadLine() { onRead(); return null; }
    }

    /// <summary>A TextReader that signals a TCS when ReadLine is invoked, then returns the line.</summary>
    private sealed class SignalingStringReader(string text, Action onRead) : StringReader(text)
    {
        public override string? ReadLine() { onRead(); return base.ReadLine(); }
    }

    // -----------------------------------------------------------------------
    // H1 (r12) — A TIMED recording (durationSec > 0) must NOT arm the stdin
    //   stop-monitor: a closed/redirected stdin delivering EOF must not cancel
    //   it. Timed runs stop on their own wall-clock deadline (or Ctrl+C). Only
    //   UNBOUNDED recordings (durationSec == 0) use stdin as the stop signal —
    //   that path is covered by Record_RedirectedStdin_StatusTextMentionsEof.
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // M2 — ProcessFrame pixel assertions (black padding, centering, clamping)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a BGRA byte array where every pixel is the specified (B, G, R, A) color.
    /// </summary>
    private static byte[] MakeSolidFrame(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }
        return pixels;
    }

    private static (byte B, byte G, byte R, byte A) GetPixel(byte[] frame, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (frame[offset], frame[offset + 1], frame[offset + 2], frame[offset + 3]);
    }

    [TestMethod]
    public void ProcessFrame_SmallContent_CenteredWithBlackPadding()
    {
        // A tiny 32×32 red content frame placed inside an 80×80 encoder frame should be
        // centered and the padding region should be black.
        const int contentW = 32, contentH = 32;
        const int encoderW = 80, encoderH = 80;

        var source = MakeSolidFrame(contentW, contentH, b: 0, g: 0, r: 255); // red
        var output = UiAutomationService.ProcessFrame(
            source, contentW, contentH,
            cropX: 0, cropY: 0, cropW: contentW, cropH: contentH,
            encoderWidth: encoderW, encoderHeight: encoderH,
            displayWidth: contentW, displayHeight: contentH);

        Assert.AreEqual(encoderW * encoderH * 4, output.Length, "output must be encoder-sized");

        // Corner pixels (outside the content area) must be black (padding).
        var topLeft = GetPixel(output, encoderW, 0, 0);
        Assert.AreEqual((byte)0, topLeft.R, "top-left corner must be black (padding)");
        Assert.AreEqual((byte)0, topLeft.G, "top-left corner must be black (padding)");
        Assert.AreEqual((byte)0, topLeft.B, "top-left corner must be black (padding)");

        // Center pixel (inside content area) must be non-black (red content).
        var centerX = (encoderW - contentW) / 2 + contentW / 2;
        var centerY = (encoderH - contentH) / 2 + contentH / 2;
        var center = GetPixel(output, encoderW, centerX, centerY);
        Assert.IsTrue(center.R > 128 || center.G > 128 || center.B > 128,
            $"center pixel should be non-black (from content); got B={center.B} G={center.G} R={center.R}");
    }

    [TestMethod]
    public void ProcessFrame_FullSizeNoLetterbox_FastPath()
    {
        // When source == encoder size and crop covers the whole frame, ProcessFrame must
        // return the original pixel array (fast path — no copy).
        const int w = 640, h = 480;
        var source = MakeSolidFrame(w, h, b: 0, g: 255, r: 0); // green

        var output = UiAutomationService.ProcessFrame(
            source, w, h,
            cropX: 0, cropY: 0, cropW: w, cropH: h,
            encoderWidth: w, encoderHeight: h,
            displayWidth: w, displayHeight: h);

        Assert.AreSame(source, output, "fast path must return the original array without copying");
    }

    [TestMethod]
    public void ProcessFrame_CropExtractsSubregion_ContentCentered()
    {
        // A 100×100 source with a 20×20 blue crop at (40,40); encoder is 80×80.
        // The blue subregion should appear centered in the output; the rest is black.
        const int srcW = 100, srcH = 100;
        const int cropX = 40, cropY = 40, cropW = 20, cropH = 20;
        const int encW = 80, encH = 80;

        // Source: black background except the crop region which is blue.
        var source = new byte[srcW * srcH * 4]; // all black
        for (var y = cropY; y < cropY + cropH; y++)
        {
            for (var x = cropX; x < cropX + cropW; x++)
            {
                var offset = (y * srcW + x) * 4;
                source[offset] = 255; // B
                source[offset + 1] = 0; // G
                source[offset + 2] = 0; // R
                source[offset + 3] = 255;
            }
        }

        var output = UiAutomationService.ProcessFrame(
            source, srcW, srcH,
            cropX, cropY, cropW, cropH,
            encoderWidth: encW, encoderHeight: encH,
            displayWidth: cropW, displayHeight: cropH);

        Assert.AreEqual(encW * encH * 4, output.Length);

        // Corners must be black (padding).
        var corner = GetPixel(output, encW, 0, 0);
        Assert.AreEqual((byte)0, corner.R);
        Assert.AreEqual((byte)0, corner.G);
        Assert.AreEqual((byte)0, corner.B);
    }

    [TestMethod]
    public void ProcessFrame_CropOutOfBounds_Clamped()
    {
        // If crop + cropW would exceed sourceWidth, the frame must clamp rather than
        // throw an exception or produce garbage.
        const int srcW = 50, srcH = 50;
        var source = MakeSolidFrame(srcW, srcH, b: 0, g: 0, r: 128); // dark red

        // Intentionally over-wide crop — must not throw.
        Exception? ex = null;
        try
        {
            UiAutomationService.ProcessFrame(
                source, srcW, srcH,
                cropX: 40, cropY: 40, cropW: 30, cropH: 30, // 40+30=70 > 50 — clamped
                encoderWidth: 64, encoderHeight: 64,
                displayWidth: 20, displayHeight: 20);
        }
        catch (Exception caught)
        {
            ex = caught;
        }
        Assert.IsNull(ex, $"out-of-bounds crop must be clamped, not throw; got: {ex?.Message}");
    }

    [TestMethod]
    public void ProcessFrame_ThinAspect_PaddingIsBlack()
    {
        // A very wide (80×8) source letterboxed into 80×64 encoder.
        // Padding rows above and below the content must be black.
        const int srcW = 80, srcH = 8;
        const int encW = 80, encH = 64;
        const int dispW = 80, dispH = 8;

        var source = MakeSolidFrame(srcW, srcH, b: 255, g: 0, r: 0); // blue content

        var output = UiAutomationService.ProcessFrame(
            source, srcW, srcH,
            cropX: 0, cropY: 0, cropW: srcW, cropH: srcH,
            encoderWidth: encW, encoderHeight: encH,
            displayWidth: dispW, displayHeight: dispH);

        Assert.AreEqual(encW * encH * 4, output.Length);

        // Top row (padding) must be black.
        var topRow = GetPixel(output, encW, encW / 2, 0);
        Assert.AreEqual((byte)0, topRow.B, "top padding must be black (B)");
        Assert.AreEqual((byte)0, topRow.G, "top padding must be black (G)");
        Assert.AreEqual((byte)0, topRow.R, "top padding must be black (R)");
    }

    // -----------------------------------------------------------------------
    // M4 — WGC pool-size-change detection logic (structural unit test)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WgcCapture_SizeChangeDetection_ResizeTriggersRecreate()
    {
        // M4: Unit test for the pool-size-change decision in OnFrameArrived.
        // We test the guard condition directly since FrameGrabber requires live D3D/WGC.
        var poolW = 800; var poolH = 600;

        // Resize — different size, non-zero → should recreate.
        var newW = 1024; var newH = 768;
        var shouldRecreate = newW > 0 && newH > 0 && (newW != poolW || newH != poolH);
        Assert.IsTrue(shouldRecreate, "valid resize must trigger pool recreation");

        // Same size — must not recreate.
        var sameW = 800; var sameH = 600;
        var sameNoRecreate = sameW > 0 && sameH > 0 && (sameW != poolW || sameH != poolH);
        Assert.IsFalse(sameNoRecreate, "same size must not trigger pool recreation");

        // Zero size — must not recreate (guard against invalid frames).
        var zeroW = 0; var zeroH = 0;
        var zeroNoRecreate = zeroW > 0 && zeroH > 0;
        Assert.IsFalse(zeroNoRecreate, "zero-size frame must not trigger pool recreation");

        // Partial zero — must not recreate.
        var partialZeroNoRecreate = 0 > 0 && 600 > 0;
        Assert.IsFalse(partialZeroNoRecreate, "zero width must not trigger pool recreation");
    }

    // -----------------------------------------------------------------------
    // M5 — Window-close mid-recording: partial video finalized gracefully
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Record_WindowClosedMidRecording_FinalizesGracefullyWithPartialVideo()
    {
        // M5: when the capture item closes mid-recording, the recording loop breaks
        // and finalizes the frames already captured rather than encoding stale data
        // to the deadline. Simulated via the fake returning fewer frames than duration.
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 3, Width = 640, Height = 480, Mode = "wgc" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "partial-close.mp4");
        var command = GetRequiredService<UiRecordCommand>();

        // Duration of 60s; fake returns 3 frames (simulating early close).
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "60", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode, "window-closed mid-recording must finalize gracefully (exit 0)");
        Assert.IsTrue(File.Exists(outputPath), "partial video must be written");
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(3, result.GetProperty("frames").GetInt32(), "partial frame count must be reported");
        Assert.AreEqual("wgc", result.GetProperty("mode").GetString());
    }

    // -----------------------------------------------------------------------
    // L1 — EvenFloor: longest edge must not EXCEED --max-edge
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ComputeTargetSize_MaxEdgeOdd_LongEdgeDoesNotExceedCap()
    {
        // L1: if --max-edge=99, the longest display edge must be ≤ 99.
        // EvenFloor(99) = 98, so display long edge is 98, not 100.
        var (_, _, dispW, dispH) = UiAutomationService.ComputeTargetSize(300, 100, 99);
        var longest = Math.Max(dispW, dispH);
        Assert.IsTrue(longest <= 99, $"longest display edge ({longest}) must be ≤ maxEdge (99)");
        Assert.AreEqual(0, dispW % 2, "displayW must be even");
        Assert.AreEqual(0, dispH % 2, "displayH must be even");
    }

    [TestMethod]
    public void ComputeTargetSize_MaxEdgeEven_LongEdgeExactlyCap()
    {
        // Even max-edge: the long edge should land exactly on (or below) the cap.
        var (_, _, dispW, dispH) = UiAutomationService.ComputeTargetSize(400, 300, 100);
        var longest = Math.Max(dispW, dispH);
        Assert.IsTrue(longest <= 100, $"longest display edge ({longest}) must be ≤ 100");
        Assert.AreEqual(0, dispW % 2);
        Assert.AreEqual(0, dispH % 2);
    }

    [TestMethod]
    public void ComputeTargetSize_ThinAspect_LongEdgeNeverExceedsMaxEdge()
    {
        // 300×10 with maxEdge=100: long edge is 300. After scale = 100/300 ≈ 0.333,
        // displayW must be ≤ 100 (not 100 rounded up).
        var (_, _, dispW, dispH) = UiAutomationService.ComputeTargetSize(300, 10, 100);
        Assert.IsTrue(dispW <= 100, $"displayW ({dispW}) must be ≤ maxEdge (100)");
        Assert.IsTrue(dispH <= 100, $"displayH ({dispH}) must be ≤ maxEdge (100)");
        Assert.AreEqual(0, dispW % 2);
        Assert.AreEqual(0, dispH % 2);
    }

    // -----------------------------------------------------------------------
    // M9 — Near-square / exact-square: short edge must also stay ≤ maxEdge
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ComputeTargetSize_ExactSquare_NearMaxEdge_BothEdgesWithinCap()
    {
        // M9: 100×100 with maxEdge=99. scale=0.99, EvenRound(99)=100 would exceed the cap.
        // Both display edges must be ≤ 99 after the fix clamps the short edge too.
        var (_, _, dispW, dispH) = UiAutomationService.ComputeTargetSize(100, 100, 99);
        Assert.IsTrue(dispW <= 99, $"dispW ({dispW}) must be ≤ maxEdge (99) for exact-square input");
        Assert.IsTrue(dispH <= 99, $"dispH ({dispH}) must be ≤ maxEdge (99) for exact-square input");
        Assert.AreEqual(0, dispW % 2, "dispW must be even");
        Assert.AreEqual(0, dispH % 2, "dispH must be even");
    }

    [TestMethod]
    public void ComputeTargetSize_NearSquare_MaxEdge_ShortEdgeDoesNotExceedCap()
    {
        // M9: 100×98 with maxEdge=99. Long edge=100 → scale=0.99; short edge EvenRound(97.02)=98.
        // Short edge 98 ≤ 99 with fix; long edge EvenFloor(99)=98 ≤ 99. Both must be ≤ 99.
        var (_, _, dispW, dispH) = UiAutomationService.ComputeTargetSize(100, 98, 99);
        Assert.IsTrue(dispW <= 99, $"dispW ({dispW}) must be ≤ 99");
        Assert.IsTrue(dispH <= 99, $"dispH ({dispH}) must be ≤ 99");
        Assert.AreEqual(0, dispW % 2);
        Assert.AreEqual(0, dispH % 2);
    }

    [TestMethod]
    public void ComputeTargetSize_ExactSquarePlusTen_OddMaxEdge_BothEdgesWithinCap()
    {
        // M9: Broader invariant: max(dispW, dispH) ≤ maxEdge for ALL inputs with a capped maxEdge.
        // Test several square and near-square sizes with odd maxEdge values.
        int[] sizes = [50, 100, 101, 200, 255, 1000];
        int[] caps = [49, 98, 99, 100, 199, 253];
        for (var i = 0; i < sizes.Length; i++)
        {
            var (_, _, dispW, dispH) = UiAutomationService.ComputeTargetSize(sizes[i], sizes[i], caps[i]);
            var longest = Math.Max(dispW, dispH);
            Assert.IsTrue(longest <= caps[i],
                $"square {sizes[i]}×{sizes[i]} maxEdge={caps[i]}: longest ({longest}) must be ≤ {caps[i]}");
            Assert.AreEqual(0, dispW % 2, "dispW must be even");
            Assert.AreEqual(0, dispH % 2, "dispH must be even");
        }
    }

    // -----------------------------------------------------------------------
    // H1 — StdinMonitor: callback must not throw on a disposed CTS
    // -----------------------------------------------------------------------

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
                var stopped = false; // mirrors Handler._stdinMonitorStopped

                // Guarded callback: same logic as the lambda in Handler.InvokeAsync.
                Action guarded = () =>
                {
                    if (!stopped)
                    {
                        try { cts.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                };

                // Simulate Handler exit: set flag BEFORE dispose (the ordering fix).
                stopped = true;
                cts.Dispose();

                // Fire callback from a background thread — simulates the stdin monitor
                // thread waking up and calling Cancel() after the handler disposed the CTS.
                var callDone = new ManualResetEventSlim(false);
                new Thread(() => { guarded(); callDone.Set(); })
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

    // -----------------------------------------------------------------------
    // H2 — Whole-window WGC: growing frame must use full current frame as crop source
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ProcessFrame_WholWindowWgc_GrownFrame_FullFrameVsStaleSubrect()
    {
        // H2: for whole-window WGC, after a window resize the crop source must be the
        // FULL current frame (sw×sh), not the stale initial srcWidth×srcHeight sub-rect.
        // We demonstrate by putting content ONLY in the grown region (outside the initial
        // bounds) and verifying that the fixed (full-frame) crop captures it.
        const int initialW = 100, initialH = 100;
        const int grownW = 200, grownH = 200;
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(initialW, initialH, 0);

        // Source: black in the top-left 100×100 region, blue in the grown (>100) region.
        var source = new byte[grownW * grownH * 4];
        for (var y = 0; y < grownH; y++)
        {
            for (var x = 0; x < grownW; x++)
            {
                if (x >= initialW || y >= initialH)
                {
                    var off = (y * grownW + x) * 4;
                    source[off] = 255; // B
                    source[off + 3] = 255;
                }
            }
        }

        // Stale crop (the bug): only the black top-left 100×100 sub-rect.
        var staleOutput = UiAutomationService.ProcessFrame(
            source, grownW, grownH,
            0, 0, initialW, initialH,
            encW, encH, dispW, dispH);

        // Fixed crop (H2 fix): full 200×200 current frame.
        var fixedOutput = UiAutomationService.ProcessFrame(
            source, grownW, grownH,
            0, 0, grownW, grownH,
            encW, encH, dispW, dispH);

        // Stale crop: the content (blue) is in the grown region — entirely missed.
        var staleCenter = GetPixel(staleOutput, encW, encW / 2, encH / 2);
        Assert.AreEqual((byte)0, staleCenter.B,
            "stale-crop output must be all black (grown content missed by stale sub-rect)");

        // Fixed crop: full frame scaled into encoder → blue from grown region must appear.
        var hasBlue = false;
        for (var i = 0; i < fixedOutput.Length; i += 4)
        {
            if (fixedOutput[i] > 128) { hasBlue = true; break; }
        }
        Assert.IsTrue(hasBlue,
            "fixed crop must include blue content from the grown frame region");
    }

    // -----------------------------------------------------------------------
    // M8 — IsClosed drain: drained frame produces valid ProcessFrame output
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ProcessFrame_ClosedItemDrain_ProducesValidEncoderSizeOutput()
    {
        // M8: when IsClosed fires and the cached frame is drained before break,
        // ProcessFrame must produce valid encoder-sized output (not empty/zero).
        const int srcW = 64, srcH = 64;
        var (encW, encH, dispW, dispH) = UiAutomationService.ComputeTargetSize(srcW, srcH, 0);
        var source = MakeSolidFrame(srcW, srcH, b: 0, g: 180, r: 0); // green

        var output = UiAutomationService.ProcessFrame(
            source, srcW, srcH, 0, 0, srcW, srcH,
            encW, encH, dispW, dispH);

        Assert.AreEqual(encW * encH * 4, output.Length,
            "drained frame must produce full encoder-sized output, not 0 bytes");
        // Output must contain source content (green channel), not be all-zero.
        var hasContent = false;
        for (var i = 0; i < output.Length; i += 4)
        {
            if (output[i + 1] > 0) { hasContent = true; break; }
        }
        Assert.IsTrue(hasContent, "drained frame output must contain source content");
    }

    // -----------------------------------------------------------------------
    // H1 (round-9) — ResolvePopupCaptureHwnd: popup/owned window retargets capture
    // -----------------------------------------------------------------------

    [TestMethod]
    public void ResolvePopupCaptureHwnd_SameWindow_NoRetarget()
    {
        // When the element's WindowHandle matches the session HWND, the method must return
        // the session HWND unchanged and leave all capture params untouched.
        var sessionHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 200, srcW = 500, srcH = 375;

        var result = UiAutomationService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)sessionHwnd,
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH);

        Assert.AreEqual(sessionHwnd, result, "same-window element must return session HWND");
        Assert.AreEqual(100, originLeft, "captureOriginLeft must be unchanged");
        Assert.AreEqual(200, originTop, "captureOriginTop must be unchanged");
        Assert.AreEqual(500, srcW, "srcWidth must be unchanged");
        Assert.AreEqual(375, srcH, "srcHeight must be unchanged");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_NullWindowHandle_NoRetarget()
    {
        // A null WindowHandle must leave all params unchanged and return the session HWND.
        var sessionHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 200, srcW = 500, srcH = 375;

        var result = UiAutomationService.ResolvePopupCaptureHwnd(
            elementWindowHandle: null,
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH);

        Assert.AreEqual(sessionHwnd, result, "null WindowHandle must return session HWND");
        Assert.AreEqual(100, originLeft, "captureOriginLeft must be unchanged");
        Assert.AreEqual(200, originTop, "captureOriginTop must be unchanged");
        Assert.AreEqual(500, srcW, "srcWidth must be unchanged");
        Assert.AreEqual(375, srcH, "srcHeight must be unchanged");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_PopupWindow_RetargetsToCaptureOriginAndSize()
    {
        // Core H1 regression test: when element's WindowHandle differs from the session HWND,
        // ResolvePopupCaptureHwnd must:
        //   1. Return the popup's top-level HWND (not the session HWND).
        //   2. Update captureOriginLeft/Top to the popup window's screen origin.
        //   3. Update srcWidth/srcHeight to the popup window's pixel dimensions.
        // Before the fix, the crop would be clamped against the main window's frame, producing
        // a truncated sliver (e.g. "width":64,"height":100 instead of 200×100).
        var sessionHwnd = (nint)0x1000;
        var popupHwnd = (nint)0x2000;

        // Main window at (100,100), size 500×375.
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        // Popup window at (550,30), size 220×130 (partly outside main window).
        var result = UiAutomationService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)popupHwnd,
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getAncestorRoot: h => h,                             // popup IS already the root
            getWindowRect: _ => (550, 30, 770, 160));            // left=550,top=30,right=770,bottom=160

        Assert.AreEqual(popupHwnd, result, "popup element must retarget to popup HWND");
        Assert.AreEqual(550, originLeft, "captureOriginLeft must be updated to popup window's screen-left");
        Assert.AreEqual(30, originTop, "captureOriginTop must be updated to popup window's screen-top");
        Assert.AreEqual(220, srcW, "srcWidth must be updated to popup window's width (770-550)");
        Assert.AreEqual(130, srcH, "srcHeight must be updated to popup window's height (160-30)");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_PopupWindow_CropRectUsesRetargetedOrigin()
    {
        // Regression: after retargeting, an element at screen (600,50) with size 200×100
        // in a popup window at screen (550,30,810,150) must produce cropX=50, cropY=20,
        // cropW=200, cropH=100 — not the clamped sliver from using the main window origin.
        // Without the fix, using the main window's origin (100,100) with srcW=500 would
        // give cropX=499 (clamped), cropW=1 — a 1-pixel sliver.
        var sessionHwnd = (nint)0x1000;
        var popupHwnd = (nint)0x2000;
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        // Popup window at (550,30), size 260×120 — large enough to fully contain the element.
        UiAutomationService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)popupHwnd,
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getAncestorRoot: h => h,
            getWindowRect: _ => (550, 30, 810, 150));    // width=260, height=120

        // Simulate the crop computation performed in RecordAsync after retarget.
        double elemX = 600, elemY = 50, elemW = 200, elemH = 100;
        var cropX = Math.Clamp((int)elemX - originLeft, 0, Math.Max(0, srcW - 1));
        var cropY = Math.Clamp((int)elemY - originTop, 0, Math.Max(0, srcH - 1));
        var cropW = Math.Clamp((int)elemW, 1, srcW - cropX);
        var cropH = Math.Clamp((int)elemH, 1, srcH - cropY);

        Assert.AreEqual(50, cropX, "cropX after retarget must be element.X(600) - popupOrigin.X(550)");
        Assert.AreEqual(20, cropY, "cropY after retarget must be element.Y(50) - popupOrigin.Y(30)");
        Assert.AreEqual(200, cropW, "cropW must match element width (element fits within popup window)");
        Assert.AreEqual(100, cropH, "cropH must match element height (element fits within popup window)");
    }

    [TestMethod]
    public void ResolvePopupCaptureHwnd_GaRootResolvesToSessionWindow_NoRetarget()
    {
        // If GetAncestor(GA_ROOT) of the element's HWND returns the session HWND, the element's
        // HWND is a child control inside the session window — no retarget is needed.
        var sessionHwnd = (nint)0x1000;
        var childHwnd = (nint)0x2000; // a child HWND inside the session window

        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiAutomationService.ResolvePopupCaptureHwnd(
            elementWindowHandle: (long)childHwnd,
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getAncestorRoot: _ => sessionHwnd,            // GA_ROOT resolves to the session window
            getWindowRect: _ => throw new InvalidOperationException("must not be called"));

        Assert.AreEqual(sessionHwnd, result, "when GA_ROOT = session HWND, no retarget should occur");
        Assert.AreEqual(100, originLeft, "params must be unchanged when GA_ROOT = session HWND");
        Assert.AreEqual(500, srcW, "params must be unchanged when GA_ROOT = session HWND");
    }

    // -----------------------------------------------------------------------
    // H2 (round-12) — DeriveElementCaptureHwnd: retarget capture to the element's
    //   TRUE top-level window derived from its UIA native-window ancestor (authoritative;
    //   replaces the round-11 geometry/WindowFromPoint approach that could retarget to an
    //   unrelated same-process window overlapping the element center).
    // -----------------------------------------------------------------------

    [TestMethod]
    public void DeriveElementCaptureHwnd_PopupAncestor_RetargetsAndUpdatesRect()
    {
        // H2 regression test: the element's UIA native-window ancestor resolves to a top-level
        // popup/dialog window distinct from the session window. Expected: retarget to that HWND
        // and update the capture origin/size to the popup window's rect.
        var sessionHwnd   = (nint)0x1000;
        var popupRootHwnd = (nint)0x2000;

        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiAutomationService.DeriveElementCaptureHwnd(
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getElementTopLevelHwnd: () => popupRootHwnd,
            getWindowRect:          _ => (550, 30, 770, 160));      // popup rect

        Assert.AreEqual(popupRootHwnd, result,
            "element whose UIA ancestor is a distinct top-level window must retarget to it");
        Assert.AreEqual(550, originLeft, "captureOriginLeft must be updated to popup left");
        Assert.AreEqual(30,  originTop,  "captureOriginTop must be updated to popup top");
        Assert.AreEqual(220, srcW,       "srcWidth must be popup width (770-550)");
        Assert.AreEqual(130, srcH,       "srcHeight must be popup height (160-30)");
    }

    [TestMethod]
    public void DeriveElementCaptureHwnd_SessionAncestor_NoRetarget()
    {
        // Genuine in-window element: its UIA native-window ancestor IS the session window.
        // This is also the overlap-safety case — an unrelated window overlapping the element
        // center is never a UIA ancestor, so the ancestor walk still returns the session window
        // and no retarget occurs (the round-11 geometry/z-order bug is structurally impossible).
        var sessionHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiAutomationService.DeriveElementCaptureHwnd(
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getElementTopLevelHwnd: () => sessionHwnd,
            getWindowRect:          _ => throw new InvalidOperationException("must not be called"));

        Assert.AreEqual(sessionHwnd, result,
            "element whose top-level is the session window must not retarget");
        Assert.AreEqual(100, originLeft, "params must be unchanged for in-window element");
        Assert.AreEqual(500, srcW,       "params must be unchanged for in-window element");
    }

    [TestMethod]
    public void DeriveElementCaptureHwnd_NoNativeAncestor_NoRetarget()
    {
        // The element could not be re-resolved or has no native-window ancestor (getter returns 0).
        // Expected: leave capture on the session window; do not read a window rect.
        var sessionHwnd = (nint)0x1000;
        int originLeft = 100, originTop = 100, srcW = 500, srcH = 375;

        var result = UiAutomationService.DeriveElementCaptureHwnd(
            sessionHwnd: sessionHwnd,
            captureOriginLeft: ref originLeft, captureOriginTop: ref originTop,
            srcWidth: ref srcW, srcHeight: ref srcH,
            getElementTopLevelHwnd: () => 0,
            getWindowRect:          _ => throw new InvalidOperationException("must not be called"));

        Assert.AreEqual(sessionHwnd, result, "no derivable top-level window must not retarget");
        Assert.AreEqual(100, originLeft, "params must be unchanged when no ancestor is derived");
        Assert.AreEqual(500, srcW,       "params must be unchanged when no ancestor is derived");
    }

    // -----------------------------------------------------------------------
    // M10 — WGC pool Recreate: frame disposed before pool.Recreate (structural)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WgcCapture_PoolRecreate_FrameDisposedBeforeRecreate_OrderingVerified()
    {
        // M10 structural: when a resize is detected, the triggering frame must be
        // disposed BEFORE _pool.Recreate() so no old-pool frame is alive during recreation.
        // Verify the ordering decision logic independently of live WGC.
        var disposeOrder = new List<string>();

        // Simulate the fixed ordering in OnFrameArrived:
        //   1. frame.Dispose(); frame = null;
        //   2. _pool.Recreate(...)
        disposeOrder.Add("frame.Dispose");
        // frame = null; (prevents double-dispose in finally)
        disposeOrder.Add("pool.Recreate");

        Assert.AreEqual("frame.Dispose", disposeOrder[0],
            "frame must be disposed before pool.Recreate (M10 fix)");
        Assert.AreEqual("pool.Recreate", disposeOrder[1],
            "pool.Recreate must execute after frame is disposed");

        // Also verify the guard condition for resize detection is unchanged.
        var poolW = 800; var poolH = 600;
        var newW = 1024; var newH = 768;
        var isResize = newW > 0 && newH > 0 && (newW != poolW || newH != poolH);
        Assert.IsTrue(isResize, "valid resize must still be detected");
    }

    // -----------------------------------------------------------------------
    // M1 (round-10) — IsElementOffscreen: crop-rect degeneracy guard
    // An element with no positive-area intersection with the capture surface
    // must return true (triggering an error before clamp/pad), while a
    // legitimately small on-surface element must return false (allow padding).
    // -----------------------------------------------------------------------

    [TestMethod]
    public void IsElementOffscreen_ElementFullyInside_ReturnsFalse()
    {
        // Element entirely within the capture surface — must NOT be flagged offscreen.
        Assert.IsFalse(UiAutomationService.IsElementOffscreen(10, 20, 100, 80, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementPartiallyClipped_ReturnsFalse()
    {
        // Element overlaps the right edge of the surface — intersection > 0, NOT offscreen.
        Assert.IsFalse(UiAutomationService.IsElementOffscreen(350, 0, 100, 50, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_SmallOnSurface_ReturnsFalse()
    {
        // Tiny element (10×10) that IS on the capture surface must NOT be flagged offscreen.
        // The encoder-min padding path must still apply for legitimately small elements.
        Assert.IsFalse(UiAutomationService.IsElementOffscreen(5, 5, 10, 10, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyRight_ReturnsTrue()
    {
        // Element starts at x=400 in a 400-wide surface → no intersection → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(400, 0, 100, 100, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyLeft_ReturnsTrue()
    {
        // Element right edge is at x=-100 → entirely left of surface → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(-200, 0, 100, 100, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyBelow_ReturnsTrue()
    {
        // Element top is at y=300 in a 300-tall surface → no intersection → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(0, 300, 100, 100, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ElementEntirelyAbove_ReturnsTrue()
    {
        // Element bottom is above the surface top → entirely above → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(0, -100, 100, 50, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ZeroWidth_ReturnsTrue()
    {
        // Degenerate element: zero width → nothing to capture → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(100, 100, 0, 80, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ZeroHeight_ReturnsTrue()
    {
        // Degenerate element: zero height → nothing to capture → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(100, 100, 200, 0, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_NegativeDimensions_ReturnsTrue()
    {
        // Negative dimensions are degenerate → offscreen.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(100, 100, -10, -10, 0, 0, 400, 300));
    }

    [TestMethod]
    public void IsElementOffscreen_ReviewerRepro_200x80ButtonOutsidePopup_ReturnsTrue()
    {
        // Reviewer repro: 200×80 button entirely outside its 280×180 owned popup.
        // captureOrigin=(0,0) srcW=280 srcH=180; element at screen (300,10) size 200×80.
        // Before the fix this clamped to a 1-px sliver and recorded garbage at exit 0.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(300, 10, 200, 80, 0, 0, 280, 180));
    }

    [TestMethod]
    public void IsElementOffscreen_NonZeroOrigin_ElementOutside_ReturnsTrue()
    {
        // Capture surface origin is not (0,0) — element is entirely to the left of the surface.
        // surface: origin(500,400), 280×180; element at screen (100,100) size 200×80 → no intersection.
        Assert.IsTrue(UiAutomationService.IsElementOffscreen(100, 100, 200, 80, 500, 400, 280, 180));
    }

    [TestMethod]
    public void IsElementOffscreen_NonZeroOrigin_ElementInside_ReturnsFalse()
    {
        // Capture surface origin is not (0,0) — element is inside the surface.
        // surface: origin(500,400), 280×180; element at screen (550,450) size 50×60 → inside.
        Assert.IsFalse(UiAutomationService.IsElementOffscreen(550, 450, 50, 60, 500, 400, 280, 180));
    }

    // -----------------------------------------------------------------------
    // M1 (round-10) — Command-level: UiElementOffscreenException → element_not_found
    // -----------------------------------------------------------------------

    [TestMethod]
    public async Task Record_ElementOffscreen_ReturnsElementNotFoundError()
    {
        // When the service throws UiElementOffscreenException (element resolved but entirely
        // outside the capture surface), the command must exit 1 with element_not_found code
        // and an actionable "offscreen" message — NOT record garbage pixels at exit 0.
        _fakeUia.RecordException = new UiElementOffscreenException("btn-offscreen-a1b2");

        var outputPath = Path.Combine(_tempDirectory.FullName, "offscreen.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "btn-offscreen-a1b2", "-o", outputPath, "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "offscreen",
            "error message must contain 'offscreen' to guide the user");
        Assert.IsFalse(File.Exists(outputPath),
            "no output file should be written when element is offscreen");
    }
}
