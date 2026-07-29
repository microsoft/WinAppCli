// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
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
        StringAssert.Contains(ConsoleStdErr.ToString(), "64 through 4096");

        ConsoleStdErr.GetStringBuilder().Clear();
        var tooSmallExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--max-edge", "32",
                "--frames-dir", Path.Join(_tempDirectory.FullName, "too-small.frames"),
                "--json",
            ]);
        Assert.AreEqual(1, tooSmallExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "64 through 4096");
        Assert.IsFalse(ConsoleStdErr.ToString().Contains("0 (unbounded)", StringComparison.Ordinal));

        ConsoleStdErr.GetStringBuilder().Clear();
        var tooLargeExitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--max-edge", "4097",
                "--frames-dir", Path.Join(_tempDirectory.FullName, "too-large.frames"),
                "--json",
            ]);
        Assert.AreEqual(1, tooLargeExitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "64 through 4096");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Record_FramesDirectory_TextMode_LogsTruncationWarning()
    {
        const string warning = "Frame artifacts reached the 1 GiB byte limit; the valid indexed prefix was published.";
        _fakeUia.RecordResult = new RecordCaptureResult
        {
            Frames = 3,
            Width = 640,
            Height = 480,
            Mode = "wgc",
            Warnings = [warning],
        };

        var command = GetRequiredService<UiRecordCommand>();
        var previousAmbient = AnsiConsole.Console;
        var ambient = new TestConsole();
        AnsiConsole.Console = ambient;
        int exitCode;
        try
        {
            exitCode = await ParseAndInvokeWithCaptureAsync(
                command,
                [
                    "-a", "TestApp",
                    "--duration-sec", "1",
                    "--output", Path.Join(_tempDirectory.FullName, "truncated.mp4"),
                    "--frames-dir", Path.Join(_tempDirectory.FullName, "truncated.frames"),
                ]);
        }
        finally
        {
            AnsiConsole.Console = previousAmbient;
        }

        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(ambient.Output, "Frame artifacts reached the 1 GiB byte limit");
        StringAssert.Contains(ambient.Output, "valid indexed prefix was");
        Assert.IsFalse(ConsoleStdErr.ToString().Contains(warning, StringComparison.Ordinal));
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
    public async Task Record_FramesDirectory_RejectsPathThatIsAnExistingFile()
    {
        var command = GetRequiredService<UiRecordCommand>();
        var outputPath = Path.Join(_tempDirectory.FullName, "available.mp4");
        var framesPath = Path.Join(_tempDirectory.FullName, "frames-file");
        await File.WriteAllTextAsync(framesPath, "sentinel");

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", outputPath,
                "--frames-dir", framesPath,
                "--json",
            ]);

        Assert.AreEqual(1, exitCode);
        Assert.IsFalse(File.Exists(outputPath));
        Assert.AreEqual("sentinel", await File.ReadAllTextAsync(framesPath));
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
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(stderr));
        using var document = JsonDocument.ParseValue(ref reader);
        var error = document.RootElement.GetProperty("error");
        Assert.AreEqual("partial_output", error.GetProperty("code").GetString());
        Assert.AreEqual("Retry with a new frame path.", error.GetProperty("recoveryHint").GetString());
        var partialOutput = error.GetProperty("partialOutput");
        Assert.AreEqual(videoPath, partialOutput.GetProperty("videoPath").GetString());
        Assert.IsFalse(partialOutput.TryGetProperty("framesDirectory", out _));
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
        Assert.IsFalse(stderr.Contains("RecordPartialOutputException", StringComparison.Ordinal));
        Assert.IsFalse(stderr.Contains("   at ", StringComparison.Ordinal));
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
            "Retry with a new frame path.",
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
        Assert.IsFalse(stderr.Contains("RecordFrameOutputException", StringComparison.Ordinal));
        Assert.IsFalse(stderr.Contains("   at ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Record_FrameOutputFailure_HumanOutputIncludesRecoveryHint()
    {
        const string recoveryHint = "Capture a smaller window or retry without --frames-dir.";
        _fakeUia.RecordException = new RecordFrameOutputException(
            "Frame output failed.",
            recoveryHint,
            new RecordFramePipelineLimitException("Source buffers exceed the budget.", false));
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            ["-a", "TestApp", "--duration-sec", "1"]);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, recoveryHint);
        Assert.IsFalse(stderr.Contains("RecordFrameOutputException", StringComparison.Ordinal));
        Assert.IsFalse(stderr.Contains("   at ", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Record_StartedEventOmitsUnavailableFrameArtifactPaths()
    {
        _fakeUia.RecordingStartedFrameArtifactsActiveOverride = false;
        var command = GetRequiredService<UiRecordCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command,
            [
                "-a", "TestApp",
                "--duration-sec", "1",
                "--output", Path.Join(_tempDirectory.FullName, "recording.mp4"),
                "--frames-dir", Path.Join(_tempDirectory.FullName, "recording.frames"),
                "--json",
            ]);

        Assert.AreEqual(0, exitCode);
        var startedLine = ConsoleStdErr.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains("\"recording-started\"", StringComparison.Ordinal));
        var startedEvent = JsonSerializer.Deserialize<JsonElement>(startedLine);
        Assert.IsFalse(startedEvent.TryGetProperty("framesDirectory", out _));
        Assert.IsFalse(startedEvent.TryGetProperty("framesManifest", out _));
        Assert.IsFalse(startedEvent.TryGetProperty("framesIndex", out _));
    }

    [TestMethod]
    public async Task RecordFrameArtifactCoordinator_InitializationFailureLogsSanitizedReason()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var provider = new TextWriterLoggerProvider(stdout, stderr);
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Information).AddProvider(provider));
        RecordFrameBundleWriter.s_create = _ =>
            throw new UnauthorizedAccessException("Access to the frame parent was denied.");
        try
        {
            var coordinator = RecordFrameArtifactCoordinator.Create(
                CreateFrameConfiguration(
                    Path.Join(_tempDirectory.FullName, "unwritable-parent", "recording.frames"),
                    logger: loggerFactory.CreateLogger("recording-test")));

            await coordinator.DisposeAsync();

            Assert.IsNotNull(coordinator.Failure);
            var error = stderr.ToString();
            StringAssert.Contains(error, "Could not initialize frame artifact output");
            StringAssert.Contains(error, "Access to the frame parent was denied.");
            Assert.IsFalse(error.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
            Assert.IsFalse(error.Contains("   at ", StringComparison.Ordinal));
            Assert.IsFalse(error.Contains(".cs:line", StringComparison.Ordinal));
        }
        finally
        {
            RecordFrameBundleWriter.ResetTestSeams();
        }
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
        Assert.AreEqual(0, firstEntry.GetProperty("sampleIndex").GetInt32());
        Assert.AreEqual(12, firstEntry.GetProperty("elapsedMs").GetInt64());
        Assert.AreEqual(0, firstEntry.GetProperty("mediaTimeMs").GetDouble());
        Assert.AreEqual(0, firstEntry.GetProperty("imageIndex").GetInt32());
        Assert.AreEqual(1, repeatedEntry.GetProperty("sampleIndex").GetInt32());
        Assert.AreEqual(108, repeatedEntry.GetProperty("elapsedMs").GetInt64());
        Assert.AreEqual(100, repeatedEntry.GetProperty("mediaTimeMs").GetDouble());
        Assert.AreEqual(0, repeatedEntry.GetProperty("imageIndex").GetInt32());
        Assert.AreEqual(2, changedEntry.GetProperty("sampleIndex").GetInt32());
        Assert.AreEqual(211, changedEntry.GetProperty("elapsedMs").GetInt64());
        Assert.AreEqual(200, changedEntry.GetProperty("mediaTimeMs").GetDouble());
        Assert.AreEqual(1, changedEntry.GetProperty("imageIndex").GetInt32());
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
    public async Task RecordFrameBundleWriter_ByteLimitPublishesIndexedPrefixAndDrains()
    {
        RecordFrameBundleWriter? writer = null;
        try
        {
            var finalDirectory = Path.Join(_tempDirectory.FullName, "limited.frames");
            writer = new RecordFrameBundleWriter(CreateFrameConfiguration(
                finalDirectory,
                maximumBundleBytes: 1024 * 1024 + 1_500));

            for (var index = 0; index < 20; index++)
            {
                var pixels = Enumerable.Repeat((byte)index, 64 * 64 * 4).ToArray();
                await writer.WriteAsync(
                    pixels,
                    Sample(index, index * 100L, index * 100d),
                    CancellationToken.None);
            }

            var result = await writer.CompleteAsync(Completion());
            Assert.IsTrue(result.Truncated);
            Assert.AreEqual(1024 * 1024 + 1_500, result.ByteLimit);
            Assert.IsGreaterThan(0, result.Samples);
            Assert.IsLessThan(20, result.Samples);
            Assert.IsTrue(result.TotalBytes <= result.ByteLimit);

            var manifest = JsonSerializer.Deserialize<JsonElement>(
                await File.ReadAllTextAsync(Path.Join(finalDirectory, "manifest.json")));
            Assert.AreEqual("truncated", manifest.GetProperty("status").GetString());
            Assert.IsTrue(manifest.GetProperty("frames").GetProperty("truncated").GetBoolean());
            Assert.AreEqual(
                result.ByteLimit,
                manifest.GetProperty("frames").GetProperty("byteLimit").GetInt64());
            var indexLines = await File.ReadAllLinesAsync(Path.Join(finalDirectory, "frames.ndjson"));
            Assert.AreEqual(result.Samples, indexLines.Length);
            var lastEntry = JsonSerializer.Deserialize<JsonElement>(indexLines[^1]);
            var timing = manifest.GetProperty("timing");
            var prefixElapsedMs = Math.Max(1, lastEntry.GetProperty("elapsedMs").GetInt64());
            Assert.AreEqual(prefixElapsedMs, timing.GetProperty("elapsedMs").GetInt64());
            Assert.AreEqual(
                result.Samples * 1000.0 / prefixElapsedMs,
                timing.GetProperty("achievedFps").GetDouble(),
                0.0001);
            Assert.AreEqual(
                timing.GetProperty("achievedFps").GetDouble() / 10,
                timing.GetProperty("cadenceRatio").GetDouble(),
                0.0001);

            var indexedFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in indexLines)
            {
                var entry = JsonSerializer.Deserialize<JsonElement>(line);
                var relativePath = entry.GetProperty("file").GetString()!;
                indexedFiles.Add(relativePath);
                var imagePath = Path.Join(
                    finalDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                var bytes = await File.ReadAllBytesAsync(imagePath);
                var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                Assert.AreEqual(expectedHash, entry.GetProperty("sha256").GetString());
                using var decoded = SKBitmap.Decode(bytes);
                Assert.IsNotNull(decoded);
                Assert.AreEqual(64, decoded.Width);
                Assert.AreEqual(64, decoded.Height);
            }

            var publishedFiles = Directory.GetFiles(Path.Join(finalDirectory, "frames"), "*.jpg")
                .Select(path => $"frames/{Path.GetFileName(path)}")
                .ToHashSet(StringComparer.Ordinal);
            CollectionAssert.AreEquivalent(indexedFiles.ToArray(), publishedFiles.ToArray());
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task RecordFrameBundleWriter_JpegFailureRemovesStagingAndFinalOutput()
    {
        var originalEncodeJpeg = RecordFrameBundleWriter.s_encodeJpeg;
        RecordFrameBundleWriter? writer = null;
        var finalDirectory = Path.Join(_tempDirectory.FullName, "jpeg-failure.frames");
        try
        {
            RecordFrameBundleWriter.s_encodeJpeg = (_, _, _) =>
                throw new IOException("simulated JPEG failure");
            writer = new RecordFrameBundleWriter(CreateFrameConfiguration(finalDirectory));
            await writer.WriteAsync(
                new byte[64 * 64 * 4],
                Sample(0, 1, 0),
                CancellationToken.None);

            var exception = await Assert.ThrowsExactlyAsync<IOException>(
                () => writer.CompleteAsync(Completion()));
            StringAssert.Contains(exception.Message, "simulated JPEG failure");
            await Assert.ThrowsExactlyAsync<IOException>(() => writer.AbortAsync());

            Assert.IsFalse(Directory.Exists(finalDirectory));
            Assert.IsFalse(Directory.EnumerateDirectories(
                _tempDirectory.FullName,
                ".*.staging",
                SearchOption.TopDirectoryOnly).Any());
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync();
            }
            RecordFrameBundleWriter.s_encodeJpeg = originalEncodeJpeg;
        }
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
        Assert.AreEqual(4, RecordFrameBundleWriter.ComputeQueueCapacity(64, 64, 64, 64, 0));
        Assert.AreEqual(2, RecordFrameBundleWriter.ComputeQueueCapacity(
            3_840, 2_160, 3_840, 2_160, 0));
        Assert.AreEqual(1, RecordFrameBundleWriter.ComputeQueueCapacity(
            4_096, 2_304, 4_096, 2_304, 0));
        Assert.AreEqual(4, RecordFrameBundleWriter.ComputeQueueCapacity(
            3_840, 2_160, 1_280, 720, 4));
        Assert.ThrowsExactly<RecordFramePipelineLimitException>(
            () => RecordFrameBundleWriter.ComputeQueueCapacity(
                4_096, 4_096, 4_096, 4_096, 0));
        Assert.ThrowsExactly<RecordFramePipelineLimitException>(
            () => RecordFrameBundleWriter.ComputeQueueCapacity(
                5_120, 2_880, 4_096, 2_304, 4));
        Assert.ThrowsExactly<RecordFramePipelineLimitException>(
            () => RecordFrameBundleWriter.ComputeQueueCapacity(
                7_680, 4_320, 1_280, 720, 4));
        Assert.ThrowsExactly<RecordFramePipelineLimitException>(
            () => RecordFrameBundleWriter.ComputeQueueCapacity(
                4_096, 4_096, 1_280, 1_280, 4));

        var configuration = CreateFrameConfiguration(
            Path.Join(_tempDirectory.FullName, "oversized.frames"),
            width: 4_096,
            height: 4_096);
        var exception = Assert.ThrowsExactly<RecordFramePipelineLimitException>(
            () => new RecordFrameBundleWriter(configuration));
        StringAssert.Contains(exception.Message, "256 MiB pipeline memory budget");
        Assert.IsFalse(Directory.Exists(configuration.FinalDirectory));
    }

    [TestMethod]
    public void RecordFrameBundleWriter_SourceResizeCannotExceedConfiguredMemoryBudget()
    {
        var writer = new RecordFrameBundleWriter(CreateFrameConfiguration(
            Path.Join(_tempDirectory.FullName, "resize.frames"),
            width: 1_280,
            height: 720,
            sourceWidth: 3_840,
            sourceHeight: 2_160,
            sourceBufferCount: 4));

        var exception = Assert.ThrowsExactly<RecordFramePipelineLimitException>(
            () => writer.ValidatePipelineDimensions(7_680, 4_320, 4));

        StringAssert.Contains(exception.Message, "256 MiB pipeline memory budget");
        writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
        int height = 64,
        int? sourceWidth = null,
        int? sourceHeight = null,
        int sourceBufferCount = 0,
        long maximumBundleBytes = RecordFrameBundleConfiguration.DefaultMaximumBundleBytes,
        ILogger? logger = null)
        => new()
        {
            FinalDirectory = finalDirectory,
            VideoPath = Path.Join(_tempDirectory.FullName, "video.mp4"),
            RecordingId = "recording-test",
            StartedUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Width = width,
            Height = height,
            SourceWidth = sourceWidth ?? width,
            SourceHeight = sourceHeight ?? height,
            SourceBufferCount = sourceBufferCount,
            MaximumBundleBytes = maximumBundleBytes,
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
            Logger = logger ?? NullLogger.Instance,
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
