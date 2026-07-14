// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Video recording: captures window/element frames at a fixed cadence and encodes
/// them incrementally to an H.264 MP4 via Media Foundation (<see cref="Mp4SinkWriterEncoder"/>).
/// </summary>
internal sealed partial class UiAutomationService
{
    public async Task<RecordCaptureResult> RecordAsync(UiSessionInfo session, string? elementId, RecordOptions options, CancellationToken ct)
    {
        _logger.LogDebug("Recording process {Pid} (duration={Dur}s, fps={Fps}, maxEdge={MaxEdge}, captureScreen={Screen})",
            session.ProcessId, options.DurationSec, options.Fps, options.MaxEdge, options.CaptureScreen);

        var root = GetRootElement(session);
        if (root is null)
        {
            throw new InvalidOperationException($"No UIA window found for {session.ProcessName} (PID {session.ProcessId}).");
        }

        var rootName = SafeGetBstr(() => root.get_CurrentName());
        if (rootName is not null)
        {
            session.WindowTitle = rootName;
        }

        var hwnd = root.get_CurrentNativeWindowHandle();
        if (hwnd.IsNull && session.WindowHandle != 0)
        {
            hwnd = new HWND((nint)session.WindowHandle);
        }
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException($"No native window handle for {session.ProcessName}. Is the window visible?");
        }

        if (Windows.Win32.PInvoke.IsIconic(hwnd))
        {
            Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        // Bring to foreground for screen-DC capture.
        if (options.CaptureScreen)
        {
            Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect);
        if (rect.right - rect.left <= 0 || rect.bottom - rect.top <= 0)
        {
            throw new InvalidOperationException("Window has zero size. Is it minimized?");
        }

        var useScreen = options.CaptureScreen;
        var useWgc = !useScreen && WgcCapture.IsSupported();

        WgcCapture.FrameGrabber? grabber = null;
        var mode = useScreen ? "screen" : (useWgc ? "wgc" : "printwindow");

        try
        {
            int srcWidth;
            int srcHeight;
            int captureOriginLeft;
            int captureOriginTop;

            if (useWgc)
            {
                try
                {
                    grabber = WgcCapture.StartGrabber(hwnd, _logger);
                    if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("Timed out waiting for the first captured frame.");
                    }
                    var first = grabber.TryGetLatest()!.Value;
                    srcWidth = first.Width;
                    srcHeight = first.Height;
                    var visibleRect = GetVisibleWindowRect(hwnd, rect);
                    captureOriginLeft = visibleRect.left;
                    captureOriginTop = visibleRect.top;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "WGC recorder init failed; falling back to screen-DC capture");
                    grabber?.Dispose();
                    grabber = null;
                    useScreen = true;
                    useWgc = false;
                    mode = "screen-fallback";
                    srcWidth = rect.right - rect.left;
                    srcHeight = rect.bottom - rect.top;
                    captureOriginLeft = rect.left;
                    captureOriginTop = rect.top;
                }
            }
            else
            {
                srcWidth = rect.right - rect.left;
                srcHeight = rect.bottom - rect.top;
                captureOriginLeft = rect.left;
                captureOriginTop = rect.top;
            }

            // Determine the crop rectangle (in capture-space) for an element selector.
            var cropX = 0;
            var cropY = 0;
            var cropW = srcWidth;
            var cropH = srcHeight;
            if (!string.IsNullOrEmpty(elementId))
            {
                var elRect = TryResolveElementRect(elementId, session, root);
                if (elRect is { } er)
                {
                    cropX = Math.Clamp(er.left - captureOriginLeft, 0, Math.Max(0, srcWidth - 1));
                    cropY = Math.Clamp(er.top - captureOriginTop, 0, Math.Max(0, srcHeight - 1));
                    cropW = Math.Clamp(er.right - er.left, 1, srcWidth - cropX);
                    cropH = Math.Clamp(er.bottom - er.top, 1, srcHeight - cropY);
                }
                else
                {
                    _logger.LogWarning("Element '{Selector}' not found; recording the whole window instead.", elementId);
                }
            }

            var (targetW, targetH) = ComputeTargetSize(cropW, cropH, options.MaxEdge);
            var bitrate = (uint)Math.Clamp((long)targetW * targetH * options.Fps / 8, 1_000_000, 24_000_000);

            using var encoder = new Mp4SinkWriterEncoder(options.OutputPath, targetW, targetH, options.Fps, bitrate);

            var frameDurationHns = 10_000_000L / options.Fps;
            var totalFrames = options.DurationSec > 0 ? options.DurationSec * options.Fps : (int?)null;
            var stopwatch = Stopwatch.StartNew();
            var frameIndex = 0;

