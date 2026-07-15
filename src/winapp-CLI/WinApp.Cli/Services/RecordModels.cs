// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>Options controlling an MP4 window/element recording.</summary>
internal sealed class RecordOptions
{
    /// <summary>Absolute output path for the .mp4 file.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Recording duration in seconds. 0 = record until cancellation (Ctrl+C).</summary>
    public int DurationSec { get; init; }

    /// <summary>Target frames per second.</summary>
    public int Fps { get; init; } = 15;

    /// <summary>Downscale so the longest edge is at most this many pixels. 0 = no downscale.</summary>
    public int MaxEdge { get; init; }

    /// <summary>Capture from the screen DC (BitBlt) so overlays/popups are included.</summary>
    public bool CaptureScreen { get; init; }
}

/// <summary>Result of an MP4 recording.</summary>
internal sealed class RecordCaptureResult
{
    public int Frames { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long FileSize { get; init; }

    /// <summary>Capture mode actually used: "wgc" (Windows Graphics Capture), "screen" (explicit --capture-screen), or "printwindow".</summary>
    public string Mode { get; init; } = "wgc";
}
