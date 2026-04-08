// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Screenshot capture methods: window/screen capture, pixel extraction, and element cropping.
/// </summary>
internal sealed partial class UiAutomationService
{
    public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, bool captureScreen, CancellationToken ct)
    {
        _logger.LogDebug("Taking screenshot of process {Pid} (captureScreen={CaptureScreen})", session.ProcessId, captureScreen);

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

        // Check if window is minimized
        if (Windows.Win32.PInvoke.IsIconic(hwnd))
        {
            Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
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

        if (captureScreen)
        {
            // Screen capture mode: BitBlt from screen DC — captures popups and overlays
            // Bring window to foreground first to avoid capturing other windows
            Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
            Thread.Sleep(100);

            pixelData = CaptureFromScreen(rect.left, rect.top, width, height);
        }
        else
        {
            // Window render mode: PrintWindow — works even when occluded
            pixelData = CaptureFromWindow(hwnd, width, height);

            // If capture is blank (all zeros), the window may not have rendered yet.
            // Activate it and retry — common with Electron on first launch.
            if (IsBlankCapture(pixelData))
            {
                _logger.LogDebug("PrintWindow returned blank frame; foregrounding and retrying");
                Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
                Thread.Sleep(200);
                pixelData = CaptureFromWindow(hwnd, width, height);
            }
        }

        // If a selector was provided, crop to the element's bounding rectangle
        if (!string.IsNullOrEmpty(elementId))
        {
            var cropped = CropToElement(pixelData, width, height, elementId, session, root, rect.left, rect.top);
            if (cropped is not null)
            {
                return Task.FromResult(cropped.Value);
            }
        }

        return Task.FromResult((pixelData, width, height));
    }

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

    private static bool IsBlankCapture(byte[] pixels)
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
