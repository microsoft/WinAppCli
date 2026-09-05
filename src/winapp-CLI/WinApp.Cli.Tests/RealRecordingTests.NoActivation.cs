// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// What <see cref="RecordOptions.NoActivation"/> promises, and what it costs when it cannot be kept.
/// </summary>
/// <remarks>
/// <c>winapp target record</c> tells the user that recording a target's desktop interrupts nothing:
/// it runs against a machine they are not looking at, through a host window they did not open. A
/// recording that quietly restored or foregrounded that window to get a usable frame would break
/// that promise at the worst possible moment — minutes into an unattended take, on top of whatever
/// the user was doing. These tests hold the two halves of the rule together: nothing is activated
/// under this option, and ordinary <c>winapp ui record</c>, which records an app the user is
/// watching, still behaves exactly as it did.
/// </remarks>
public partial class RealRecordingTests
{
    [TestMethod]
    public async Task RecordAsync_NoActivation_UsesTheStrictCaptureAndNeverTheBlankRetryOne()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        var output = ScratchOutput("strict.mp4");
        capture.CaptureWithoutActivationOverride = _ => Frame();
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, NoActivationOptions(output), CancellationToken.None);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(
            0,
            capture.CapturedWithBlankRetry.Count,
            "The blank-retry capture foregrounds the window, so this path must never reach it.");
        Assert.IsTrue(
            capture.CapturedWithoutActivation.Count >= 2,
            "Every frame, and the check that the window is capturable at all, goes through the strict path.");
    }

    /// <summary>
    /// The other half: <c>ui record</c> is unchanged, still recovering a blank <c>PrintWindow</c>
    /// frame from the foreground, because the user is standing in front of the app they asked to
    /// record and failing the recording would serve them worse.
    /// </summary>
    [TestMethod]
    public async Task RecordAsync_OrdinaryRecording_StillUsesTheBlankRetryCapture()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var svc = NewAutomation();
        var recording = NewRecordingService(svc, capture);
        var uiTarget = SessionFor(fx);
        var output = ScratchOutput("ordinary.mp4");
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(uiTarget, null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(1, capture.CapturedWithBlankRetry.Count);
        Assert.AreEqual(
            0,
            capture.CapturedWithoutActivation.Count,
            "An ordinary recording has no reason to take the strict path.");
    }

    [TestMethod]
    public async Task RecordAsync_NoActivation_MinimizedWindow_IsNeverRestored()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("minimized-strict.mp4");
        var restores = 0;
        UiRecordingService.s_isWindowMinimized = _ => true;
        UiRecordingService.s_restoreWindow = _ => restores++;

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => recording.RecordAsync(SessionFor(fx), null, NoActivationOptions(output), CancellationToken.None));

        Assert.AreEqual(0, restores, "Restoring puts the window back on the user's screen.");
        StringAssert.Contains(exception.Message, "minimized");
        Assert.IsFalse(File.Exists(output), "A recording that never started leaves nothing behind.");
    }

    [TestMethod]
    public async Task RecordAsync_OrdinaryRecording_MinimizedWindow_IsStillRestored()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("minimized-ordinary.mp4");
        var restores = 0;
        UiRecordingService.s_isWindowMinimized = _ => true;
        UiRecordingService.s_restoreWindow = _ => restores++;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 1,
            Fps = 1,
            MaxEdge = 64,
        }, CancellationToken.None);

        Assert.AreEqual(1, restores, "This is the behavior ui record has always had.");
        Assert.AreEqual(1, result.Frames);
    }

    [TestMethod]
    public async Task RecordAsync_NoActivation_NeverBringsTheWindowToTheForeground()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("no-foreground.mp4");
        var foregrounds = 0;
        UiRecordingService.s_bringToForeground = _ => foregrounds++;
        capture.CaptureWithoutActivationOverride = _ => Frame();
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(
            SessionFor(fx), null, NoActivationOptions(output), CancellationToken.None);

        Assert.AreEqual(1, result.Frames);
        Assert.AreEqual(0, foregrounds);
    }

    /// <summary>
    /// A window frame capture cannot see and <c>PrintWindow</c> returns blank for. The honest answer
    /// is that it was not recorded — not an all-black video reported as a successful take.
    /// </summary>
    [TestMethod]
    public async Task RecordAsync_NoActivation_WindowCannotBeCapturedWhereItStands_RecordsNothing()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("uncapturable.mp4");
        capture.CaptureWithoutActivationOverride = _ => null;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => recording.RecordAsync(SessionFor(fx), null, NoActivationOptions(output), CancellationToken.None));

        StringAssert.Contains(exception.Message, "could not be captured");
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
        Assert.IsFalse(
            File.Exists(output),
            "The window is checked before any output exists, so there is nothing to clean up.");
    }

    /// <summary>
    /// The same failure arriving partway through a take. Nine good seconds are worth keeping, so the
    /// recording ends and publishes rather than discarding what it already has.
    /// </summary>
    [TestMethod]
    public async Task RecordAsync_NoActivation_CaptureStopsMidTake_PublishesWhatItCaptured()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("mid-take.mp4");
        var calls = 0;

        // Call 1 is the pre-flight check, call 2 the first frame; the window goes blank after that.
        capture.CaptureWithoutActivationOverride = _ => ++calls <= 2 ? Frame() : null;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var result = await recording.RecordAsync(SessionFor(fx), null, new RecordOptions
        {
            OutputPath = output,
            DurationSec = 4,
            Fps = 2,
            MaxEdge = 64,
            NoActivation = true,
        }, CancellationToken.None);

        Assert.AreEqual(1, result.Frames, "The one frame that was captured is kept.");
        Assert.AreEqual("capture_unavailable", result.StopReason);
        Assert.AreEqual(0, capture.CapturedWithBlankRetry.Count);
    }

    /// <summary>
    /// Losing the window before a single frame lands is the uncapturable case, not a zero-frame
    /// success: an empty video would report that a recording of nothing had worked.
    /// </summary>
    [TestMethod]
    public async Task RecordAsync_NoActivation_CaptureStopsBeforeTheFirstFrame_RecordsNothing()
    {
        using var fx = new UiaTestFixture();
        var capture = new FakeWindowCapture { Supported = false };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("no-first-frame.mp4");
        var calls = 0;
        capture.CaptureWithoutActivationOverride = _ => ++calls <= 1 ? Frame() : null;
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => recording.RecordAsync(SessionFor(fx), null, NoActivationOptions(output), CancellationToken.None));

        StringAssert.Contains(exception.Message, "could not be captured");
    }

    [TestMethod]
    public async Task RecordAsync_NoActivation_WithScreenCapture_IsRejected()
    {
        using var fx = new UiaTestFixture();
        var recording = NewRecordingService(NewAutomation(), new FakeWindowCapture());
        var output = ScratchOutput("screen-conflict.mp4");

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => recording.RecordAsync(SessionFor(fx), null, new RecordOptions
            {
                OutputPath = output,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
                CaptureScreen = true,
                NoActivation = true,
            }, CancellationToken.None));

        StringAssert.Contains(exception.Message, "screen");
        Assert.IsFalse(File.Exists(output));
    }

    [TestMethod]
    public async Task RecordAsync_NoActivation_WithAnElementSelector_IsRejected()
    {
        using var fx = new UiaTestFixture();
        var recording = NewRecordingService(NewAutomation(), new FakeWindowCapture());
        var output = ScratchOutput("selector-conflict.mp4");

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => recording.RecordAsync(
                SessionFor(fx), "btnInvoke", NoActivationOptions(output), CancellationToken.None));

        StringAssert.Contains(exception.Message, "whole window");
    }

    /// <summary>
    /// Frame capture failing must not silently become screen capture here. Screen capture reads the
    /// user's display, where a window recorded in place is deliberately not visible — it would
    /// record their desktop and label it the target's.
    /// </summary>
    [TestMethod]
    public async Task RecordAsync_NoActivation_FrameCaptureInitFails_DoesNotFallBackToTheScreen()
    {
        using var fx = new UiaTestFixture();
        var screenCalls = 0;
        var capture = new FakeWindowCapture
        {
            Supported = true,
            StartGrabberCallback = (_, _) => throw new InvalidOperationException("simulated WGC init failure"),
            CaptureScreenOverride = (_, _, _, _, tw, th, _, _) =>
            {
                screenCalls++;
                return new byte[tw * th * 4];
            },
        };
        var recording = NewRecordingService(NewAutomation(), capture);
        var output = ScratchOutput("wgc-init-failure.mp4");
        capture.CaptureWithoutActivationOverride = _ => Frame();
        Mp4SinkWriterEncoder.s_createNoClobber = (path, width, height, _, _) =>
            new FakeVideoEncoder(path, width, height);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => recording.RecordAsync(SessionFor(fx), null, NoActivationOptions(output), CancellationToken.None));

        Assert.AreEqual(0, screenCalls);
    }

    private static RecordOptions NoActivationOptions(string outputPath) => new()
    {
        OutputPath = outputPath,
        DurationSec = 1,
        Fps = 1,
        MaxEdge = 64,
        NoActivation = true,
    };

    /// <summary>A usable captured frame, sized so the encoder has something real to scale.</summary>
    private static (byte[] Pixels, int Width, int Height) Frame()
        => (Enumerable.Repeat((byte)0x5A, 64 * 64 * 4).ToArray(), 64, 64);

    private static string ScratchOutput(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        return path;
    }
}
