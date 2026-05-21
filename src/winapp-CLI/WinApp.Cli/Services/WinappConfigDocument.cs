// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Hand-rolled reader / writer for the small winapp.yaml grammar that the
/// native CLI owns (packages:). Pure data class — no DI, no file I/O.
///
/// Mirrors <see cref="AppxManifestDocument"/>: <see cref="ConfigService"/> is
/// the thin file-I/O wrapper; this class owns the YAML grammar. Splitting
/// keeps grammar changes (which evolve with the schema) from leaking into
/// the service surface tests assert against.
///
/// Unknown top-level keys are silently ignored on read and dropped on a full
/// <see cref="Render"/>. Callers that need to round-trip an unknown block
/// (e.g. tooling that layers extra metadata into winapp.yaml) must avoid
/// Save() and read/rewrite the raw yaml text themselves.
/// </summary>
internal sealed class WinappConfigDocument
{
    public WinappConfig Config { get; }

    public WinappConfigDocument(WinappConfig config)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Parse a winapp.yaml document. Unknown top-level fields are silently
    /// ignored so adding fields server-side doesn't break older CLIs.
    /// </summary>
    public static WinappConfigDocument Parse(string yaml)
    {
        return new WinappConfigDocument(ParseInternal(yaml ?? string.Empty));
    }

    /// <summary>
    /// Full re-serialization. Drops comments and unknown fields.
    /// </summary>
    public string Render() => Stringify(Config);

    // -------------------------------------------------------------------------
    // Parse
    // -------------------------------------------------------------------------

    private static WinappConfig ParseInternal(string yaml)
    {
        var cfg = new WinappConfig();
        using var sr = new StringReader(yaml);
        string? line;
        string? currentName = null;
        var section = Section.None;

        while ((line = sr.ReadLine()) != null)
        {
            // Preserve raw indent for nested-list tracking, then trim for content match.
            var indent = LeadingSpaceCount(line);
            var t = line.Trim();
            if (t.StartsWith('#') || t.Length == 0)
            {
                continue;
            }

            // Top-level section switches (no indent).
            if (indent == 0)
            {
                if (IsTopLevelKey(t, "packages:"))
                {
                    section = Section.Packages;
                    currentName = null;
                    continue;
                }

                // Unknown top-level field → reset section so children don't leak
                // into packages/etc.
                section = Section.None;
                currentName = null;
                continue;
            }

            switch (section)
            {
                case Section.Packages:
                    if (t.StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentName = SanitizeScalar(t.Substring("- name:".Length));
                    }
                    else if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentName = SanitizeScalar(t.Substring("name:".Length));
                    }
                    else if (currentName is not null && t.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                    {
                        var version = SanitizeScalar(t.Substring("version:".Length));
                        cfg.Packages.Add(new PackagePin { Name = currentName, Version = version });
                        currentName = null;
                    }
                    break;
            }
        }

        return cfg;
    }

