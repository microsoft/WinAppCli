// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Pure geometry for fitting a captured region into a fixed output surface. Shared by the
/// screenshot scaler and by video recording, which must letterbox each frame into a constant
/// encoder size.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>
    /// Scales a <paramref name="cropW"/>×<paramref name="cropH"/> region to fit inside
    /// <paramref name="displayWidth"/>×<paramref name="displayHeight"/> while preserving aspect ratio,
    /// then centers it within an <paramref name="encoderWidth"/>×<paramref name="encoderHeight"/>
    /// surface. Returns the centered offset and the fitted size.
    /// </summary>
    public static (int OffsetX, int OffsetY, int FitW, int FitH) ComputeFittedContentRect(
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
