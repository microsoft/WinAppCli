// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Shared parser for screen-coordinate tokens in the <c>x,y</c> form reported by <c>ui inspect</c>.
/// </summary>
public static class CoordinateParser
{
    public static bool TryParsePoint(string? value, out PointerPoint point)
    {
        point = default;
        if (TryParsePoint(value, out int x, out int y))
        {
            point = new PointerPoint(x, y);
            return true;
        }

        return false;
    }

    public static bool TryParsePoint(string? value, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    /// <summary>
    /// Whether a token that failed to parse as a point was nonetheless meant as coordinates: it has a
    /// comma and its first field is an integer.
    /// </summary>
    public static bool LooksLikeCoordinates(string token)
    {
        var parts = token.Split(',');
        return parts.Length >= 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
