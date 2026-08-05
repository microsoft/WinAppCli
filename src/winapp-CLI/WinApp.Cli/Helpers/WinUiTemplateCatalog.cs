// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Linq;

namespace WinApp.Cli.Helpers;

/// <summary>
/// A single template row parsed from <c>dotnet new list</c>. <see cref="ShortNames"/> holds every
/// alias dotnet accepts for the template (comma-separated in the source table); <see cref="ShortName"/>
/// is the first (canonical) one that <c>dotnet new &lt;short&gt;</c> is invoked with. <see cref="Type"/>
/// is <c>project</c> or <c>item</c>; <see cref="Tags"/> is the raw <c>Windows/WinUI/...</c> tag path
/// used to derive next-step guidance.
/// </summary>
internal sealed record WinUiTemplateEntry(
    string DisplayName,
    IReadOnlyList<string> ShortNames,
    string Language,
    string Type,
    string Tags)
{
    /// <summary>Canonical short name passed to <c>dotnet new</c> (the first listed alias).</summary>
    public string ShortName => ShortNames.Count > 0 ? ShortNames[0] : string.Empty;

    /// <summary>True when this is a project template (creates a new project), not an item template.</summary>
    public bool IsProject => string.Equals(Type, "project", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this is an item template (added into an existing project).</summary>
    public bool IsItem => string.Equals(Type, "item", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> matches any of this template's short names (case-insensitive).</summary>
    public bool MatchesShortName(string candidate)
        => ShortNames.Any(s => string.Equals(s, candidate, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Pure parsing of <c>dotnet</c> template subcommand output. Enumerating a pack's templates and
/// detecting a stale installed pack both require scraping localized, human-oriented tables (there is
/// no machine-readable <c>dotnet new</c> output), so callers must force <c>DOTNET_CLI_UI_LANGUAGE=en</c>
/// before invoking dotnet. Parsing lives here (not in the command) so the brittle table formats can be
/// unit-tested and evolved independently of the command orchestration.
/// </summary>
internal static class WinUiTemplateCatalog
{
    /// <summary>
    /// Parses the fixed-width table emitted by <c>dotnet new list ... --columns-all</c> into template
    /// entries. Column boundaries are taken from the separator row of dashes (the only reliable
    /// delimiter, since several columns — Template Name, Tags — contain spaces), so the parser is
    /// resilient to differing column widths across dotnet versions. Rows that don't line up with the
    /// header (banners, blank lines, the "These templates matched" preamble) are skipped. Returns an
    /// empty list when no table is present.
    /// </summary>
    internal static IReadOnlyList<WinUiTemplateEntry> ParseList(string output)
    {
        var entries = new List<WinUiTemplateEntry>();
        if (string.IsNullOrEmpty(output))
        {
            return entries;
        }

        var lines = output.Replace("\r\n", "\n").Split('\n');

        // Locate the dashes separator row; the header is the non-blank line immediately above it.
        var separatorIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsSeparatorRow(lines[i]))
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex <= 0)
        {
            return entries;
        }

        var header = lines[separatorIndex - 1];
        var columns = GetColumnRanges(lines[separatorIndex]);
        if (columns.Count == 0)
        {
            return entries;
        }

        // Map each column to a role by its header label, so column order/presence changes across
        // dotnet versions don't silently shift fields (e.g. reading Tags out of the Author column).
        int shortNameCol = -1, languageCol = -1, typeCol = -1, tagsCol = -1, nameCol = -1;
        for (var c = 0; c < columns.Count; c++)
        {
            var label = Slice(header, columns[c]).Trim();
            if (label.Equals("Short Name", StringComparison.OrdinalIgnoreCase))
            {
                shortNameCol = c;
            }
            else if (label.Equals("Template Name", StringComparison.OrdinalIgnoreCase))
            {
                nameCol = c;
            }
            else if (label.Equals("Language", StringComparison.OrdinalIgnoreCase))
            {
                languageCol = c;
            }
            else if (label.Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                typeCol = c;
            }
            else if (label.Equals("Tags", StringComparison.OrdinalIgnoreCase))
            {
                tagsCol = c;
            }
        }

        // Short Name is the only field we cannot function without; the rest degrade gracefully.
        if (shortNameCol < 0)
        {
            return entries;
        }

        for (var i = separatorIndex + 1; i < lines.Length; i++)
        {
            var row = lines[i];
            if (string.IsNullOrWhiteSpace(row) || IsSeparatorRow(row))
            {
                continue;
            }

            var shortNamesRaw = Slice(row, columns[shortNameCol]).Trim();
            if (shortNamesRaw.Length == 0)
            {
                continue;
            }

            var shortNames = shortNamesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (shortNames.Length == 0)
            {
                continue;
            }

            entries.Add(new WinUiTemplateEntry(
                DisplayName: nameCol >= 0 ? Slice(row, columns[nameCol]).Trim() : string.Empty,
                ShortNames: shortNames,
                Language: languageCol >= 0 ? Slice(row, columns[languageCol]).Trim() : string.Empty,
                Type: typeCol >= 0 ? Slice(row, columns[typeCol]).Trim() : string.Empty,
                Tags: tagsCol >= 0 ? Slice(row, columns[tagsCol]).Trim() : string.Empty));
        }

        return entries;
    }

    /// <summary>
    /// Parses <c>dotnet new update --check-only</c> output for the given package id, returning the
    /// installed (<c>Current</c>) and available (<c>Latest</c>) versions when an update row for the
    /// package is present. Returns <c>(null, null)</c> when the package is up-to-date (no update row)
    /// or the output can't be parsed. The update table has three columns (Package, Current, Latest);
    /// the Package id contains no spaces, so a simple whitespace split is sufficient and avoids
    /// depending on exact column widths.
    /// </summary>
    internal static (string? Current, string? Latest) ParseUpdateCheck(string output, string packageId)
    {
        if (string.IsNullOrEmpty(output))
        {
            return (null, null);
        }

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsSeparatorRow(raw))
            {
                continue;
            }

            // Data row for our package: "<id>  <current>  <latest>". Header row starts with
            // "Package" and is skipped by the id comparison.
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Equals(packageId, StringComparison.OrdinalIgnoreCase))
            {
                return (parts[1], parts[2]);
            }
        }

        return (null, null);
    }

    /// <summary>True when the line consists solely of a dashes separator (with optional surrounding whitespace).</summary>
    private static bool IsSeparatorRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0 && trimmed.All(ch => ch == '-' || ch == ' ');
    }

    /// <summary>
    /// Derives column [start, end) ranges from a dashes separator row: each contiguous run of dashes
    /// is one column, and the range extends to the start of the next column's dashes so trailing
    /// spaces in a wide field are captured. The final column extends to end-of-line.
    /// </summary>
    private static List<(int Start, int End)> GetColumnRanges(string separator)
    {
        var ranges = new List<(int Start, int End)>();
        var i = 0;
        while (i < separator.Length)
        {
            if (separator[i] == '-')
            {
                var start = i;
                while (i < separator.Length && separator[i] == '-')
                {
                    i++;
                }

                ranges.Add((start, i));
            }
            else
            {
                i++;
            }
        }

        // Extend each column's end to just before the next column's start (absorbing the gap), and the
        // last column to end-of-line, so values slightly wider than their dash run aren't truncated.
        for (var c = 0; c < ranges.Count; c++)
        {
            var end = c + 1 < ranges.Count ? ranges[c + 1].Start : int.MaxValue;
            ranges[c] = (ranges[c].Start, end);
        }

        return ranges;
    }

    /// <summary>Returns the substring of <paramref name="line"/> within [Start, End), clamped to the line length.</summary>
    private static string Slice(string line, (int Start, int End) range)
    {
        if (range.Start >= line.Length)
        {
            return string.Empty;
        }

        var end = Math.Min(range.End, line.Length);
        return line[range.Start..end];
    }
}
