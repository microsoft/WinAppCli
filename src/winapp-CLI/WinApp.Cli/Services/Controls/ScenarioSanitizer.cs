// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

/// <summary>
/// Single corpus-boundary guard for fetched scenario content. Every scenario that
/// comes from a network provider (Gallery/Toolkit/Reactor) passes through
/// <see cref="Sanitize"/> exactly once — in <see cref="ControlsSearchService"/>,
/// before the <see cref="SearchEngine"/> is built — so the console, <c>--json</c>,
/// and cache-read paths are all covered by one call. It does three things:
/// <list type="number">
/// <item>Strips C0/C1 control characters (except tab/newline/carriage-return) from
/// every emitted text field, so a booby-trapped upstream sample can't smuggle ANSI
/// escape / OSC sequences to a terminal or into piped agent input. <c>Id</c> and
/// <c>ControlId</c> are stripped harder still — tab/newline/CR go too, since an id is
/// a lookup key echoed into output, <c>--json</c> and usage telemetry, never prose.</item>
/// <item>Drops XAML that is not well-formed rather than shipping broken markup an
/// agent would paste and fail to compile — malformed truncation "repairs" and a
/// small number of pre-existing upstream samples produce unparseable XAML.</item>
/// <item>Drops C# whose braces don't balance for the same reason.</item>
/// </list>
/// Curated core patterns are hand-authored and trusted, so they don't go through here.
/// </summary>
internal static partial class ScenarioSanitizer
{
    /// <summary>Sanitize every scenario in place (see <see cref="Sanitize"/>).</summary>
    public static void SanitizeAll(IEnumerable<Scenario> scenarios)
    {
        foreach (var s in scenarios)
        {
            Sanitize(s);
        }
    }

    /// <summary>
    /// Strip control characters from every emitted field of <paramref name="s"/> (ids
    /// included), then null out its XAML if it isn't well-formed and its C# if the
    /// braces don't balance.
    /// </summary>
    public static void Sanitize(Scenario s)
    {
        // Ids are stripped harder than prose (see StripIdControlChars): they are
        // single-token lookup keys echoed into search output, --json and usage
        // telemetry, and Reactor takes ControlId straight from downloaded JSON.
        s.Id = StripIdControlChars(s.Id);
        s.ControlId = StripIdControlChars(s.ControlId);

        s.HeaderText = StripControlChars(s.HeaderText) ?? "";
        s.ControlName = StripControlChars(s.ControlName) ?? "";
        s.ControlDescription = StripControlChars(s.ControlDescription);
        s.Description = StripControlChars(s.Description);
        s.NuGetPackage = StripControlChars(s.NuGetPackage);
        s.ApiNamespace = StripControlChars(s.ApiNamespace);
        s.XmlnsImports = StripControlChars(s.XmlnsImports);
        s.RelatedControls = StripControlChars(s.RelatedControls);

        var xaml = StripControlChars(s.Xaml);
        s.Xaml = !string.IsNullOrWhiteSpace(xaml) && XamlIsWellFormed(xaml) ? xaml : null;

        var csharp = StripControlChars(s.CSharp);
        s.CSharp = !string.IsNullOrWhiteSpace(csharp) && CSharpBracesBalanced(csharp) ? csharp : null;
    }

    /// <summary>Prefixes that appear before a colon in element/attribute names — used to
    /// synthesize namespace declarations so an undeclared-prefix XAML fragment isn't a
    /// false structural-validation failure.</summary>
    [GeneratedRegex(@"([A-Za-z_][\w.\-]*):")]
    private static partial Regex NsPrefixRegex();

