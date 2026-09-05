// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32.Foundation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public class CaptureForegroundSafetyTests
{
    [TestCleanup]
    public void Cleanup()
    {
        ForegroundGuard.ResetNativeSeams();
        UiAutomationService.ResetNativeSeams();
    }

    [TestMethod]
    public async Task ScreenshotAsync_CaptureScreen_ThrowsWhenTargetIsNotForeground()
    {
        using var fx = new UiaTestFixture();
        var service = NewAutomationService();
        var target = TargetFor(fx);
        ForegroundGuard.s_getForegroundWindow = () => new HWND(0);

        await Assert.ThrowsExactlyAsync<ForegroundLostException>(
            () => service.ScreenshotAsync(target, null, captureScreen: true, focus: false, CancellationToken.None));
    }

    [TestMethod]
    public async Task RecordAsync_FirstScreenFrame_ThrowsWhenTargetIsNotForeground()
    {
        using var fx = new UiaTestFixture();
        var recorder = NewRecordingService();
        var target = TargetFor(fx);
        ForegroundGuard.s_getForegroundWindow = () => new HWND(0);

        await Assert.ThrowsExactlyAsync<ForegroundLostException>(
            () => recorder.RecordAsync(target, null, new RecordOptions
            {
                OutputPath = Path.Combine(AppContext.BaseDirectory, "coverage-scratch", Guid.NewGuid().ToString("N"), "foreground.mp4"),
                CaptureScreen = true,
                DurationSec = 1,
                Fps = 1,
                MaxEdge = 64,
            }, CancellationToken.None));
    }

    private static IUiAutomation NewAutomationService()
        => new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddWinAppUiAutomation()
            .BuildServiceProvider()
            .GetRequiredService<IUiAutomation>();

    private static IUiRecordingService NewRecordingService()
        => new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddWinAppUiAutomation()
            .AddSingleton<IWindowCapture, SafeFakeWindowCapture>()
            .AddWinAppUiRecording()
            .BuildServiceProvider()
            .GetRequiredService<IUiRecordingService>();

    private static UiTarget TargetFor(UiaTestFixture fx) => new()
    {
        ProcessId = fx.ProcessId,
        ProcessName = "WinApp.UIAutomation.Tests",
        WindowHandle = fx.Hwnd,
        WindowTitle = fx.Title,
        IsExplicitWindow = true,
    };

    private sealed class SafeFakeWindowCapture : IWindowCapture
    {
        public bool IsFrameCaptureSupported => false;

        public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
            => throw new InvalidOperationException("Foreground safety should fail before WGC starts.");

        public byte[] CaptureWindowPixels(nint hwnd, int width, int height)
            => new byte[Math.Max(0, width * height * 4)];

        public byte[] CaptureScreenPixels(
            int x, int y, int cropWidth, int cropHeight,
            int encoderWidth, int encoderHeight,
            int displayWidth, int displayHeight)
            => throw new InvalidOperationException("Foreground safety should fail before screen capture.");
    }
}
