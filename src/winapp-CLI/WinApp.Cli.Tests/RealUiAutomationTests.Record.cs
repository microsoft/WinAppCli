// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
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

        public void WriteFrame(ReadOnlySpan<byte> bgra, long sampleTimeHns, long sampleDurationHns)
        {
            Assert.AreEqual(Width * Height * 4, bgra.Length);
            Assert.IsTrue(sampleDurationHns > 0);
            FramesWritten++;
        }

        public void Complete()
        {
            Completed = true;
            File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        }

        public void Dispose()
        {
        }
    }
}
