// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Hand-rolled reader / writer / splicer for the small winapp.yaml grammar
/// (packages + jsBindings only). Pure data class — no DI, no file I/O.
///
/// Mirrors <see cref="AppxManifestDocument"/>: <see cref="ConfigService"/> is
/// the thin file-I/O wrapper; this class owns the YAML grammar. Splitting
/// keeps grammar changes (which evolve with the schema) from leaking into
/// the service surface tests assert against.
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
    /// Full re-serialization. Drops comments and unknown fields — use
    /// <see cref="SpliceJsBindingsInto"/> when you need to preserve the
    /// rest of the file.
    /// </summary>
    public string Render() => Stringify(Config);

    /// <summary>
    /// Replace (or insert) just the jsBindings: block inside the existing
    /// yaml text, preserving comments, unknown fields, blank lines, and
    /// original line endings. Returns the rewritten yaml text.
    /// </summary>
    public string SpliceJsBindingsInto(string existingYaml)
        => SpliceJsBindingsBlock(existingYaml ?? string.Empty, Config.JsBindings);

    // -------------------------------------------------------------------------
    // Splice
    // -------------------------------------------------------------------------

    // Splice a new jsBindings: block into existingYaml. Block bounds: a
    // zero-indent "jsBindings:" line → next zero-indent non-blank line (or
    // EOF). null `newJsBindings` removes the block.
    internal static string SpliceJsBindingsBlock(string existingYaml, JsBindingsConfig? newJsBindings)
    {
        string? replacement = null;
        if (newJsBindings is not null)
        {
            var sb = new StringBuilder();
            AppendJsBindingsBlock(sb, newJsBindings);
            replacement = sb.ToString();
        }

        // Line-by-line scan; preserve original newline style.
        var lines = existingYaml.Split('\n');
        int blockStart = -1;
        int blockEnd = -1;  // exclusive end (next line index)
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');
            if (IsTopLevelKey(trimmed, "jsBindings:"))
            {
                if (lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]))
                {
                    continue;  // nested, not a top-level key
                }
                blockStart = i;
                // Find block end: next zero-indent non-blank line, or EOF.
                // Zero-indent comments belong to the *next* top-level section
                // (or to the file tail), not to jsBindings — preserve them.
                blockEnd = lines.Length;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var t = lines[j].TrimEnd('\r');
                    if (t.Length == 0)
                    {
                        continue;  // blank lines belong to no block
                    }
                    if (!char.IsWhiteSpace(lines[j][0]))
                    {
                        // Any zero-indent line (key OR comment) ends the block.
                        blockEnd = j;
                        break;
                    }
                }
                break;
            }
        }

        if (blockStart >= 0)
        {
            var before = string.Join('\n', lines.Take(blockStart));
            var after = string.Join('\n', lines.Skip(blockEnd));
            var middle = replacement ?? string.Empty;
            // Careful newline stitching — avoid double blanks / dropped trailing newline.
            var result = new StringBuilder();
            if (before.Length > 0)
            {
                result.Append(before);
                if (!before.EndsWith('\n'))
                {
                    result.Append('\n');
                }
            }
            if (middle.Length > 0)
            {
                result.Append(middle);
                if (!middle.EndsWith('\n'))
                {
                    result.Append('\n');
                }
            }
            if (after.Length > 0)
            {
                result.Append(after);
            }
            return result.ToString();
        }

        // No existing block — append the new one (if any) at the end.
        if (replacement is null)
        {
            return existingYaml;
        }
        var trailing = existingYaml.EndsWith('\n') ? string.Empty : "\n";
        return existingYaml + trailing + replacement;
    }

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

        // jsBindings sub-state
        JsBindingsConfig? js = null;
        var jsList = JsListMode.None;
        JsBindingsExtraType? currentExtra = null;
        bool inClassesList = false;

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
                // Accept `jsBindings:` followed by inline comment / trailing
                // whitespace — matches SpliceJsBindingsBlock's detection so
                // Load() and the splice can never disagree on whether the
                // block exists.
                if (IsTopLevelKey(t, "jsBindings:"))
                {
                    section = Section.JsBindings;
                    js = new JsBindingsConfig();
                    jsList = JsListMode.None;
                    currentExtra = null;
                    inClassesList = false;
                    continue;
                }

                // Unknown top-level field → reset section so children don't leak.
                section = Section.None;
                currentName = null;
                jsList = JsListMode.None;
                currentExtra = null;
                inClassesList = false;
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

                case Section.JsBindings:
                    ParseJsBindingsLine(js!, t, ref jsList, ref currentExtra, ref inClassesList);
                    break;
            }
        }

        if (js is not null)
        {
            cfg.JsBindings = js;
        }
        return cfg;
    }

    private static void ParseJsBindingsLine(
        JsBindingsConfig js,
        string t,
        ref JsListMode listMode,
        ref JsBindingsExtraType? currentExtra,
        ref bool inClassesList)
    {
        // Scalar keys reset list state.
        if (TryReadScalar(t, "lang:", out var v)) { js.Lang = v; listMode = JsListMode.None; currentExtra = null; inClassesList = false; return; }
        if (TryReadScalar(t, "output:", out v)) { js.Output = v; listMode = JsListMode.None; currentExtra = null; inClassesList = false; return; }

        if (IsTopLevelKey(t, "packages:"))
        {
            listMode = JsListMode.Packages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (IsTopLevelKey(t, "additionalWinmds:"))
        {
            listMode = JsListMode.AdditionalWinmds;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (IsTopLevelKey(t, "additionalRefs:"))
        {
            listMode = JsListMode.AdditionalRefs;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (IsTopLevelKey(t, "skipPackages:"))
        {
            listMode = JsListMode.SkipPackages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (IsTopLevelKey(t, "refOnlyPackages:"))
        {
            listMode = JsListMode.RefOnlyPackages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (IsTopLevelKey(t, "emitPackages:"))
        {
            listMode = JsListMode.EmitPackages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (IsTopLevelKey(t, "extraTypes:"))
        {
            listMode = JsListMode.ExtraTypes;
            currentExtra = null;
            inClassesList = false;
            return;
        }

        if (t.StartsWith("- ", StringComparison.Ordinal)
            && s_listSelectors.TryGetValue(listMode, out var getList))
        {
            var value = SanitizeScalar(t[2..]);
            var list = getList(js);
            if (!string.IsNullOrEmpty(value)
                && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
            return;
        }

        if (listMode == JsListMode.ExtraTypes)
        {
            // A `- ` IMMEDIATELY followed by a known extraTypes sub-key
            // (`namespace:` or `classes:`) anchors a new entry. Without
            // this recognition rule we couldn't tell `- classes:` (a new
            // entry whose first key is `classes:`) from `- ClassName` (a
            // class literally named "ClassName" inside the previous
            // entry's classes list) — the parser sees the line already
            // left-trimmed, so indent can't disambiguate. Restricting to
            // known sub-keys makes the order-independence safe in both
            // directions: `- namespace: …` first or `- classes: …` first.
            if (t.StartsWith("- ", StringComparison.Ordinal))
            {
                var rest = t.Substring(2).TrimStart();
                bool isEntryAnchor =
                    rest.StartsWith("namespace:", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("classes:", StringComparison.OrdinalIgnoreCase);
                if (isEntryAnchor)
                {
                    currentExtra = new JsBindingsExtraType();
                    js.ExtraTypes.Add(currentExtra);
                    inClassesList = false;
                    // Re-dispatch the rest of the dash line as a child
                    // key/value by stripping the dash prefix and falling
                    // through to the sub-key matchers.
                    t = rest;
                }
            }
            if (currentExtra is null)
            {
                return;
            }
            if (t.StartsWith("namespace:", StringComparison.OrdinalIgnoreCase))
            {
                currentExtra.Namespace = SanitizeScalar(t.Substring("namespace:".Length));
                inClassesList = false;
                return;
            }
            if (IsTopLevelKey(t, "classes:"))
            {
                inClassesList = true;
                return;
            }
            // Inline flow-list form: `classes: [X, Y, Z]` or `classes: [X]`.
            if (t.StartsWith("classes:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = t.Substring("classes:".Length).Trim();
                if (rest.StartsWith('['))
                {
                    var end = rest.IndexOf(']');
                    if (end > 0)
                    {
                        var contents = rest.Substring(1, end - 1);
                        foreach (var item in contents.Split(','))
                        {
                            var name = SanitizeScalar(item);
                            if (!string.IsNullOrEmpty(name))
                            {
                                currentExtra.Classes.Add(name);
                            }
                        }
                        inClassesList = false;
                        return;
                    }
                }
                // Scalar form: `classes: SingleClass` (no brackets).
                if (!string.IsNullOrEmpty(rest))
                {
                    var name = SanitizeScalar(rest);
                    if (!string.IsNullOrEmpty(name))
                    {
                        currentExtra.Classes.Add(name);
                    }
                    inClassesList = false;
                    return;
                }
            }
            if (inClassesList && t.StartsWith("- ", StringComparison.Ordinal))
            {
                var name = SanitizeScalar(t[2..]);
                if (!string.IsNullOrEmpty(name))
                {
                    currentExtra.Classes.Add(name);
                }
                return;
            }
        }
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

    // Trims surrounding whitespace, strips an unquoted trailing `# comment`,
    // then strips a single pair of matching surrounding quotes. Mirrors what
    // a YAML parser would do for plain / single- / double-quoted scalars
    // (sufficient for the small jsBindings grammar we parse by hand).
    //
    // `output: bindings/winrt # generated` → `bindings/winrt`
    // `name: "weird # name"`                → `weird # name`
    // `path: 'C:\foo'`                       → `C:\foo`
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

    // Matches a top-level key like `packages:` or `jsBindings:` with any
    // trailing whitespace or inline `# comment`. Used by both Parse and
    // SpliceJsBindingsBlock so they never disagree on block presence.
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

    private enum Section { None, Packages, JsBindings }
    private enum JsListMode { None, Packages, ExtraTypes, AdditionalWinmds, AdditionalRefs, SkipPackages, RefOnlyPackages, EmitPackages }

    // Table-driven dispatch for the string-list jsBindings sub-keys (everything
    // except ExtraTypes, whose entries are objects). Keeps ParseJsBindingsLine
    // honest: adding a new list-mode is one table entry instead of an enum arm
    // + a 9-line near-duplicate if-block that could silently drift.
    private static readonly Dictionary<JsListMode, Func<JsBindingsConfig, List<string>>> s_listSelectors = new()
    {
        [JsListMode.Packages]         = js => js.Packages,
        [JsListMode.AdditionalWinmds] = js => js.AdditionalWinmds,
        [JsListMode.AdditionalRefs]   = js => js.AdditionalRefs,
        [JsListMode.SkipPackages]     = js => js.SkipPackages,
        [JsListMode.RefOnlyPackages]  = js => js.RefOnlyPackages,
        [JsListMode.EmitPackages]     = js => js.EmitPackages,
    };

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

        if (cfg.JsBindings is { } js)
        {
            sb.AppendLine();
            AppendJsBindingsBlock(sb, js);
        }
        return sb.ToString();
    }

    // Render the jsBindings: block. Shared by Stringify and SpliceJsBindingsBlock.
    private static void AppendJsBindingsBlock(StringBuilder sb, JsBindingsConfig js)
    {
        sb.AppendLine("jsBindings:");
        sb.AppendLine($"  lang: {QuoteScalar(js.Lang)}");
        sb.AppendLine($"  output: {QuoteScalar(js.Output)}");
        if (js.Packages.Count > 0)
        {
            sb.AppendLine("  packages:");
            foreach (var pkg in js.Packages)
            {
                sb.AppendLine($"    - {QuoteScalar(pkg)}");
            }
        }
        if (js.AdditionalWinmds.Count > 0)
        {
            sb.AppendLine("  additionalWinmds:");
            foreach (var path in js.AdditionalWinmds)
            {
                sb.AppendLine($"    - {QuoteScalar(path)}");
            }
        }
        if (js.AdditionalRefs.Count > 0)
        {
            sb.AppendLine("  additionalRefs:");
            foreach (var path in js.AdditionalRefs)
            {
                sb.AppendLine($"    - {QuoteScalar(path)}");
            }
        }
        if (js.SkipPackages.Count > 0)
        {
            sb.AppendLine("  skipPackages:");
            foreach (var pkg in js.SkipPackages)
            {
                sb.AppendLine($"    - {QuoteScalar(pkg)}");
            }
        }
        if (js.RefOnlyPackages.Count > 0)
        {
            sb.AppendLine("  refOnlyPackages:");
            foreach (var pkg in js.RefOnlyPackages)
            {
                sb.AppendLine($"    - {QuoteScalar(pkg)}");
            }
        }
        if (js.EmitPackages.Count > 0)
        {
            sb.AppendLine("  emitPackages:");
            foreach (var pkg in js.EmitPackages)
            {
                sb.AppendLine($"    - {QuoteScalar(pkg)}");
            }
        }
        if (js.ExtraTypes.Count > 0)
        {
            sb.AppendLine("  extraTypes:");
            foreach (var et in js.ExtraTypes)
            {
                sb.AppendLine($"    - namespace: {QuoteScalar(et.Namespace)}");
                if (et.Classes.Count > 0)
                {
                    sb.AppendLine("      classes:");
                    foreach (var cls in et.Classes)
                    {
                        sb.AppendLine($"        - {QuoteScalar(cls)}");
                    }
                }
            }
        }
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