    /// <summary>
    /// True when <paramref name="xaml"/> is a well-formed XML fragment. Namespace prefixes
    /// (<c>controls:</c>, <c>x:</c>, …) are declared on a synthetic wrapper root so an
    /// otherwise-valid snippet isn't rejected for using a prefix it never declares; only a
    /// genuine structural fault (unbalanced tags, unquoted attributes, raw <c>&amp;</c>/<c>&lt;</c>)
    /// fails. DTD processing is disabled to avoid any external-entity handling.
    /// </summary>
    public static bool XamlIsWellFormed(string xaml)
    {
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in NsPrefixRegex().Matches(xaml))
        {
            var p = m.Groups[1].Value;
            // xml/xmlns are reserved and can't be (re)declared.
            if (p is "xml" or "xmlns") continue;
            prefixes.Add(p);
        }

        var wrapped = new StringBuilder("<winappSanitizerRoot");
        foreach (var p in prefixes)
        {
            wrapped.Append(" xmlns:").Append(p).Append("=\"urn:winapp:").Append(p).Append('"');
        }
        wrapped.Append('>').Append(xaml).Append("</winappSanitizerRoot>");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(wrapped.ToString()), settings);
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the curly braces in <paramref name="code"/> balance, ignoring braces inside
    /// string/char literals (including verbatim <c>@"…"</c>) and line/block comments. A snippet
    /// truncated mid-block (or otherwise brace-unbalanced) won't compile when pasted, so it's
    /// dropped rather than emitted.
    /// </summary>
    public static bool CSharpBracesBalanced(string code)
    {
        int depth = 0;
        bool inStr = false, inChr = false, inLine = false, inBlk = false, inVerb = false;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i]; char prev = i > 0 ? code[i - 1] : '\0';
            if (inLine) { if (c == '\n') inLine = false; continue; }
            if (inBlk) { if (c == '/' && prev == '*') inBlk = false; continue; }
            if (inStr)
            {
                if (inVerb) { if (c == '"' && (i + 1 >= code.Length || code[i + 1] != '"')) { inStr = false; inVerb = false; } else if (c == '"') i++; }
                else if (c == '"' && prev != '\\') inStr = false;
                continue;
            }
            if (inChr) { if (c == '\'' && prev != '\\') inChr = false; continue; }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') { inLine = true; continue; }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '*') { inBlk = true; continue; }
            if (c == '@' && i + 1 < code.Length && code[i + 1] == '"') { inStr = true; inVerb = true; i++; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '\'') { inChr = true; continue; }
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth < 0) return false; }
        }
        return depth == 0 && !inStr && !inBlk;
    }

    private static string[] StripControlChars(string[] values)
    {
        if (values.Length == 0) return values;
        var result = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = StripControlChars(values[i]) ?? "";
        }
        return result;
    }

    /// <summary>
    /// Identifier-strength stripping: everything <see cref="StripControlChars(string?)"/>
    /// removes, plus tab, newline and carriage-return. Those three are legitimate inside
    /// prose, XAML and C#, but never inside an id. An id is a single-token lookup key that
    /// gets echoed back in search/list output, in <c>--json</c>, and in usage telemetry, so
    /// a newline inside one would let a poisoned upstream id forge an extra output line
    /// (a fake result row) even after ANSI escapes were removed.
    /// </summary>
    private static string StripIdControlChars(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        StringBuilder? sb = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool strip = c < 0x20 || c == 0x7F || (c >= 0x80 && c <= 0x9F);
            if (strip)
            {
                sb ??= new StringBuilder(value.Length).Append(value, 0, i);
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? value;
    }

    /// <summary>
    /// Remove C0 control characters (0x00–0x1F) except tab/newline/carriage-return, DEL (0x7F),
    /// and C1 control characters (0x80–0x9F). This neutralizes ANSI/OSC escape sequences (which
    /// begin with ESC, 0x1B) and other terminal-control bytes at the one boundary where fetched
    /// text becomes scenario content, so no downstream console/JSON/cache path can emit them.
    /// Returns the input unchanged (same reference) when nothing needs stripping.
    /// </summary>
    private static string? StripControlChars(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        StringBuilder? sb = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool strip = (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                || c == 0x7F
                || (c >= 0x80 && c <= 0x9F);
            if (strip)
            {
                sb ??= new StringBuilder(value.Length).Append(value, 0, i);
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? value;
    }
}
