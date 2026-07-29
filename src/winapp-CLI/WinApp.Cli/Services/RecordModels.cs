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

    /// <summary>Absolute final directory for agent-readable frame artifacts, or null when disabled.</summary>
    public string? FramesDirectory { get; init; }

    /// <summary>Original selector supplied by the caller, for artifact provenance.</summary>
    public string? Selector { get; init; }

    /// <summary>Optional progress callback. The recorder invokes it at most once every five seconds.</summary>
    public Action<RecordProgress>? OnProgress { get; init; }
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

    public long ElapsedMs { get; init; }
    public double AchievedFps { get; init; }
    public double CadenceRatio { get; init; }
    public string StopReason { get; init; } = "duration_elapsed";
    public RecordFrameArtifactResult? FrameArtifacts { get; init; }
    public string[]? Warnings { get; init; }
}

internal sealed class RecordProgress
{
    public long ElapsedMs { get; init; }
    public int Samples { get; init; }
    public int Images { get; init; }
    public double AchievedFps { get; init; }
}

internal sealed class RecordFrameArtifactResult
{
    public string Directory { get; init; } = "";
    public string Manifest { get; init; } = "";
    public string Index { get; init; } = "";
    public string Format { get; init; } = "jpeg";
    public int Quality { get; init; } = 85;
    public int Samples { get; init; }
    public int Images { get; init; }
    public int RepeatedSamples { get; init; }
    public long TotalBytes { get; init; }
    public bool Truncated { get; init; }
    public long ByteLimit { get; init; }
}

internal sealed class RecordFrameSample
{
    public int SampleIndex { get; init; }
    public long ElapsedMs { get; init; }
    public double MediaTimeMs { get; init; }
    public long? SourceVersion { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public RecordFrameRectManifest ContentRect { get; init; } = new();
}

internal sealed class RecordFrameIndexEntry
{
    public int SampleIndex { get; init; }
    public long ElapsedMs { get; init; }
    public double MediaTimeMs { get; init; }
    public int ImageIndex { get; init; }
    public string File { get; init; } = "";
    public bool Changed { get; init; }
    public string Sha256 { get; init; } = "";
    public long? SourceVersion { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public RecordFrameRectManifest ContentRect { get; init; } = new();
}

internal sealed class RecordFrameBundleManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Status { get; set; } = "complete";
    public string RecordingId { get; init; } = "";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string StopReason { get; set; } = "duration_elapsed";
    public RecordFrameRequestManifest Requested { get; init; } = new();
    public RecordFrameTimingManifest Timing { get; set; } = new();
    public RecordFrameVideoManifest Video { get; set; } = new();
    public RecordFrameImagesManifest Frames { get; set; } = new();
    public RecordFrameSourceManifest Source { get; init; } = new();
    public RecordFrameCropManifest Crop { get; init; } = new();
}

internal sealed class RecordFrameRequestManifest
{
    public int DurationSec { get; init; }
    public int Fps { get; init; }
    public int MaxEdge { get; init; }
    public string? Selector { get; init; }
    public bool CaptureScreen { get; init; }
}

internal sealed class RecordFrameTimingManifest
{
    public long ElapsedMs { get; init; }
    public int SampleCount { get; init; }
    public int ImageCount { get; init; }
    public int RepeatedSampleCount { get; init; }
    public double AchievedFps { get; init; }
    public double CadenceRatio { get; init; }
}

internal sealed class RecordFrameVideoManifest
{
    public string Path { get; init; } = "";
    public string Status { get; init; } = "complete";
    public string Codec { get; init; } = "h264";
    public int FrameCount { get; init; }
    public long? FileSize { get; init; }
}

internal sealed class RecordFrameImagesManifest
{
    public string Format { get; init; } = "jpeg";
    public int Quality { get; init; } = 85;
    public string Index { get; init; } = "frames.ndjson";
    public int Width { get; init; }
    public int Height { get; init; }
    public RecordFrameRectManifest ContentRect { get; init; } = new();
    public string HashAlgorithm { get; init; } = "sha256";
    public long TotalBytes { get; init; }
    public bool Truncated { get; init; }
    public long ByteLimit { get; init; }
}

internal sealed class RecordFrameSourceManifest
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string? WindowTitle { get; init; }
    public long SessionHwnd { get; init; }
    public long CaptureHwnd { get; init; }
    public string CaptureMode { get; init; } = "";
}

internal sealed class RecordFrameCropManifest
{
    public string Kind { get; init; } = "window";
    public string CoordinateSpace { get; init; } = "capture";
    public RecordFrameRectManifest Rect { get; init; } = new();
}

internal sealed class RecordFrameRectManifest
{
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

internal sealed class RecordPartialOutputException(
    string message,
    string? videoPath,
    string? framesDirectory,
    string recoveryHint,
    Exception innerException)
    : IOException(message, innerException)
{
    public string? VideoPath { get; } = videoPath;
    public string? FramesDirectory { get; } = framesDirectory;
    public string RecoveryHint { get; } = recoveryHint;
}

internal sealed class RecordFrameOutputException(
    string message,
    string recoveryHint,
    Exception innerException)
    : IOException(message, innerException)
{
    public string RecoveryHint { get; } = recoveryHint;
}

internal sealed class RecordFramePipelineLimitException(
    string message,
    bool lowerMaxEdgeCanHelp)
    : ArgumentOutOfRangeException("frameDimensions", message)
{
    public bool LowerMaxEdgeCanHelp { get; } = lowerMaxEdgeCanHelp;
}
