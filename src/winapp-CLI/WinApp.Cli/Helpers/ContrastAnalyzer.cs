// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Computes WCAG 2.x contrast ratios from captured pixels. Pure functions only — no UIA or
/// capture dependencies — so the logic is unit-testable with synthetic pixel buffers.
/// </summary>
internal static class ContrastAnalyzer
{
    /// <summary>
    /// A rectangle in buffer (pixel) coordinates.
    /// </summary>
    internal readonly record struct PixelRect(int X, int Y, int Width, int Height);

    /// <summary>Pixels with an alpha below this are treated as transparent and ignored.</summary>
    private const byte AlphaOpaqueThreshold = 250;

    /// <summary>
    /// A rect is "not measured" (returns null) unless at least this fraction of its pixels are
    /// opaque. Guards transparent/layered regions that would otherwise be scored as raw (often
    /// black) RGB.
    /// </summary>
    private const double MinOpaqueFraction = 0.40;

    /// <summary>Foreground (glyph) cluster must contain at least this many opaque pixels.</summary>
    private const int MinForegroundPixels = 8;

    /// <summary>Foreground (glyph) cluster must be at least this fraction of opaque pixels.</summary>
    private const double MinForegroundFraction = 0.005;

    /// <summary>
    /// Below this luminance spread the region is treated as effectively uniform (no measurable
    /// text), so no contrast is reported. Prevents solid fills collapsing to a fabricated ~1:1.
    /// </summary>
    private const double UniformLuminanceEpsilon = 1e-4;

    /// <summary>
    /// Estimate the WCAG contrast ratio between foreground (text) and background luminance
    /// within a region of a BGRA (32-bpp, top-down) pixel buffer.
    /// <para>
    /// Text glyphs are typically a minority of pixels within a text element's bounding box. This
    /// separates a background cluster from a foreground cluster (1D Otsu split over opaque-pixel
    /// luminance) and reports the ratio between their representative luminances. Alpha is honored:
    /// transparent/layered pixels are ignored, and a mostly-transparent rect is "not measured".
    /// </para>
    /// Returns <c>null</c> when the region cannot be meaningfully measured — degenerate/out-of-buffer,
    /// mostly transparent, effectively uniform (no glyphs), or the glyph cluster is too small to
    /// trust (sparse-glyph guard). This deliberately avoids fabricating a low ratio for sparse or
    /// transparent content.
    /// </summary>
    public static double? ComputeContrastRatio(ReadOnlySpan<byte> bgra, int width, int height, PixelRect rect)
    {
        if (width <= 0 || height <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }
        if ((long)width * height * 4 > bgra.Length)
        {
            return null;
        }

        // Clamp the region to the buffer bounds.
        var x0 = Math.Max(0, rect.X);
        var y0 = Math.Max(0, rect.Y);
        var x1 = Math.Min(width, rect.X + rect.Width);
        var y1 = Math.Min(height, rect.Y + rect.Height);
        if (x1 <= x0 || y1 <= y0)
        {
            return null;
        }

        var totalRectPixels = (x1 - x0) * (y1 - y0);

        // Collect luminances of opaque pixels only. Transparent/layered pixels are skipped so a
        // transparent overlay is never scored as its raw (often black) RGB.
        var luminances = new double[totalRectPixels];
        var opaqueCount = 0;
        for (var y = y0; y < y1; y++)
        {
            var rowOffset = y * width * 4;
            for (var x = x0; x < x1; x++)
            {
                var p = rowOffset + x * 4;
                // BGRA order.
                var b = bgra[p];
                var g = bgra[p + 1];
                var r = bgra[p + 2];
                var a = bgra[p + 3];
                if (a < AlphaOpaqueThreshold)
                {
                    continue;
                }
                luminances[opaqueCount++] = RelativeLuminance(r, g, b);
            }
        }

        // Mostly transparent (or fully) → not measured.
        if (opaqueCount == 0 || (double)opaqueCount / totalRectPixels < MinOpaqueFraction)
        {
            return null;
        }

        var opaque = luminances.AsSpan(0, opaqueCount);
        opaque.Sort();

        // Effectively uniform region (a solid fill, not text) → not measured.
        if (opaque[opaqueCount - 1] - opaque[0] < UniformLuminanceEpsilon)
        {
            return null;
        }

        // Separate a background cluster from a foreground (glyph) cluster via a 1D Otsu split that
        // maximizes between-class variance, then require the minority (glyph) cluster to clear a
        // small coverage floor. This stops short text from collapsing to a fabricated ~1:1.
        var (lowMean, lowCount, highMean, highCount) = OtsuSplit(opaque);
        var minorityCount = Math.Min(lowCount, highCount);
        var floor = Math.Max(MinForegroundPixels, (int)Math.Ceiling(opaqueCount * MinForegroundFraction));
        if (minorityCount < floor)
        {
            return null;
        }

        return ContrastRatio(lowMean, highMean);
    }

    /// <summary>
    /// 1D two-class split (Otsu) over a sorted span of luminances. Returns the mean and pixel
    /// count of the low and high clusters using the split index that maximizes between-class
    /// variance.
    /// </summary>
    private static (double LowMean, int LowCount, double HighMean, int HighCount) OtsuSplit(ReadOnlySpan<double> sorted)
    {
        var n = sorted.Length;
        var total = 0.0;
        for (var i = 0; i < n; i++)
        {
            total += sorted[i];
        }

        var sumLow = 0.0;
        var bestBetween = -1.0;
        var bestIdx = 0; // low cluster = [0..bestIdx]
        for (var i = 0; i < n - 1; i++)
        {
            sumLow += sorted[i];
            var wLow = i + 1;
            var wHigh = n - wLow;
            var meanLow = sumLow / wLow;
            var meanHigh = (total - sumLow) / wHigh;
            var diff = meanLow - meanHigh;
            var between = (double)wLow * wHigh * diff * diff;
            if (between > bestBetween)
            {
                bestBetween = between;
                bestIdx = i;
            }
        }

        var lowCount = bestIdx + 1;
        var highCount = n - lowCount;
        var lowSum = 0.0;
        for (var i = 0; i < lowCount; i++)
        {
            lowSum += sorted[i];
        }
        var lowMean = lowSum / lowCount;
        var highMean = (total - lowSum) / highCount;
        return (lowMean, lowCount, highMean, highCount);
    }

    /// <summary>
    /// WCAG contrast ratio between two relative luminances (order-independent).
    /// </summary>
    public static double ContrastRatio(double l1, double l2)
    {
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// WCAG relative luminance for an sRGB color (channels 0-255).
    /// </summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        var rl = Linearize(r / 255.0);
        var gl = Linearize(g / 255.0);
        var bl = Linearize(b / 255.0);
        return 0.2126 * rl + 0.7152 * gl + 0.0722 * bl;
    }

    private static double Linearize(double c)
        => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}
