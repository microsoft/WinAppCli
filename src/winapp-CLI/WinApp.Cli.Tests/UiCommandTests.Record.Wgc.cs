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

    [TestMethod]
    public void Record_ShouldEncodeClosedDrainFrame_SkipsAlreadyEncodedVersion()
    {
        Assert.IsFalse(UiAutomationService.ShouldEncodeClosedDrainFrame(cachedVersion: 7, lastEncodedVersion: 7));
    }

    [TestMethod]
    public void Record_ShouldEncodeClosedDrainFrame_EncodesNewerVersion()
    {
        Assert.IsTrue(UiAutomationService.ShouldEncodeClosedDrainFrame(cachedVersion: 8, lastEncodedVersion: 7));
    }

    [TestMethod]
    public void WgcCapture_SizeChangeDetection_ResizeTriggersRecreate()
    {
        var poolSize = new Windows.Graphics.SizeInt32(800, 600);

        // Resize — different size, non-zero → should recreate.
        var shouldRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new Windows.Graphics.SizeInt32(1024, 768));
        Assert.IsTrue(shouldRecreate, "valid resize must trigger pool recreation");

        // Same size — must not recreate.
        var sameNoRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new Windows.Graphics.SizeInt32(800, 600));
        Assert.IsFalse(sameNoRecreate, "same size must not trigger pool recreation");

        // Zero size — must not recreate (guard against invalid frames).
        var zeroNoRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new Windows.Graphics.SizeInt32(0, 0));
        Assert.IsFalse(zeroNoRecreate, "zero-size frame must not trigger pool recreation");

        // Partial zero — must not recreate.
        var partialZeroNoRecreate = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            poolSize,
            new Windows.Graphics.SizeInt32(0, 600));
        Assert.IsFalse(partialZeroNoRecreate, "zero width must not trigger pool recreation");
    }

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

    [TestMethod]
    public void WgcCapture_PoolRecreate_FrameDisposedBeforeRecreate_OrderingVerified()
    {
        var disposeOrder = new List<string>();

        WgcCapture.FrameGrabber.DisposeFrameBeforeRecreate(
            () => disposeOrder.Add("frame.Dispose"),
            () => disposeOrder.Add("pool.Recreate"));

        Assert.AreEqual("frame.Dispose", disposeOrder[0],
            "frame must be disposed before pool.Recreate (M10 fix)");
        Assert.AreEqual("pool.Recreate", disposeOrder[1],
            "pool.Recreate must execute after frame is disposed");

        var isResize = WgcCapture.FrameGrabber.ShouldRecreateFramePool(
            new Windows.Graphics.SizeInt32(800, 600),
            new Windows.Graphics.SizeInt32(1024, 768));
        Assert.IsTrue(isResize, "valid resize must still be detected");
    }
}
