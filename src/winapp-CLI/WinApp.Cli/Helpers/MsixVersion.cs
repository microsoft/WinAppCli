// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Represents a canonical four-part MSIX identity version where each component is an unsigned
/// 16-bit integer (<c>Major.Minor.Build.Revision</c>).
/// </summary>
internal readonly record struct MsixVersion
{
    public ushort Major { get; }
    public ushort Minor { get; }
    public ushort Build { get; }
    public ushort Revision { get; }

    public MsixVersion(ushort major, ushort minor, ushort build, ushort revision)
    {
        Major = major;
        Minor = minor;
        Build = build;
        Revision = revision;
    }

    /// <summary>
    /// Parses a canonical four-part MSIX version string.
    /// </summary>
    /// <param name="versionString">The version string to parse.</param>
    /// <param name="version">When successful, the parsed <see cref="MsixVersion"/>.</param>
    /// <returns><c>true</c> if the string is a valid four-part MSIX version; otherwise <c>false</c>.</returns>
    public static bool TryParse([NotNullWhen(true)] string? versionString, out MsixVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(versionString))
        {
            return false;
        }

        var trimmed = versionString.Trim();
        var parts = trimmed.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var build) ||
            !ushort.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
        {
            return false;
        }

        version = new MsixVersion(major, minor, build, revision);
        return true;
    }

    /// <summary>
    /// Parses a canonical four-part MSIX version string.
    /// </summary>
    /// <param name="versionString">The version string to parse.</param>
    /// <returns>The parsed <see cref="MsixVersion"/>.</returns>
    /// <exception cref="FormatException">The string is not a valid four-part MSIX version.</exception>
    public static MsixVersion Parse(string versionString)
    {
        if (!TryParse(versionString, out var version))
        {
            throw new FormatException($"'{versionString}' is not a canonical four-part MSIX version (e.g. 1.2.3.4).");
        }

        return version;
    }

    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}";
}
