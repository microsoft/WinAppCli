// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

/// <summary>Options controlling an MP4 window/element recording.</summary>
public sealed class RecordOptions
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

    public string? FramesDirectory { get; init; }
}

/// <summary>Result of an MP4 recording.</summary>
public sealed class RecordCaptureResult
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

public sealed class RecordFrameArtifactResult
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

public sealed class RecordFrameSample
{
    public int SampleIndex { get; init; }
    public long ElapsedMs { get; init; }
    public double MediaTimeMs { get; init; }
}

public sealed class RecordFrameIndexEntry
{
    public int SampleIndex { get; init; }
    public long ElapsedMs { get; init; }
    public double MediaTimeMs { get; init; }
    public int ImageIndex { get; init; }
    public string File { get; init; } = "";
    public bool Changed { get; init; }
}

public sealed class RecordFrameBundleManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string Status { get; set; } = "complete";
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string StopReason { get; set; } = "duration_elapsed";
    public RecordFrameRequestManifest Requested { get; init; } = new();
    public RecordFrameTimingManifest Timing { get; set; } = new();
    public RecordFrameVideoManifest Video { get; set; } = new();
    public RecordFrameImagesManifest Frames { get; set; } = new();
}

public sealed class RecordFrameRequestManifest
{
    public int DurationSec { get; init; }
    public int Fps { get; init; }
    public int MaxEdge { get; init; }
}

public sealed class RecordFrameTimingManifest
{
    public long ElapsedMs { get; init; }
    public int SampleCount { get; init; }
    public int ImageCount { get; init; }
    public int RepeatedSampleCount { get; init; }
    public double AchievedFps { get; init; }
    public double CadenceRatio { get; init; }
}

public sealed class RecordFrameVideoManifest
{
    public string Path { get; init; } = "";
    public string Status { get; init; } = "complete";
    public string Codec { get; init; } = "h264";
    public int FrameCount { get; init; }
    public long? FileSize { get; init; }
}

public sealed class RecordFrameImagesManifest
{
    public string Format { get; init; } = "jpeg";
    public int Quality { get; init; } = 85;
    public string Index { get; init; } = "frames.ndjson";
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Truncated { get; init; }
    public long ByteLimit { get; init; }
}

public sealed class RecordPartialOutputException(
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

public sealed class RecordFrameOutputException(
    string message,
    string recoveryHint,
    Exception innerException)
    : IOException(message, innerException)
{
    public string RecoveryHint { get; } = recoveryHint;
}
