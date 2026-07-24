// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Linq;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure NuGet version parsing/normalization used to validate an explicit template-pack version and to
/// compare a requested version against the one reported by <c>dotnet new uninstall</c>. Extracted from
/// the <c>new</c> command so the version grammar can evolve independently of the command orchestration.
/// </summary>
internal static class NuGetVersionHelper
{
    /// <summary>
    /// A plausible NuGet package version: a well-formed <c>Major[.Minor[.Patch[.Revision]]]</c>
    /// numeric release with an optional non-empty <c>-prerelease</c> label and optional non-empty
    /// <c>+metadata</c>, using only version characters. Rejects whitespace/quote-laden input (defense
    /// in depth against argument injection) as well as malformed shapes such as <c>1.0-</c>,
    /// <c>1.0+</c>, and <c>1..0</c> that would otherwise normalize to a valid version and silently
    /// satisfy an invalid explicit <c>--template-version</c>.
    /// </summary>
    internal static bool IsPlausibleVersion(string version) => NormalizeNuGetVersion(version) is not null;

    /// <summary>
    /// Returns true when two NuGet version strings are equivalent. NuGet normalizes equal versions
    /// (for example <c>1.0</c>, <c>1.0.0</c>, and <c>1.0.0.0</c>), and <c>dotnet new uninstall</c>
    /// prints the normalized form, so an exact string compare against the requested spelling would
    /// spuriously miss and force a redundant install/network operation on every run. Compares the
    /// numeric release (padding to at least three parts, dropping a trailing zero revision) and the
    /// prerelease label (case-insensitively, per the NuGet spec), ignoring build metadata.
    /// </summary>
    internal static bool NuGetVersionsEquivalent(string a, string b)
    {
        var normA = NormalizeNuGetVersion(a);
        var normB = NormalizeNuGetVersion(b);
        return normA is not null && normB is not null
            ? string.Equals(normA, normB, StringComparison.Ordinal)
            : string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces a NuGet version to a canonical <c>Major.Minor.Patch[.Revision][-prerelease]</c> string
    /// for equivalence comparison, or <c>null</c> when the version is malformed (non-numeric release,
    /// empty release/prerelease/metadata segment, repeated separators, or invalid identifier
    /// characters). Build metadata (<c>+...</c>) is validated for shape but dropped from the result,
    /// since it does not participate in version equality.
    /// </summary>
    private static string? NormalizeNuGetVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var value = version.Trim();

        // Drop build metadata (ignored for version equality), but reject an empty or malformed
        // '+...' segment (e.g. "1.0+") instead of silently accepting it.
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            if (!IsValidDotSeparatedIdentifiers(value[(plus + 1)..]))
            {
                return null;
            }

            value = value[..plus];
        }

        // Separate the prerelease label from the numeric release, rejecting an empty or malformed
        // '-...' segment (e.g. "1.0-") so it can't normalize to a valid release.
        var prerelease = string.Empty;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            if (!IsValidDotSeparatedIdentifiers(prerelease))
            {
                return null;
            }

            value = value[..dash];
        }

        if (value.Length == 0)
        {
            return null;
        }

        var parts = value.Split('.');
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            numbers.Add(number);
        }

        // NuGet keeps at least Major.Minor.Patch and omits a zero-valued Revision.
        while (numbers.Count < 3)
        {
            numbers.Add(0);
        }

        while (numbers.Count > 3 && numbers[^1] == 0)
        {
            numbers.RemoveAt(numbers.Count - 1);
        }

        var numeric = string.Join('.', numbers);
        return prerelease.Length > 0
            ? $"{numeric}-{prerelease.ToLowerInvariant()}"
            : numeric;
    }

    /// <summary>
    /// True when <paramref name="segment"/> is a dot-separated list of non-empty identifiers using
    /// only ASCII alphanumerics and hyphens (the NuGet prerelease/metadata grammar). Rejects an empty
    /// segment, empty identifiers (e.g. <c>a..b</c> or a trailing dot), and any other character.
    /// </summary>
    private static bool IsValidDotSeparatedIdentifiers(string segment)
    {
        if (segment.Length == 0)
        {
            return false;
        }

        foreach (var identifier in segment.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            if (identifier.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            {
                return false;
            }
        }

        return true;
    }
}
