// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using SkiaSharp;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class UiAutomationService
{
    // Minimum dimensions accepted by the Windows MF H.264 encoder.
    // Empirically, the encoder rejects frames narrower or shorter than 64 pixels with
    // COM error 0xC00D36B4 ("media type is invalid"). Frames smaller than this are
    // centered on a black letterbox canvas padded to the minimum, preserving aspect ratio.
    private const int MfH264MinWidth = 64;
    private const int MfH264MinHeight = 64;

    private sealed class RecordFrameArtifactSetup
    {
        public required RecordOptions Options { get; init; }
        public required DateTimeOffset StartedUtc { get; init; }
        public required int EncoderWidth { get; init; }
        public required int EncoderHeight { get; init; }
    }

    private RecordFrameArtifactCoordinator CreateRecordFrameArtifactCoordinator(
        RecordFrameArtifactSetup setup)
    {
        return RecordFrameArtifactCoordinator.Create(new RecordFrameBundleConfiguration
        {
            FinalDirectory = setup.Options.FramesDirectory!,
            VideoPath = setup.Options.OutputPath,
            StartedUtc = setup.StartedUtc,
            Width = setup.EncoderWidth,
            Height = setup.EncoderHeight,
            Requested = new RecordFrameRequestManifest
            {
                DurationSec = setup.Options.DurationSec,
                Fps = setup.Options.Fps,
                MaxEdge = setup.Options.MaxEdge,
            },
            Logger = _logger,
        });
    }

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

        int displayW, displayH;
        if (maxEdge > 0 && longest > maxEdge)
        {
            var evenMaxEdge = EvenFloor(maxEdge);
            if (width >= height)
            {
                displayW = evenMaxEdge;
                displayH = Math.Min(EvenRound(height * scale), evenMaxEdge);
            }
            else
            {
                displayH = evenMaxEdge;
                displayW = Math.Min(EvenRound(width * scale), evenMaxEdge);
            }
        }
        else
        {
            displayW = EvenRound(width * scale);
            displayH = EvenRound(height * scale);
        }

        var encoderW = Math.Max(displayW, MfH264MinWidth);
        var encoderH = Math.Max(displayH, MfH264MinHeight);
        return (encoderW, encoderH, displayW, displayH);

        static int EvenRound(double value)
            => Math.Max(2, (int)(Math.Round(value / 2.0, MidpointRounding.AwayFromZero) * 2));

        static int EvenFloor(int value)
            => Math.Max(2, value % 2 == 0 ? value : value - 1);
    }

    /// <summary>
    /// Crops and scales a captured BGRA frame to the encoder target dimensions (top-down output).
    /// When the display size is smaller than the encoder size, the content is centered on a black
    /// letterbox background rather than stretched.
    /// </summary>
    internal static byte[] ProcessFrame(
        byte[] source, int sourceWidth, int sourceHeight,
        int cropX, int cropY, int cropW, int cropH,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
    {
        if (!RequiresFrameTransform(
                sourceWidth,
                sourceHeight,
                cropX,
                cropY,
                cropW,
                cropH,
                encoderWidth,
                encoderHeight,
                displayWidth,
                displayHeight)
            && source.Length == encoderWidth * encoderHeight * 4)
        {
            return source;
        }

        (cropX, cropY, cropW, cropH) = ClampCropRect(
            cropX,
            cropY,
            cropW,
            cropH,
            sourceWidth,
            sourceHeight);

        var srcInfo = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var srcBitmap = new SKBitmap(srcInfo);
        Marshal.Copy(source, 0, srcBitmap.GetPixels(), Math.Min(source.Length, srcInfo.BytesSize));

        var dstInfo = new SKImageInfo(encoderWidth, encoderHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var dstBitmap = new SKBitmap(dstInfo);
        using (var canvas = new SKCanvas(dstBitmap))
        {
            canvas.Clear(SKColors.Black);
            var (offsetX, offsetY, fitW, fitH) = ComputeFittedContentRect(
                cropW, cropH, encoderWidth, encoderHeight, displayWidth, displayHeight);
            var srcRect = SKRect.Create(cropX, cropY, cropW, cropH);
            var dstRect = SKRect.Create(offsetX, offsetY, fitW, fitH);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
            canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);
        }

        var output = new byte[dstInfo.BytesSize];
        Marshal.Copy(dstBitmap.GetPixels(), output, 0, output.Length);
        return output;
    }

    internal static bool RequiresFrameTransform(
        int sourceWidth,
        int sourceHeight,
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        int encoderWidth,
        int encoderHeight,
        int displayWidth,
        int displayHeight)
        => cropX != 0
            || cropY != 0
            || cropW != sourceWidth
            || cropH != sourceHeight
            || encoderWidth != sourceWidth
            || encoderHeight != sourceHeight
            || displayWidth != encoderWidth
            || displayHeight != encoderHeight;

    internal static (int X, int Y, int Width, int Height) ClampCropRect(
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        int sourceWidth,
        int sourceHeight)
    {
        cropX = Math.Clamp(cropX, 0, Math.Max(0, sourceWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, sourceHeight - 1));
        cropW = Math.Clamp(cropW, 1, sourceWidth - cropX);
        cropH = Math.Clamp(cropH, 1, sourceHeight - cropY);
        return (cropX, cropY, cropW, cropH);
    }

    internal static (int OffsetX, int OffsetY, int FitW, int FitH) ComputeFittedContentRect(
        int cropW, int cropH, int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
    {
        var scale = Math.Min(displayWidth / (double)cropW, displayHeight / (double)cropH);
        var fitW = Math.Clamp((int)Math.Round(cropW * scale), 1, displayWidth);
        var fitH = Math.Clamp((int)Math.Round(cropH * scale), 1, displayHeight);
        var offsetX = (encoderWidth - fitW) / 2;
        var offsetY = (encoderHeight - fitH) / 2;
        return (offsetX, offsetY, fitW, fitH);
    }
}
