// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    public async Task Record_FramesDirectory_RequiresTimedRecording()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var framesDirectory = Path.Join(_tempDirectory.FullName, "unbounded.frames");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--frames-dir", framesDirectory, "--json"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--duration-sec greater than 0");
        Assert.IsFalse(Directory.Exists(framesDirectory));
    }

    [TestMethod]
    public async Task Record_FramesDirectory_RejectsExcessiveCadenceAndSampleCount()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var framesDirectory = Path.Join(_tempDirectory.FullName, "limits.frames");

        var fpsExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "--fps", "31", "--frames-dir", framesDirectory, "--json"]);
        Assert.AreEqual(1, fpsExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "1 through 30");

        ConsoleStdErr.GetStringBuilder().Clear();
        var countExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "601", "--fps", "30", "--frames-dir", framesDirectory, "--json"]);
        Assert.AreEqual(1, countExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "18,000");
    }

    [TestMethod]
    public async Task Record_FramesDirectory_DefaultsAndBoundsMaxEdge()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var framesDirectory = Path.Join(_tempDirectory.FullName, "max-edge.frames");

        var successExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "--frames-dir", framesDirectory, "--json"]);
        Assert.AreEqual(0, successExitCode);
        Assert.AreEqual(1280, _fakeUia.LastRecordOptions?.MaxEdge);

        ConsoleStdErr.GetStringBuilder().Clear();
        var unboundedExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--max-edge", "0",
                "--frames-dir", Path.Join(_tempDirectory.FullName, "unbounded-edge.frames"),
                "--json",
            ]);
        Assert.AreEqual(1, unboundedExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "does not support --max-edge 0");

        ConsoleStdErr.GetStringBuilder().Clear();
        var rejectedExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--max-edge", "4097",
                "--frames-dir", Path.Join(_tempDirectory.FullName, "too-large.frames"),
                "--json",
            ]);
        Assert.AreEqual(1, rejectedExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "up to 4096");
    }

    [TestMethod]
    public async Task Record_FramesDirectory_NeverClobbersExistingMp4()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "existing.mp4");
        var framesDirectory = Path.Join(_tempDirectory.FullName, "available.frames");
        await File.WriteAllTextAsync(outputPath, "video sentinel");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", outputPath,
                "--frames-dir", framesDirectory,
                "--json",
            ]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual("video sentinel", await File.ReadAllTextAsync(outputPath));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        StringAssert.Contains(ConsoleStdErr.ToString(), "output_exists");
    }

    [TestMethod]
    public async Task Record_FramesDirectory_NeverClobbersExistingFrameDirectory()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "available.mp4");
        var framesDirectory = Path.Join(_tempDirectory.FullName, "existing.frames");
        Directory.CreateDirectory(framesDirectory);
        var sentinelPath = Path.Join(framesDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "frame sentinel");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", outputPath,
                "--frames-dir", framesDirectory,
                "--json",
            ]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(outputPath));
        Assert.AreEqual("frame sentinel", await File.ReadAllTextAsync(sentinelPath));
        StringAssert.Contains(ConsoleStdErr.ToString(), "output_exists");
    }

    [TestMethod]
    public async Task Record_FramesDirectory_RejectsEitherPathContainingTheOther()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "nested.mp4");
        var framesDirectory = Path.Join(outputPath, "frames");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", outputPath,
                "--frames-dir", framesDirectory,
                "--json",
            ]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "must not contain one another");
        Assert.IsFalse(Path.Exists(outputPath), "validation must run before creating either path");

        ConsoleStdErr.GetStringBuilder().Clear();
        var containingFramesDirectory = Path.Join(_tempDirectory.FullName, "outer.frames");
        var nestedOutputPath = Path.Join(containingFramesDirectory, "nested.mp4");
        exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", nestedOutputPath,
                "--frames-dir", containingFramesDirectory,
                "--json",
            ]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "must not contain one another");
        Assert.IsFalse(Path.Exists(containingFramesDirectory), "validation must run before creating either path");
    }

    [TestMethod]
    public async Task Record_FramesDirectory_AcceptsTrailingDirectorySeparator()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var framesDirectory = Path.Join(_tempDirectory.FullName, "trailing.frames");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--frames-dir", framesDirectory + Path.DirectorySeparatorChar,
                "--json",
            ]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(framesDirectory, _fakeUia.LastRecordOptions?.FramesDirectory);
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "manifest.json")));
    }

    [TestMethod]
    public async Task Record_FramesDirectory_EmitsAdditiveJsonAndProgressEvents()
    {
        _fakeUia.RecordResult = new RecordCaptureResult
        {
            Frames = 10,
            Width = 640,
            Height = 480,
            Mode = "wgc",
            ElapsedMs = 1_000,
            AchievedFps = 10,
            CadenceRatio = 1,
            StopReason = "duration_elapsed",
        };
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "frames.mp4");
        var framesDirectory = Path.Join(_tempDirectory.FullName, "frames.bundle");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--fps", "10",
                "--output", outputPath,
                "--frames-dir", framesDirectory,
                "--json",
            ]);

        Assert.AreEqual(0, exitCode);
        var result = JsonSerializer.Deserialize<JsonElement>(TestAnsiConsole.Output);
        Assert.AreEqual(outputPath, result.GetProperty("path").GetString());
        Assert.AreEqual(10, result.GetProperty("frames").GetInt32());
        Assert.AreEqual(10, result.GetProperty("achievedFps").GetDouble());
        Assert.AreEqual("duration_elapsed", result.GetProperty("stopReason").GetString());
        var artifacts = result.GetProperty("frameArtifacts");
        Assert.AreEqual(framesDirectory, artifacts.GetProperty("directory").GetString());
        Assert.AreEqual(10, artifacts.GetProperty("samples").GetInt32());
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Join(framesDirectory, "frames.ndjson")));

        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "\"recording-started\"");
        StringAssert.Contains(stderr, "\"recording-progress\"");
        StringAssert.Contains(stderr, framesDirectory.Replace("\\", "\\\\"));
    }

    [TestMethod]
    public async Task Record_PartialOutput_EmitsStableRecoveryEnvelope()
    {
        var videoPath = Path.Join(_tempDirectory.FullName, "preserved.mp4");
        _fakeUia.RecordException = new RecordPartialOutputException(
            "Frame output failed.",
            videoPath,
            framesDirectory: null,
            "Retry with a new frame path.",
            new IOException("disk full"));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "--json"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "\"partial_output\"");
        StringAssert.Contains(stderr, "\"recoveryHint\"");
        StringAssert.Contains(stderr, videoPath.Replace("\\", "\\\\"));
    }

    [TestMethod]
    public async Task Record_PartialOutput_HumanOutputIdentifiesPreservedArtifactAndRecovery()
    {
        var framesDirectory = Path.Join(_tempDirectory.FullName, "preserved.frames");
        _fakeUia.RecordException = new RecordPartialOutputException(
            "MP4 recording failed.",
            videoPath: null,
            framesDirectory,
            "Inspect the frame bundle and retry.",
            new IOException("disk full"));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, framesDirectory);
        StringAssert.Contains(stderr, "Inspect the frame bundle and retry.");
    }

    [TestMethod]
    public async Task Record_FramesDirectory_HumanStatusShowsBothDestinations()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "status.mp4");
        var framesDirectory = Path.Join(_tempDirectory.FullName, "status.frames");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", outputPath,
                "--frames-dir", framesDirectory,
            ]);

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(TestAnsiConsole.Output, Path.GetFileName(outputPath));
        StringAssert.Contains(TestAnsiConsole.Output, Path.GetFileName(framesDirectory));
    }

    [TestMethod]
    public async Task Record_FrameOutputFailureWithoutArtifact_EmitsStableRecoveryEnvelope()
    {
        _fakeUia.RecordException = new RecordFrameOutputException(
            "Frame output failed.",
            new IOException("disk full"));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1", "--json"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "\"frame_output_failed\"");
        StringAssert.Contains(stderr, "\"recoveryHint\"");
        StringAssert.Contains(stderr, "\"IOException\"");
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_WritesCompleteTimelineAndDeduplicatesExactPixels()
    {
        var finalDirectory = Path.Join(_tempDirectory.FullName, "writer.frames");
        var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(finalDirectory));
        var first = Enumerable.Repeat((byte)0x20, 64 * 64 * 4).ToArray();
        var changed = Enumerable.Repeat((byte)0xA0, 64 * 64 * 4).ToArray();

        await writer.WriteAsync(first, Sample(0, 12, 0), CancellationToken.None);
        await writer.WriteAsync(first, Sample(1, 108, 100), CancellationToken.None);
        await writer.WriteAsync(changed, Sample(2, 211, 200), CancellationToken.None);
        var result = await writer.CompleteAsync(Completion());
        await writer.DisposeAsync();

        Assert.AreEqual(3, result.Samples);
        Assert.AreEqual(2, result.Images);
        Assert.AreEqual(1, result.RepeatedSamples);

        var images = Directory.GetFiles(Path.Join(finalDirectory, "frames"), "*.jpg");
        Assert.AreEqual(2, images.Length);
        CollectionAssert.Contains(
            images.Select(Path.GetFileName).ToArray(),
            "frame-000000-t000000000012.jpg");
        using var decoded = SKBitmap.Decode(images[0]);
        Assert.IsNotNull(decoded);
        Assert.AreEqual(64, decoded.Width);
        Assert.AreEqual(64, decoded.Height);

        var lines = await File.ReadAllLinesAsync(Path.Join(finalDirectory, "frames.ndjson"));
        Assert.AreEqual(3, lines.Length);
        var firstEntry = JsonSerializer.Deserialize<JsonElement>(lines[0]);
        var repeatedEntry = JsonSerializer.Deserialize<JsonElement>(lines[1]);
        var changedEntry = JsonSerializer.Deserialize<JsonElement>(lines[2]);
        Assert.IsTrue(firstEntry.GetProperty("changed").GetBoolean());
        Assert.IsFalse(repeatedEntry.GetProperty("changed").GetBoolean());
        Assert.AreEqual(
            firstEntry.GetProperty("file").GetString(),
            repeatedEntry.GetProperty("file").GetString());
        Assert.IsTrue(changedEntry.GetProperty("changed").GetBoolean());
        Assert.AreNotEqual(
            firstEntry.GetProperty("sha256").GetString(),
            changedEntry.GetProperty("sha256").GetString());
        Assert.AreEqual(
            64,
            firstEntry.GetProperty("contentRect").GetProperty("width").GetInt32());
        var firstJpegPath = Path.Join(
            finalDirectory,
            firstEntry.GetProperty("file").GetString()!.Replace('/', Path.DirectorySeparatorChar));
        var expectedHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(firstJpegPath))).ToLowerInvariant();
        Assert.AreEqual(expectedHash, firstEntry.GetProperty("sha256").GetString());

        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Join(finalDirectory, "manifest.json")));
        Assert.AreEqual(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("complete", manifest.GetProperty("status").GetString());
        Assert.AreEqual(3, manifest.GetProperty("timing").GetProperty("sampleCount").GetInt32());
        Assert.AreEqual(2, manifest.GetProperty("timing").GetProperty("imageCount").GetInt32());
        Assert.AreEqual("sha256", manifest.GetProperty("frames").GetProperty("hashAlgorithm").GetString());
        Assert.AreEqual(10, manifest.GetProperty("requested").GetProperty("fps").GetInt32());
        Assert.AreEqual(42, manifest.GetProperty("source").GetProperty("processId").GetInt32());
        Assert.AreEqual("window", manifest.GetProperty("crop").GetProperty("kind").GetString());
        Assert.AreEqual("complete", manifest.GetProperty("video").GetProperty("status").GetString());
        Assert.AreEqual(3, manifest.GetProperty("video").GetProperty("frameCount").GetInt32());
        Assert.AreEqual(
            Path.Join(_tempDirectory.FullName, "video.mp4"),
            manifest.GetProperty("video").GetProperty("path").GetString());
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_PublicationDoesNotReplaceExistingDirectory()
    {
        var finalDirectory = Path.Join(_tempDirectory.FullName, "race.frames");
        var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(finalDirectory));
        await writer.WriteAsync(new byte[64 * 64 * 4], Sample(0, 1, 0), CancellationToken.None);
        Directory.CreateDirectory(finalDirectory);
        await File.WriteAllTextAsync(Path.Join(finalDirectory, "sentinel.txt"), "keep");

        await Assert.ThrowsExactlyAsync<IOException>(() => writer.CompleteAsync(Completion()));
        await writer.AbortAsync();

        Assert.AreEqual("keep", await File.ReadAllTextAsync(Path.Join(finalDirectory, "sentinel.txt")));
        Assert.IsFalse(Directory.EnumerateDirectories(
            _tempDirectory.FullName,
            ".*.staging",
            SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    public void RecordFrameBundleWriter_QueueCapacityIsBoundedByMemoryBudget()
    {
        Assert.AreEqual(4, RecordFrameBundleWriter.ComputeQueueCapacity(64, 64));
        Assert.AreEqual(2, RecordFrameBundleWriter.ComputeQueueCapacity(3_840, 2_160));
        Assert.AreEqual(1, RecordFrameBundleWriter.ComputeQueueCapacity(4_096, 2_304));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => RecordFrameBundleWriter.ComputeQueueCapacity(4_096, 4_096));

        var configuration = CreateFrameConfiguration(
            Path.Join(_tempDirectory.FullName, "oversized.frames"),
            width: 4_096,
            height: 4_096);
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RecordFrameBundleWriter(configuration));
        StringAssert.Contains(exception.Message, "256 MiB pipeline memory budget");
        Assert.IsFalse(Directory.Exists(configuration.FinalDirectory));
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_FullQueueAppliesBackpressureAndDrainsWithoutDropping()
    {
        var originalEncodeJpeg = RecordFrameBundleWriter.s_encodeJpeg;
        using var releaseEncoder = new ManualResetEventSlim();
        var encoderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordFrameBundleWriter? writer = null;
        try
        {
            RecordFrameBundleWriter.s_encodeJpeg = (_, _, _) =>
            {
                encoderEntered.TrySetResult();
                releaseEncoder.Wait();
                return [1, 2, 3];
            };

            var finalDirectory = Path.Join(_tempDirectory.FullName, "backpressure.frames");
            writer = new RecordFrameBundleWriter(CreateFrameConfiguration(finalDirectory));
            var pixels = new byte[64 * 64 * 4];

            await writer.WriteAsync(pixels, Sample(0, 1, 0), CancellationToken.None);
            await encoderEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 1; index <= 4; index++)
            {
                await writer.WriteAsync(
                    pixels,
                    Sample(index, index * 100L, index * 100d),
                    CancellationToken.None);
            }

            var blockedPixels = new byte[64 * 64 * 4];
            var blockedWrite = writer.WriteAsync(
                blockedPixels,
                Sample(5, 500, 500),
                CancellationToken.None).AsTask();
            await Task.Delay(100);
            Assert.IsFalse(blockedWrite.IsCompleted, "a full queue must apply backpressure");

            blockedPixels[0] = 1;
            releaseEncoder.Set();
            await blockedWrite.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await writer.CompleteAsync(Completion());

            Assert.AreEqual(6, result.Samples);
            Assert.AreEqual(2, result.Images, "a blocked frame must be cloned only after queue capacity is available");
            Assert.AreEqual(4, result.RepeatedSamples);
        }
        finally
        {
            releaseEncoder.Set();
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
            RecordFrameBundleWriter.s_encodeJpeg = originalEncodeJpeg;
        }
    }

    private RecordFrameBundleConfiguration CreateFrameConfiguration(
        string finalDirectory,
        int width = 64,
        int height = 64)
        => new()
        {
            FinalDirectory = finalDirectory,
            VideoPath = Path.Join(_tempDirectory.FullName, "video.mp4"),
            RecordingId = "recording-test",
            StartedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Width = width,
            Height = height,
            ContentRect = new RecordFrameRectManifest { Width = width, Height = height },
            Requested = new RecordFrameRequestManifest
            {
                DurationSec = 1,
                Fps = 10,
                MaxEdge = 1280,
            },
            Source = new RecordFrameSourceManifest
            {
                ProcessId = 42,
                ProcessName = "CalculatorApp",
                WindowTitle = "Calculator",
                SessionHwnd = 100,
                CaptureHwnd = 100,
                CaptureMode = "wgc",
            },
            Crop = new RecordFrameCropManifest
            {
                Kind = "window",
                Rect = new RecordFrameRectManifest { Width = 64, Height = 64 },
            },
            Logger = NullLogger.Instance,
        };

    private static RecordFrameSample Sample(int index, long elapsedMs, double mediaTimeMs)
        => new()
        {
            SampleIndex = index,
            ElapsedMs = elapsedMs,
            MediaTimeMs = mediaTimeMs,
            SourceVersion = index + 1,
            SourceWidth = 64,
            SourceHeight = 64,
            ContentRect = new RecordFrameRectManifest { Width = 64, Height = 64 },
        };

    private static RecordFrameCompletion Completion()
        => new()
        {
            Status = "complete",
            StopReason = "duration_elapsed",
            ElapsedMs = 300,
            AchievedFps = 10,
            CadenceRatio = 1,
            VideoStatus = "complete",
            VideoFrameCount = 3,
            VideoFileSize = 123,
        };
}
