// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake <see cref="IWindowCapture"/> — lets the recording tests drive frame capture without a GPU
/// or a live Windows Graphics Capture session.
/// </summary>
internal sealed class FakeWindowCapture : IWindowCapture
{
    public bool Supported { get; set; } = true;

    public Func<nint, int, IFrameGrabber>? StartGrabberCallback { get; set; }

    public bool IsFrameCaptureSupported => Supported;

    public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
        => StartGrabberCallback is not null
            ? StartGrabberCallback(hwnd, fps)
            : throw new PlatformNotSupportedException("No frame grabber configured for this test.");
}
