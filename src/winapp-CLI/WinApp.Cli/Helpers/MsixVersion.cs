// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

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

        // ST_VersionQuad XSD pattern validates exactly 4 parts separated by dots, from 0 to 65535, no leading zeroes except for 0 itself.
        const string pattern = @"\A(0|[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5])(\.(0|[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5])){3}\z";

        if (!Regex.IsMatch(versionString, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            return false;
        }

        // The regex from above ensures we have exactly 4 parts, and each part is a valid ushort, so we can safely parse them.
        var parts = versionString.Split('.');
        var major = ushort.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = ushort.Parse(parts[1], CultureInfo.InvariantCulture);
        var build = ushort.Parse(parts[2], CultureInfo.InvariantCulture);
        var revision = ushort.Parse(parts[3], CultureInfo.InvariantCulture);

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
