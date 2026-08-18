// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Services;

/// <summary>
/// Raised by an observational screenshot pass when the target can only be captured by restoring or
/// foregrounding it — a desktop-sensitive act that an <see cref="UiTurnMode.Observe"/> invocation is
/// not entitled to perform.
/// </summary>
/// <remarks>
/// The caller responds by discarding every buffered capture, escalating the <em>whole</em> invocation
/// to <see cref="UiTurnMode.DesktopExclusive"/>, and recapturing from the beginning (spec §6.5).
/// Escalating per-window instead would publish an image mixing pre- and post-escalation pixels.
/// </remarks>
internal sealed class DesktopEscalationRequiredException(string reason)
    : Exception($"This screenshot needs the desktop: {reason}")
{
    /// <summary>Why the desktop is needed, for verbose diagnostics.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// Screenshot capture methods: window/screen capture, pixel extraction, and element cropping.
/// </summary>
internal sealed partial class UiAutomationService
{
    internal static Func<Windows.Win32.Foundation.HWND, int, int, byte[]> s_captureFromWindow = CaptureFromWindow;
    internal static Func<int, int, int, int, int, int, byte[]> s_captureFromScreenScaled = CaptureFromScreenScaled;
    internal static Action<Windows.Win32.Foundation.HWND> s_foregroundWindowForBlankRetry = ForegroundWindowForBlankRetry;
    internal static Action<int> s_sleepForBlankRetry = Thread.Sleep;

    /// <remarks>
    /// Coverage ceiling (issue #630): this is a direct Win32 foreground request used only after a
    /// native PrintWindow blank frame. Tests cover callers through the injectable seam.
    /// <para>
    /// Coordination (issue #764): this bypasses <see cref="IDesktopForegroundService"/> only because it
    /// is an established test seam with an <c>HWND</c> signature. Its single caller
    /// (<see cref="CaptureFromWindowWithBlankRetryAsync"/>) invokes it inside a desktop section, so the
    /// foreground change is still serialized against every other coordinated process.
    /// </para>
    /// </remarks>
    private static void ForegroundWindowForBlankRetry(Windows.Win32.Foundation.HWND hwnd)
        => Windows.Win32.PInvoke.SetForegroundWindow(hwnd);

    /// <remarks>
    /// Coverage ceiling (issue #630): tests cover real WGC/screen/PrintWindow attempts and deterministic
    /// blank-retry/composition seams. Remaining lines require minimized/zero-size native HWND state,
    /// foreground policy transitions, WGC cancellation timing, or UIA elements without native handles
    /// that cannot be forced safely on the shared desktop.
    /// </remarks>
    public async Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(
        UiSessionInfo session,
        string? elementId,
        bool captureScreen,
        bool focus,
        IDesktopSection desktopSection,
        bool observeOnly,
        CancellationToken ct)
    {
        _logger.LogDebug("Taking screenshot of process {Pid} (captureScreen={CaptureScreen}, focus={Focus}, observeOnly={ObserveOnly})", session.ProcessId, captureScreen, focus, observeOnly);

        var root = GetRootElement(session);
        if (root is null)
        {
            throw new InvalidOperationException($"No UIA window found for {session.ProcessName} (PID {session.ProcessId}).");
        }

        // Get the actual window title from UIA (not session cache, which may be stale)
        var rootName = SafeGetBstr(() => root.get_CurrentName());
        if (rootName is not null)
        {
            session.WindowTitle = rootName;
        }

        var hwnd = root.get_CurrentNativeWindowHandle();
        if (hwnd.IsNull && session.WindowHandle != 0)
        {
            // UIA element may lack a native handle (e.g. Electron content pane),
            // but the session already has a validated HWND from -w flag or window enumeration.
            hwnd = new Windows.Win32.Foundation.HWND((nint)session.WindowHandle);
            _logger.LogDebug("UIA element has no native handle; using session HWND {Hwnd}", session.WindowHandle);
        }
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException($"No native window handle for {session.ProcessName}. Is the window visible?");
        }

        var handle = (long)(nint)hwnd;

        // Restoring a minimized window is desktop-sensitive: it changes what the user sees and can take
        // the foreground. An observational pass reports the need and lets the caller escalate rather
        // than quietly disturbing another workflow's desktop (spec §6.5).
        if (_desktopForeground.IsMinimized(handle))
        {
            if (observeOnly)
            {
                throw new DesktopEscalationRequiredException("the target window is minimized and must be restored");
            }

            await using var restoreSection = await desktopSection.EnterAsync(ct).ConfigureAwait(false);
            _desktopForeground.Restore(handle);
            Thread.Sleep(300);
        }

        // Get window dimensions
        Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect);
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Window has zero size. Is it minimized?");
        }

        byte[] pixelData;
        var cropOriginLeft = rect.left;
        var cropOriginTop = rect.top;

        // --focus and --capture-screen both require the foreground, so they are classified
        // DesktopExclusive up front and never reach the observational path.
        if (focus || captureScreen)
        {
            if (observeOnly)
            {
                throw new DesktopEscalationRequiredException("the requested capture mode needs the target in the foreground");
            }

            // Bring window to foreground when explicitly requested or implied by --capture-screen.
            // Done exactly once here, regardless of capture path. The screen-DC BitBlt below reads the
            // live screen, so it stays inside the same section.
            await using var foregroundSection = await desktopSection.EnterAsync(ct).ConfigureAwait(false);
            _desktopForeground.RequestForeground(handle);
            await Task.Delay(focus ? 150 : 100, ct).ConfigureAwait(false);

            if (captureScreen)
            {
                // Screen capture mode: BitBlt from screen DC — captures popups and overlays.
                pixelData = CaptureFromScreen(rect.left, rect.top, width, height);
                return CropIfRequested(pixelData, width, height, elementId, session, root, cropOriginLeft, cropOriginTop);
            }
        }

        if (WgcCapture.IsSupported())
        {
            try
            {
                var visibleRect = GetVisibleWindowRect(hwnd, rect);
                var result = await WgcCapture.CaptureAsync(hwnd, _logger, ct).ConfigureAwait(false);
                pixelData = result.Pixels;
                width = result.Width;
                height = result.Height;
                cropOriginLeft = visibleRect.left;
                cropOriginTop = visibleRect.top;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WGC capture failed; falling back to PrintWindow");
                pixelData = await CaptureFromWindowWithBlankRetryAsync(hwnd, width, height, desktopSection, observeOnly, ct).ConfigureAwait(false);
            }
        }
        else
        {
            pixelData = await CaptureFromWindowWithBlankRetryAsync(hwnd, width, height, desktopSection, observeOnly, ct).ConfigureAwait(false);
        }

        return CropIfRequested(pixelData, width, height, elementId, session, root, cropOriginLeft, cropOriginTop);
    }

    private (byte[] Pixels, int Width, int Height) CropIfRequested(
        byte[] pixelData, int width, int height, string? elementId,
        UiSessionInfo session, IUIAutomationElement root, int cropOriginLeft, int cropOriginTop)
    {
        // If a selector was provided, crop to the element's bounding rectangle
        if (!string.IsNullOrEmpty(elementId))
        {
            var cropped = CropToElement(pixelData, width, height, elementId, session, root, cropOriginLeft, cropOriginTop);
            if (cropped is not null)
            {
                return cropped.Value;
            }
        }

        return (pixelData, width, height);
    }


    private static unsafe Windows.Win32.Foundation.RECT GetVisibleWindowRect(
        Windows.Win32.Foundation.HWND hwnd,
        Windows.Win32.Foundation.RECT fallbackRect)
    {
        var visibleRect = fallbackRect;
        var hr = Windows.Win32.PInvoke.DwmGetWindowAttribute(
            hwnd,
            Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &visibleRect,
            (uint)sizeof(Windows.Win32.Foundation.RECT));

        return hr.Succeeded ? visibleRect : fallbackRect;
    }

    internal async Task<byte[]> CaptureFromWindowWithBlankRetryAsync(
        Windows.Win32.Foundation.HWND hwnd, int width, int height,
        IDesktopSection desktopSection, bool observeOnly, CancellationToken ct)
    {
        var pixels = s_captureFromWindow(hwnd, width, height);
        if (!IsBlankCapture(pixels))
        {
            return pixels;
        }

        // A blank PrintWindow frame can only be recovered by foregrounding the window, which is
        // desktop-sensitive. An observational pass reports it so the caller can escalate the whole
        // invocation rather than publishing a black image or stealing focus (spec §6.5).
        if (observeOnly)
        {
            throw new DesktopEscalationRequiredException(
                "the target rendered a blank frame and must be foregrounded to capture it");
        }

        _logger.LogDebug("PrintWindow returned blank frame; foregrounding and retrying");
        await using (await desktopSection.EnterAsync(ct).ConfigureAwait(false))
        {
            s_foregroundWindowForBlankRetry(hwnd);
            s_sleepForBlankRetry(200);
            pixels = s_captureFromWindow(hwnd, width, height);
        }

        return pixels;
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): this is the innermost GDI/PrintWindow capture boundary. Tests
    /// cover the blank-retry and caller orchestration through seams; the native DC/bitmap handles are
    /// only safe to exercise against a real visible window.
    /// </remarks>
    private static unsafe byte[] CaptureFromWindow(Windows.Win32.Foundation.HWND hwnd, int width, int height)
    {
        var hdcWindow = Windows.Win32.PInvoke.GetDC(hwnd);
        try
        {
            var hdcMem = Windows.Win32.PInvoke.CreateCompatibleDC(hdcWindow);
            try
            {
                var hBitmap = Windows.Win32.PInvoke.CreateCompatibleBitmap(hdcWindow, width, height);
                try
                {
                    var hOld = Windows.Win32.PInvoke.SelectObject(hdcMem, *(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);

                    // PW_RENDERFULLCONTENT = 2
                    Windows.Win32.PInvoke.PrintWindow(hwnd, hdcMem, (Windows.Win32.Storage.Xps.PRINT_WINDOW_FLAGS)2);

                    Windows.Win32.PInvoke.SelectObject(hdcMem, hOld);

                    return ExtractPixels(hdcWindow, hBitmap, width, height);
                }
                finally
                {
                    Windows.Win32.PInvoke.DeleteObject(*(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
                }
            }
            finally
            {
                Windows.Win32.PInvoke.DeleteDC(hdcMem);
            }
        }
        finally
        {
            Windows.Win32.PInvoke.ReleaseDC(hwnd, hdcWindow);
        }
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): this is the innermost screen-DC BitBlt boundary. It reads the
    /// shared desktop and is intentionally covered only by gated real capture tests.
    /// </remarks>
    private static unsafe byte[] CaptureFromScreen(int x, int y, int width, int height)
    {
        var hdcScreen = Windows.Win32.PInvoke.GetDC(Windows.Win32.Foundation.HWND.Null);
        try
        {
            var hdcMem = Windows.Win32.PInvoke.CreateCompatibleDC(hdcScreen);
            try
            {
                var hBitmap = Windows.Win32.PInvoke.CreateCompatibleBitmap(hdcScreen, width, height);
                try
                {
                    var hOld = Windows.Win32.PInvoke.SelectObject(hdcMem, *(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);

                    // BitBlt from screen at the window's position
                    Windows.Win32.PInvoke.BitBlt(hdcMem, 0, 0, width, height,
                        hdcScreen, x, y, Windows.Win32.Graphics.Gdi.ROP_CODE.SRCCOPY);

                    Windows.Win32.PInvoke.SelectObject(hdcMem, hOld);

                    return ExtractPixels(hdcScreen, hBitmap, width, height);
                }
                finally
                {
                    Windows.Win32.PInvoke.DeleteObject(*(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
                }
            }
            finally
            {
                Windows.Win32.PInvoke.DeleteDC(hdcMem);
            }
        }
        finally
        {
            Windows.Win32.PInvoke.ReleaseDC(Windows.Win32.Foundation.HWND.Null, hdcScreen);
        }
    }

    internal static byte[] CaptureScreenFrame(
        int x, int y, int cropWidth, int cropHeight,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
    {
        var (offsetX, offsetY, fitW, fitH) = ComputeFittedContentRect(
            cropWidth, cropHeight, encoderWidth, encoderHeight, displayWidth, displayHeight);
        var content = s_captureFromScreenScaled(x, y, cropWidth, cropHeight, fitW, fitH);
        if (offsetX == 0 && offsetY == 0 && fitW == encoderWidth && fitH == encoderHeight)
        {
            return content;
        }

        var frame = new byte[encoderWidth * encoderHeight * 4];
        var sourceStride = fitW * 4;
        var destinationStride = encoderWidth * 4;
        for (var row = 0; row < fitH; row++)
        {
            Buffer.BlockCopy(
                content,
                row * sourceStride,
                frame,
                ((offsetY + row) * destinationStride) + (offsetX * 4),
                sourceStride);
        }
        return frame;
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): this is the innermost scaled screen-DC StretchBlt boundary.
    /// Deterministic tests cover letterbox composition through a seam; native readback is gated to
    /// interactive capture hosts.
    /// </remarks>
    private static unsafe byte[] CaptureFromScreenScaled(int x, int y, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var hdcScreen = Windows.Win32.PInvoke.GetDC(Windows.Win32.Foundation.HWND.Null);
        try
        {
            var hdcMem = Windows.Win32.PInvoke.CreateCompatibleDC(hdcScreen);
            try
            {
                var hBitmap = Windows.Win32.PInvoke.CreateCompatibleBitmap(hdcScreen, targetWidth, targetHeight);
                try
                {
                    var hOld = Windows.Win32.PInvoke.SelectObject(hdcMem, *(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
                    try
                    {
                        _ = Windows.Win32.PInvoke.SetStretchBltMode(hdcMem, Windows.Win32.Graphics.Gdi.STRETCH_BLT_MODE.HALFTONE);
                        Windows.Win32.PInvoke.StretchBlt(
                            hdcMem, 0, 0, targetWidth, targetHeight,
                            hdcScreen, x, y, sourceWidth, sourceHeight,
                            Windows.Win32.Graphics.Gdi.ROP_CODE.SRCCOPY);
                    }
                    finally
                    {
                        Windows.Win32.PInvoke.SelectObject(hdcMem, hOld);
                    }

                    return ExtractPixels(hdcScreen, hBitmap, targetWidth, targetHeight);
                }
                finally
                {
                    Windows.Win32.PInvoke.DeleteObject(*(Windows.Win32.Graphics.Gdi.HGDIOBJ*)&hBitmap);
                }
            }
            finally
            {
                Windows.Win32.PInvoke.DeleteDC(hdcMem);
            }
        }
        finally
        {
            Windows.Win32.PInvoke.ReleaseDC(Windows.Win32.Foundation.HWND.Null, hdcScreen);
        }
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): this is the innermost GetDIBits extraction from a native HBITMAP.
    /// It is covered indirectly by real screenshot attempts and cannot be executed with managed-only
    /// fakes without fabricating native GDI handles.
    /// </remarks>
    private static unsafe byte[] ExtractPixels(Windows.Win32.Graphics.Gdi.HDC hdc, Windows.Win32.Graphics.Gdi.HBITMAP hBitmap, int width, int height)
    {
        var bmi = new Windows.Win32.Graphics.Gdi.BITMAPINFO
        {
            bmiHeader = new Windows.Win32.Graphics.Gdi.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(Windows.Win32.Graphics.Gdi.BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        var pixelData = new byte[width * height * 4];
        fixed (byte* pPixels = pixelData)
        {
            Windows.Win32.PInvoke.GetDIBits(hdc, hBitmap, 0, (uint)height, pPixels, &bmi,
                Windows.Win32.Graphics.Gdi.DIB_USAGE.DIB_RGB_COLORS);
        }

        return pixelData;
    }

    internal static bool IsBlankCapture(byte[] pixels)
    {
        // Check if all pixels are zero (black/unrendered frame).
        // Use int-sized chunks for speed on large buffers.
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(pixels.AsSpan());
        foreach (var chunk in span)
        {
            if (chunk != 0)
            {
                return false;
            }
        }
        // Check remaining bytes
        for (var i = span.Length * sizeof(long); i < pixels.Length; i++)
        {
            if (pixels[i] != 0)
            {
                return false;
            }
        }
        return true;
    }

    /// <remarks>
    /// Coverage ceiling (issue #630): real screenshot tests cover element cropping for normal controls.
    /// Remaining branches require stale/missing UIA selector resolution or off-surface native bounding
    /// rectangles, which would need unsafe COM/provider fault injection or desktop mutation.
    /// </remarks>
    private (byte[] Pixels, int Width, int Height)? CropToElement(
        byte[] fullPixels, int fullWidth, int fullHeight,
        string selector, UiSessionInfo session, IUIAutomationElement root,
        int windowLeft, int windowTop)
    {
        // Find the element — try slug first, then legacy selector
        IUIAutomationElement? target = null;

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
                target = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
            }
        }

        if (target is null)
        {
            return null;
        }

        var elRect = target.get_CurrentBoundingRectangle();
        var cropX = Math.Max(0, elRect.left - windowLeft);
        var cropY = Math.Max(0, elRect.top - windowTop);
        var cropW = Math.Min(elRect.right - elRect.left, fullWidth - cropX);
        var cropH = Math.Min(elRect.bottom - elRect.top, fullHeight - cropY);

        if (cropW <= 0 || cropH <= 0)
        {
            return null;
        }

        var croppedPixels = new byte[cropW * cropH * 4];
        for (var row = 0; row < cropH; row++)
        {
            var srcOffset = ((cropY + row) * fullWidth + cropX) * 4;
            var dstOffset = row * cropW * 4;
            Array.Copy(fullPixels, srcOffset, croppedPixels, dstOffset, cropW * 4);
        }

        return (croppedPixels, cropW, cropH);
    }
}
