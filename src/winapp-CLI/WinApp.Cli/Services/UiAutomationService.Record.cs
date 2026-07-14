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
    public async Task<RecordCaptureResult> RecordAsync(UiSessionInfo session, string? elementId, RecordOptions options, CancellationToken ct, Action? onRecordingStarted = null)
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
                    grabber = WgcCapture.StartGrabber(hwnd, _logger, options.Fps);
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
                // Use the canonical single-element resolution path (same as ui click/hover) so that:
                //   - An ambiguous plain-text selector (multiple matches) → structured error with slug
                //     suggestions, not a silent first-match recording.
                //   - Element not found → UiElementNotFoundException (element_not_found error code).
                //   - Popup/child-window searching and slug validation are preserved.
                // No selector (elementId == null) → whole-window recording (by design, unchanged).
                var selectorExpr = _selectorService.Parse(elementId);
                var selectorElement = await FindSingleElementAsync(session, selectorExpr, ct).ConfigureAwait(false);
                if (selectorElement is null)
                {
                    throw new UiElementNotFoundException(elementId);
                }
                // Compute crop from the element's screen-space bounding rectangle.
                cropX = Math.Clamp((int)selectorElement.X - captureOriginLeft, 0, Math.Max(0, srcWidth - 1));
                cropY = Math.Clamp((int)selectorElement.Y - captureOriginTop, 0, Math.Max(0, srcHeight - 1));
                cropW = Math.Clamp((int)selectorElement.Width, 1, srcWidth - cropX);
                cropH = Math.Clamp((int)selectorElement.Height, 1, srcHeight - cropY);
            }

            var (encoderW, encoderH, displayW, displayH) = ComputeTargetSize(cropW, cropH, options.MaxEdge);
            var bitrate = (uint)Math.Clamp((long)encoderW * encoderH * options.Fps / 8, 1_000_000, 24_000_000);

            using var encoder = new Mp4SinkWriterEncoder(options.OutputPath, encoderW, encoderH, options.Fps, bitrate);

            var frameDurationHns = 10_000_000L / options.Fps;
            // Use long arithmetic to avoid int overflow for high fps × long duration combinations.
            var totalFrames = options.DurationSec > 0 ? (long)options.DurationSec * options.Fps : (long?)null;
            var stopwatch = Stopwatch.StartNew();
            var frameIndex = 0;
            var startedSignaled = false;

            while (!ct.IsCancellationRequested)
            {
                if (totalFrames.HasValue && frameIndex >= totalFrames.Value)
                {
                    break;
                }

                // Wall-clock deadline: also break when the requested wall time has elapsed, so slow
                // encoding (encoding takes longer than the sampling cadence) doesn't overshoot.
                if (totalFrames.HasValue && stopwatch.Elapsed.TotalSeconds >= options.DurationSec)
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

                var frame = ProcessFrame(source, sw, sh, cropX, cropY, cropW, cropH, encoderW, encoderH, displayW, displayH);
                encoder.WriteFrame(frame, frameIndex * frameDurationHns, frameDurationHns);
                frameIndex++;

                // Signal readiness after the first frame is encoded: the encoder is initialized
                // and live. Only now is it safe to arm the stdin-stop monitor and emit the
                // liveness event, so that a newline pre-buffered in stdin triggers a graceful
                // stop that finalizes a valid MP4 rather than canceling before the encoder exists.
                if (!startedSignaled)
                {
                    startedSignaled = true;
                    onRecordingStarted?.Invoke();
                }

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
                Width = encoderW,
                Height = encoderH,
                FileSize = fileSize,
                Mode = mode,
            };
        }
        finally
        {
            grabber?.Dispose();
        }
    }

    // Minimum dimensions accepted by the Windows MF H.264 encoder.
    // Empirically, the encoder rejects frames narrower or shorter than 64 pixels with
    // COM error 0xC00D36B4 ("media type is invalid"). Frames smaller than this are
    // centered on a black letterbox canvas padded to the minimum, preserving aspect ratio.
    private const int MfH264MinWidth = 64;
    private const int MfH264MinHeight = 64;

    /// <summary>
    /// Computes the encoder output size and display (content) size for the given crop dimensions.
    /// The encoder size is at least <see cref="MfH264MinWidth"/>×<see cref="MfH264MinHeight"/> to
    /// satisfy the Windows MF H.264 encoder's minimum requirements; if the content is smaller it is
    /// letterboxed (centered on a black background) rather than stretched.
    /// Both dimensions are always even (H.264 requirement).
    /// </summary>
    internal static (int EncoderW, int EncoderH, int DisplayW, int DisplayH) ComputeTargetSize(int width, int height, int maxEdge)
    {
        var scale = 1.0;
        var longest = Math.Max(width, height);
        if (maxEdge > 0 && longest > maxEdge)
        {
            scale = (double)maxEdge / longest;
        }

        // Round to the NEAREST even integer (not floor) to minimise aspect-ratio distortion.
        // Flooring odd-scaled dimensions (e.g. 300×10 → 100×2) can introduce large aspect-ratio
        // error; rounding to nearest-even keeps the error ≤ one half-pixel per side.
        var displayW = EvenRound(width * scale);
        var displayH = EvenRound(height * scale);

        // Pad up to the encoder minimum while preserving even dimensions.
        var encoderW = Math.Max(displayW, MfH264MinWidth);
        var encoderH = Math.Max(displayH, MfH264MinHeight);

        return (encoderW, encoderH, displayW, displayH);

        // Round a scaled double to the nearest even integer ≥ 2.
        static int EvenRound(double v)
            => Math.Max(2, (int)(Math.Round(v / 2.0, MidpointRounding.AwayFromZero) * 2));
    }

    /// <summary>
    /// Crops and scales a captured BGRA frame to the encoder target dimensions (top-down output).
    /// When the display size is smaller than the encoder size, the content is centered on a black
    /// letterbox background rather than stretched — aspect ratio is always preserved.
    /// </summary>
    internal static byte[] ProcessFrame(
        byte[] source, int sourceWidth, int sourceHeight,
        int cropX, int cropY, int cropW, int cropH,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
    {
        // Fast path: whole frame at native encoder size with no letterbox needed.
        if (cropX == 0 && cropY == 0 && cropW == sourceWidth && cropH == sourceHeight
            && encoderWidth == sourceWidth && encoderHeight == sourceHeight
            && displayWidth == encoderWidth && displayHeight == encoderHeight
            && source.Length == encoderWidth * encoderHeight * 4)
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

        var dstInfo = new SKImageInfo(encoderWidth, encoderHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var dstBitmap = new SKBitmap(dstInfo);
        using (var canvas = new SKCanvas(dstBitmap))
        {
            // Black letterbox background (covers padding regions when encoderW > displayW or encoderH > displayH).
            canvas.Clear(SKColors.Black);

            // Center the display content within the encoder canvas.
            var offsetX = (encoderWidth - displayWidth) / 2;
            var offsetY = (encoderHeight - displayHeight) / 2;

            var srcRect = SKRect.Create(cropX, cropY, cropW, cropH);
            var dstRect = SKRect.Create(offsetX, offsetY, displayWidth, displayHeight);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
            canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);
        }

        var output = new byte[dstInfo.BytesSize];
        Marshal.Copy(dstBitmap.GetPixels(), output, 0, output.Length);
        return output;
    }

}