            while (!ct.IsCancellationRequested)
            {
                if (totalFrames.HasValue && frameIndex >= totalFrames.Value)
                {
                    break;
                }

                byte[]? source;
                int sw, sh;
                if (useWgc)
                {
                    var latest = grabber!.TryGetLatest();
                    if (latest is null)
                    {
                        await Task.Delay(5, ct).ConfigureAwait(false);
                        continue;
                    }
                    (source, sw, sh) = latest.Value;
                }
                else if (useScreen)
                {
                    source = CaptureFromScreen(captureOriginLeft, captureOriginTop, srcWidth, srcHeight);
                    sw = srcWidth;
                    sh = srcHeight;
                }
                else
                {
                    // PrintWindow fallback (WGC unsupported, not --capture-screen): render the window
                    // into an offscreen DC. Unlike BitBlt-from-screen this excludes occluding windows,
                    // matching `ui screenshot`. captureOrigin is the window's top-left so crop math holds.
                    source = CaptureFromWindowWithBlankRetry(hwnd, srcWidth, srcHeight);
                    sw = srcWidth;
                    sh = srcHeight;
                }

                var frame = ProcessFrame(source, sw, sh, cropX, cropY, cropW, cropH, targetW, targetH);
                encoder.WriteFrame(frame, frameIndex * frameDurationHns, frameDurationHns);
                frameIndex++;

                var targetMs = frameIndex * 1000.0 / options.Fps;
                var delayMs = targetMs - stopwatch.Elapsed.TotalMilliseconds;
                if (delayMs > 1)
                {
                    try
                    {
                        await Task.Delay((int)delayMs, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            encoder.Complete();

            var fileSize = new FileInfo(options.OutputPath).Length;
            return new RecordCaptureResult
            {
                Frames = frameIndex,
                Width = targetW,
                Height = targetH,
                FileSize = fileSize,
                Mode = mode,
            };
        }
        finally
        {
            grabber?.Dispose();
        }
    }

    /// <summary>Computes an even (H.264-safe) target size, downscaled so the longest edge is ≤ maxEdge (0 = no downscale).</summary>
    private static (int Width, int Height) ComputeTargetSize(int width, int height, int maxEdge)
    {
        var scale = 1.0;
        var longest = Math.Max(width, height);
        if (maxEdge > 0 && longest > maxEdge)
        {
            scale = (double)maxEdge / longest;
        }

        var w = EvenClamp((int)Math.Round(width * scale));
        var h = EvenClamp((int)Math.Round(height * scale));
        return (w, h);

        static int EvenClamp(int v) => Math.Max(2, v & ~1);
    }

    /// <summary>Crops and scales a captured BGRA frame to the target dimensions (top-down output).</summary>
    private static byte[] ProcessFrame(
        byte[] source, int sourceWidth, int sourceHeight,
        int cropX, int cropY, int cropW, int cropH,
        int targetWidth, int targetHeight)
    {
        // Fast path: whole frame at native size.
        if (cropX == 0 && cropY == 0 && cropW == sourceWidth && cropH == sourceHeight
            && targetWidth == sourceWidth && targetHeight == sourceHeight
            && source.Length == targetWidth * targetHeight * 4)
        {
            return source;
        }

        // Clamp crop against the actual frame in case dimensions drifted between frames.
        cropX = Math.Clamp(cropX, 0, Math.Max(0, sourceWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, sourceHeight - 1));
        cropW = Math.Clamp(cropW, 1, sourceWidth - cropX);
        cropH = Math.Clamp(cropH, 1, sourceHeight - cropY);

        var srcInfo = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var srcBitmap = new SKBitmap(srcInfo);
        Marshal.Copy(source, 0, srcBitmap.GetPixels(), Math.Min(source.Length, srcInfo.BytesSize));

        var dstInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var dstBitmap = new SKBitmap(dstInfo);
        using (var canvas = new SKCanvas(dstBitmap))
        {
            var srcRect = SKRect.Create(cropX, cropY, cropW, cropH);
            var dstRect = SKRect.Create(0, 0, targetWidth, targetHeight);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
            canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);
        }

        var output = new byte[dstInfo.BytesSize];
        Marshal.Copy(dstBitmap.GetPixels(), output, 0, output.Length);
        return output;
    }

    /// <summary>Resolves an element selector to its screen-space bounding rectangle, or null if not found.</summary>
    private RECT? TryResolveElementRect(string selector, UiSessionInfo session, IUIAutomationElement root)
    {
        Windows.Win32.UI.Accessibility.IUIAutomationElement? target = null;

        var slugParsed = SlugGenerator.ParseSlug(selector);
        if (slugParsed is not null)
        {
            var slugResult = FindElementBySlug(selector, root);
            if (slugResult is not null)
            {
                target = ResolveComElement(session, slugResult);
            }
        }
        else
        {
            var parsed = _selectorService.Parse(selector);
            var condition = BuildCondition(parsed);
            if (condition is not null)
            {
                target = root.FindFirst(Windows.Win32.UI.Accessibility.TreeScope.TreeScope_Descendants, condition);
            }
        }

        if (target is null)
        {
            return null;
        }

        return target.get_CurrentBoundingRectangle();
    }
}