    internal static bool TryReadScalar(string t, string prefix, out string value)
    {
        if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = SanitizeScalar(t.Substring(prefix.Length));
            return true;
        }
        value = string.Empty;
        return false;
    }

    // YAML-style boolean tolerance: true/false/yes/no/on/off (case-insensitive).
    internal static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "yes":
            case "on":
            case "1":
                result = true;
                return true;
            case "false":
            case "no":
            case "off":
            case "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    // Trims surrounding whitespace, strips an unquoted trailing `# comment`,
    // then strips a single pair of matching surrounding quotes. Mirrors what
    // a YAML parser would do for plain / single- / double-quoted scalars
    // (sufficient for the small grammar we parse by hand).
    //
    // `version: 1.0.0 # pinned`   → `1.0.0`
    // `name: "weird # name"`      → `weird # name`
    // `name: 'O''Brien'`          → `O'Brien`
    internal static string SanitizeScalar(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.AsSpan().TrimStart();
        char? quoteOpener = null;
        if (trimmed.Length > 0 && (trimmed[0] == '"' || trimmed[0] == '\''))
        {
            quoteOpener = trimmed[0];
        }

        int cutoff = trimmed.Length;
        // Quote-state tracking only matters when the scalar is actually
        // quoted (i.e. `quoteOpener` was set at index 0). For plain
        // scalars, an apostrophe inside a value like `John's` must NOT
        // make the rest of the line look "inside a single quote" — that
        // would suppress the `# comment` boundary and a subsequent save
        // would re-quote the value with the comment baked in.
        bool trackQuoteState = quoteOpener is not null;
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (trackQuoteState)
            {
                if (inDouble)
                {
                    if (c == '\\' && i + 1 < trimmed.Length) { i++; continue; }
                    if (c == '"') { inDouble = false; }
                    continue;
                }
                if (inSingle)
                {
                    if (c == '\'') { inSingle = false; }
                    continue;
                }
                if (c == '"') { inDouble = true; continue; }
                if (c == '\'') { inSingle = true; continue; }
            }
            if (c == '#')
            {
                // YAML requires whitespace before an unquoted inline comment.
                if (i == 0 || char.IsWhiteSpace(trimmed[i - 1]))
                {
                    cutoff = i;
                    break;
                }
            }
        }

        var value = trimmed.Slice(0, cutoff).TrimEnd();
        // Only peel the OUTER quote pair when it's symmetrical so we don't
        // turn `it's` into `it`s`.
        if (value.Length >= 2 && quoteOpener is char q && value[0] == q && value[^1] == q)
        {
            var inner = value.Slice(1, value.Length - 2).ToString();
            if (q == '\'')
            {
                // YAML single-quoted scalars use `''` as the escape for a
                // literal `'`. Render writes `'O''Brien'` for `O'Brien`;
                // unescape symmetrically so round-trip is stable.
                return inner.Replace("''", "'");
            }
            return inner;
        }
        return value.ToString();
    }

    private static int LeadingSpaceCount(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }
        return i;
    }

    // Matches a top-level key like `packages:` with any trailing whitespace
    // or inline `# comment`.
    internal static bool IsTopLevelKey(string trimmedLine, string key)
    {
        if (!trimmedLine.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (trimmedLine.Length == key.Length)
        {
            return true;
        }
        var rest = trimmedLine.AsSpan(key.Length).TrimStart();
        return rest.IsEmpty || rest[0] == '#';
    }

    private enum Section { None, Packages }

    // -------------------------------------------------------------------------
    // Render
    // -------------------------------------------------------------------------

    private static string Stringify(WinappConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("packages:");
        foreach (var p in cfg.Packages)
        {
            sb.AppendLine($"  - name: {QuoteScalar(p.Name)}");
            sb.AppendLine($"    version: {QuoteScalar(p.Version)}");
        }

        return sb.ToString();
    }

    // Quote a YAML scalar with single quotes when the raw value would be
    // re-parsed incorrectly by our (or any other) plain-scalar reader. We
    // bias toward over-quoting because the cost is cosmetic and the cost
    // of UNDER-quoting is silent data corruption on the next load (e.g.
    // a Windows path `C:\foo` would otherwise be re-read as a mapping, a
    // value containing `#` would be re-read with the comment chopped off).
    internal static string QuoteScalar(string value)
    {
        if (NeedsQuoting(value))
        {
            // Single-quoted YAML strings: only `'` needs escaping (doubled).
            return "'" + value.Replace("'", "''") + "'";
        }
        return value;
    }

    private static bool NeedsQuoting(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }
        // YAML indicators that cannot start a plain scalar (YAML 1.2 §7.3.3).
        if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) >= 0)
        {
            return true;
        }
        // Reserved YAML 1.1 boolean/null literals (parsers may still honor these).
        switch (value.ToLowerInvariant())
        {
            case "null":
            case "~":
            case "true":
            case "false":
            case "yes":
            case "no":
            case "on":
            case "off":
                return true;
        }
        // Numeric-looking values would re-parse as numbers, not strings.
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return true;
        }
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return true;
        }
        foreach (var c in value)
        {
            if (c == '\t' || c == '\r' || c == '\n')
            {
                return true;
            }
            // Any `:` or `#` in the body is enough to change the parse. Windows
            // paths (`C:\…`) and values like `note # foo` are the motivating
            // cases — be conservative and quote.
            if (c == ':' || c == '#')
            {
                return true;
            }
        }
        return false;
    }
}
