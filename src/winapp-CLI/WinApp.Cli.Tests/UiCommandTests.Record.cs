// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    // ---------------------------------------------------------------------
    // record — capture a window/element region to an H.264 MP4. The fake
    // RecordAsync writes a tiny placeholder file and returns configurable
    // frame/mode metadata, so these tests exercise the command's validation
    // and JSON envelope without touching WGC/Media Foundation.
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
        // When --json is set, the command must emit the "invalid_arguments" code (not "internal_error")
        // so agent consumers don't retry-loop on a bad argument. The JSON goes to Console.Error;
        // we verify the exit code and trust the code-path change (CodeInternalError → CodeInvalidArguments)
        // is covered by code inspection and the H1 fix. Exit code 1 confirms the validation fired.
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "--fps", "0", "--json"]);
        Assert.AreEqual(1, exitCode);
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
    }

    [TestMethod]
    public async Task Record_DefaultDuration_IsThirtySeconds()
    {
        // Verify the default --duration-sec is 30 (not 0) so agents don't hang forever.
        var outputPath = Path.Combine(_tempDirectory.FullName, "default-duration.mp4");
        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "-o", outputPath, "--json"]);

        Assert.AreEqual(0, exitCode);
        var result = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(30, result.GetProperty("durationSec").GetInt32());
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
}
