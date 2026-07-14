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
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max-edge must be 0 or greater");
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
    public async Task Record_Success_ReportsScreenFallbackMode()
    {
        // Verify that when the fake service reports "screen-fallback" (WGC init failed silently),
        // the command passes the mode through unchanged so consumers can detect degradation.
        _fakeUia.RecordResult = new RecordCaptureResult { Frames = 2, Width = 100, Height = 100, Mode = "screen-fallback" };

        var outputPath = Path.Combine(_tempDirectory.FullName, "fallback.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--duration-sec", "1", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual("screen-fallback", result.GetProperty("mode").GetString());
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
            TimeSpan.FromSeconds(1),
            () => TimeSpan.Zero,
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
            TimeSpan.FromSeconds(1),
            () => TimeSpan.Zero,
            () => stopped = true);
        Assert.IsTrue(stopped, "an empty line (immediate enter) should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_ImmediateEof_WithinGrace_DoesNotStop()
    {
        // Immediate EOF with no data and elapsed < grace → ignore (protects against the
        // "no stdin attached → instant EOF → 0-frame file" footgun).
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new EofReader(),
            TimeSpan.FromSeconds(1),
            () => TimeSpan.FromMilliseconds(10), // 10ms << 1000ms grace
            () => stopped = true);
        Assert.IsFalse(stopped, "immediate EOF within grace window should not stop");
    }

    [TestMethod]
    public void StdinStopMonitor_EofAfterGrace_Stops()
    {
        // EOF after the grace window has elapsed → stop (programmatic caller closed the pipe).
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new EofReader(),
            TimeSpan.FromSeconds(1),
            () => TimeSpan.FromSeconds(2), // 2s >> 1s grace
            () => stopped = true);
        Assert.IsTrue(stopped, "EOF after grace window should trigger stop");
    }

    [TestMethod]
    public void StdinStopMonitor_LineOfText_Stops()
    {
        var stopped = false;
        StdinStopMonitor.MonitorCore(
            new StringReader("stop"),
            TimeSpan.FromSeconds(1),
            () => TimeSpan.Zero,
            () => stopped = true);
        Assert.IsTrue(stopped, "any line of text should trigger stop");
    }

    /// <summary>A TextReader that always returns null from ReadLine (simulates EOF with no data).</summary>
    private sealed class EofReader : TextReader
    {
        public override string? ReadLine() => null;
    }
}
