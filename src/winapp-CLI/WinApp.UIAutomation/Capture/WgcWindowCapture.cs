// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Production <see cref="IWindowCapture"/> — delegates to the Windows Graphics Capture helpers so
/// the capture pipeline stays the single source of truth.
/// </summary>
internal sealed class WgcWindowCapture(ILogger<WgcWindowCapture> logger) : IWindowCapture
{
    public bool IsFrameCaptureSupported => WgcCapture.IsSupported();

    public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
        => WgcCapture.s_startGrabber(new global::Windows.Win32.Foundation.HWND(hwnd), logger, fps);
}
