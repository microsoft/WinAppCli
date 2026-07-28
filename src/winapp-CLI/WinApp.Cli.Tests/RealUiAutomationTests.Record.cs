// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Text.Json;
using Windows.Win32.Foundation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    public async Task RecordAsync_WgcSeams_EncodesTimedFramesAndReportsResult()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "record.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var frame = Enumerable.Repeat((byte)0x44, 80 * 60 * 4).ToArray();
        var grabber = new FakeFrameGrabber(frame, 80, 60);
        FakeVideoEncoder? encoder = null;
        var started = 0;

        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (hwnd, logger, fps) =>
        {
            Assert.AreEqual(fx.Hwnd, (nint)hwnd);
            Assert.AreEqual(2, fps);
            return grabber;
        };
        Mp4SinkWriterEncoder.s_create = (path, width, height, fps, bitrate) =>
        {
            encoder = new FakeVideoEncoder(path, width, height);
            return encoder;
        };

        var result = await svc.RecordAsync(session, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None, () => started++);

        Assert.AreEqual("wgc", result.Mode);
        Assert.AreEqual(2, result.Frames);
        Assert.AreEqual(64, result.Width);
        Assert.AreEqual(64, result.Height);
        Assert.AreEqual(1, started, "recording should signal readiness exactly once after the first frame");
        Assert.IsNotNull(encoder);
        Assert.AreEqual(2, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
        Assert.IsTrue(grabber.Disposed);
        Assert.IsTrue(new FileInfo(output).Length > 0);
    }

    [TestMethod]
    public async Task RecordAsync_WgcClosedDrainsLatestFrameBeforeStopping()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "closed.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var grabber = new FakeFrameGrabber(Enumerable.Repeat((byte)0x55, 64 * 64 * 4).ToArray(), 64, 64)
        {
            IsClosed = true,
        };

        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) => grabber;
        Mp4SinkWriterEncoder.s_create = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await svc.RecordAsync(session, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 10,
            Fps = 5,
            MaxEdge = 0,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames, "closed WGC items should drain the cached frame once before finalizing");
        Assert.AreEqual("wgc", result.Mode);
        Assert.IsTrue(grabber.Disposed);
    }

    [TestMethod]
    public async Task RecordAsync_CaptureScreen_UsesConsentedScreenPath()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "screen.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var screenCalls = 0;
        UiAutomationService.s_captureFromScreenScaled = (_, _, _, _, tw, th) =>
        {
            screenCalls++;
            return Enumerable.Repeat((byte)0x66, tw * th * 4).ToArray();
        };
        Mp4SinkWriterEncoder.s_create = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await svc.RecordAsync(session, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
            CaptureScreen = true,
        }, CancellationToken.None);

        Assert.AreEqual("screen", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, screenCalls);
    }

    [TestMethod]
    public async Task RecordAsync_PrintWindowFallback_UsesBlankRetryCapture()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var output = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "printwindow.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var windowCalls = 0;
        WgcCapture.s_isSupported = () => false;
        UiAutomationService.s_captureFromWindow = (_, width, height) =>
        {
            windowCalls++;
            return Enumerable.Repeat((byte)0x77, width * height * 4).ToArray();
        };
        Mp4SinkWriterEncoder.s_create = (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await svc.RecordAsync(session, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual("printwindow", result.Mode);
        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, windowCalls);
    }

    [TestMethod]
    public async Task RecordAsync_FrameArtifacts_UseTheProcessedMp4FrameStream()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "record.mp4");
        var framesDirectory = Path.Combine(root, "record.frames");
        var frame = Enumerable.Repeat((byte)0x88, 80 * 60 * 4).ToArray();

        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) => new FakeFrameGrabber(frame, 80, 60);
        Mp4SinkWriterEncoder.s_createNoClobber =
            (path, width, height, _, _) => new FakeVideoEncoder(path, width, height);

        var result = await svc.RecordAsync(session, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = framesDirectory,
            DurationSec = 1,
            Fps = 2,
            MaxEdge = 64,
            CaptureScreen = false,
        }, CancellationToken.None);

        Assert.AreEqual(2, result.Frames);
        Assert.IsNotNull(result.FrameArtifacts);
        Assert.AreEqual(2, result.FrameArtifacts.Samples);
        Assert.AreEqual(1, result.FrameArtifacts.Images, "identical processed BGRA frames should share one JPEG");
        Assert.AreEqual(1, result.FrameArtifacts.RepeatedSamples);
        Assert.IsTrue(File.Exists(Path.Combine(framesDirectory, "manifest.json")));
        Assert.IsTrue(File.Exists(Path.Combine(framesDirectory, "frames.ndjson")));
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(framesDirectory, "frames"), "*.jpg").Length);
    }

    [TestMethod]
    public async Task RecordAsync_FirstMp4WriteFailure_PreservesAcceptedFrameArtifact()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var framesDirectory = Path.Combine(root, "partial.frames");
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                WriteFrameException = new IOException("simulated encoder failure"),
            };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => svc.RecordAsync(session, null, new RecordOptions
            {
                OutputPath = Path.Combine(root, "partial.mp4"),
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual(framesDirectory, exception.FramesDirectory);
        var manifest = JsonSerializer.Deserialize<JsonElement>(
            await File.ReadAllTextAsync(Path.Combine(framesDirectory, "manifest.json")));
        Assert.AreEqual("partial", manifest.GetProperty("status").GetString());
        Assert.AreEqual(1, manifest.GetProperty("timing").GetProperty("sampleCount").GetInt32());
        Assert.AreEqual(0, manifest.GetProperty("video").GetProperty("frameCount").GetInt32());
    }

    [TestMethod]
    public async Task RecordAsync_PostEncodingMetadataFailure_RemovesFrameStaging()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "missing.mp4");
        var framesDirectory = Path.Combine(root, "missing.frames");
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                DeleteOutputOnComplete = true,
            };

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            () => svc.RecordAsync(session, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.IsFalse(Directory.Exists(framesDirectory));
        Assert.IsFalse(Directory.EnumerateDirectories(
            root,
            ".*.staging",
            SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    public async Task RecordAsync_CancellationBeforeFirstSamplePublishesNoArtifacts()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "cancelled.mp4");
        var framesDirectory = Path.Combine(root, "cancelled.frames");
        using var cts = new CancellationTokenSource();
        var frameSink = new FakeFrameSink();
        FakeVideoEncoder? encoder = null;
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            encoder = new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = _ =>
        {
            cts.Cancel();
            return frameSink;
        };

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => svc.RecordAsync(session, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = framesDirectory,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, cts.Token));

        Assert.IsFalse(File.Exists(output));
        Assert.IsFalse(Directory.Exists(framesDirectory));
        Assert.IsTrue(frameSink.Aborted);
        Assert.IsNotNull(encoder);
        Assert.IsFalse(encoder!.Completed);
        Assert.IsTrue(encoder.Disposed);
    }

    [TestMethod]
    public async Task RecordAsync_CancellationAfterProcessedFrameCommitsSampleBeforeStopping()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "drained.mp4");
        using var cts = new CancellationTokenSource();
        var frameSink = new FakeFrameSink
        {
            OnWrite = cancellationToken =>
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            },
        };
        FakeVideoEncoder? encoder = null;
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            encoder = new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = _ => frameSink;

        var result = await svc.RecordAsync(session, null, new RecordOptions
        {
            OutputPath = output,
            FramesDirectory = Path.Combine(root, "drained.frames"),
            DurationSec = 10,
            Fps = 1,
            MaxEdge = 64,
        }, cts.Token);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, frameSink.SampleCount);
        Assert.AreEqual("cancelled", result.StopReason);
        Assert.IsNotNull(encoder);
        Assert.AreEqual(1, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
    }

    [TestMethod]
    public async Task RecordAsync_FrameAndMp4FailureWithoutArtifactThrowsFrameOutputException()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height)
            {
                WriteFrameException = new IOException("simulated encoder failure"),
            };
        RecordFrameBundleWriter.s_create = _ => new FakeFrameSink
        {
            WriteException = new IOException("simulated frame failure"),
        };

        var exception = await Assert.ThrowsExactlyAsync<RecordFrameOutputException>(
            () => svc.RecordAsync(session, null, new RecordOptions
            {
                OutputPath = Path.Combine(root, "failed.mp4"),
                FramesDirectory = Path.Combine(root, "failed.frames"),
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.IsInstanceOfType<IOException>(exception.InnerException);
        StringAssert.Contains(exception.InnerException.Message, "frame failure");
    }

    [TestMethod]
    public async Task RecordAsync_FrameAndAbortFailureStillPreservesMp4()
    {
        using var fx = new UiaTestFixture();
        var svc = NewService();
        var session = SessionFor(fx);
        await ResolveAsync(svc, session, "btnInvoke");

        var root = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "preserved.mp4");
        FakeVideoEncoder? encoder = null;
        WgcCapture.s_isSupported = () => true;
        WgcCapture.s_startGrabber = (_, _, _) =>
            new FakeFrameGrabber(new byte[64 * 64 * 4], 64, 64);
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            encoder = new FakeVideoEncoder(path, width, height);
        RecordFrameBundleWriter.s_create = _ => new FakeFrameSink
        {
            WriteException = new IOException("simulated frame failure"),
            AbortException = new IOException("simulated cleanup failure"),
        };

        var exception = await Assert.ThrowsExactlyAsync<RecordPartialOutputException>(
            () => svc.RecordAsync(session, null, new RecordOptions
            {
                OutputPath = output,
                FramesDirectory = Path.Combine(root, "failed.frames"),
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));

        Assert.AreEqual(output, exception.VideoPath);
        Assert.IsTrue(File.Exists(output));
        Assert.IsNotNull(encoder);
        Assert.AreEqual(1, encoder!.FramesWritten);
        Assert.IsTrue(encoder.Completed);
    }

    private sealed class FakeFrameGrabber(byte[] pixels, int width, int height) : WgcCapture.IFrameGrabber
    {
        private long _version;

        public bool IsClosed { get; init; }

        public bool Disposed { get; private set; }

        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest()
            => (pixels, width, height, Interlocked.Increment(ref _version));

        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeVideoEncoder(string path, int width, int height) : IVideoEncoder
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public int FramesWritten { get; private set; }

        public bool Completed { get; private set; }

        public bool Disposed { get; private set; }

        public Exception? WriteFrameException { get; init; }

        public bool DeleteOutputOnComplete { get; init; }

        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns)
        {
            if (WriteFrameException is not null)
            {
                throw WriteFrameException;
            }

            Assert.AreEqual(Width * Height * 4, bgra.Length);
            Assert.IsTrue(sampleDurationHns > 0);
            FramesWritten++;
        }

        public void Complete()
        {
            Completed = true;
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
            if (DeleteOutputOnComplete)
            {
                File.Delete(path);
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeFrameSink : IRecordFrameSink
    {
        public int SampleCount { get; private set; }

        public int ImageCount => SampleCount;

        public bool Aborted { get; private set; }

        public Action<CancellationToken>? OnWrite { get; init; }

        public Exception? WriteException { get; init; }

        public Exception? AbortException { get; init; }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> bgra,
            RecordFrameSample sample,
            CancellationToken cancellationToken)
        {
            OnWrite?.Invoke(cancellationToken);
            if (WriteException is not null)
            {
                throw WriteException;
            }
            SampleCount++;
            return ValueTask.CompletedTask;
        }

        public Task<RecordFrameArtifactResult> CompleteAsync(RecordFrameCompletion completion)
            => Task.FromResult(new RecordFrameArtifactResult
            {
                Directory = "frames",
                Manifest = "frames/manifest.json",
                Index = "frames/frames.ndjson",
                Samples = SampleCount,
                Images = ImageCount,
            });

        public Task AbortAsync()
        {
            Aborted = true;
            if (AbortException is not null)
            {
                throw AbortException;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
