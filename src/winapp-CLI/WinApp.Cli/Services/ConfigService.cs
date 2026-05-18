// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class ConfigService : IConfigService
{
    public FileInfo ConfigPath { get; set; }

    public ConfigService(ICurrentDirectoryProvider currentDirectoryProvider)
    {
        var workingDir = currentDirectoryProvider.GetCurrentDirectory();
        ConfigPath = new FileInfo(Path.Combine(workingDir, "winapp.yaml"));
    }

    public bool Exists()
    {
        ConfigPath.Refresh();
        return ConfigPath.Exists;
    }

    public WinappConfig Load()
    {
        if (!Exists())
        {
            return new WinappConfig();
        }

        var text = File.ReadAllText(ConfigPath.FullName);
        return Parse(text);
    }

    public void Save(WinappConfig cfg)
    {
        // Full serialization — drops comments / unknown fields.
        var yaml = Stringify(cfg);
        File.WriteAllText(ConfigPath.FullName, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ConfigPath.Refresh();
    }

    public void SaveJsBindingsOnly(WinappConfig cfg)
    {
        string yaml;
        if (ConfigPath.Exists)
        {
            string existing;
            try
            {
                existing = File.ReadAllText(ConfigPath.FullName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not read existing winapp.yaml at {ConfigPath.FullName} to splice "
                    + "jsBindings while preserving comments. Close any editor/process that may "
                    + "be holding the file open, then retry. "
                    + $"Underlying error: {ex.Message}", ex);
            }

            try
            {
                yaml = SpliceJsBindingsBlock(existing, cfg.JsBindings);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not splice jsBindings: block into winapp.yaml at {ConfigPath.FullName} "
                    + "without losing comments or unknown fields. The file's structure may be "
                    + "malformed; fix it manually or remove the jsBindings: block and re-run. "
                    + $"Underlying error: {ex.Message}", ex);
            }
        }
        else
        {
            yaml = Stringify(cfg);
        }
        File.WriteAllText(ConfigPath.FullName, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ConfigPath.Refresh();
    }

    // Splice a new jsBindings: block into existingYaml. Block bounds: a
    // zero-indent "jsBindings:" line → next zero-indent non-blank line (or
    // EOF). null `newJsBindings` removes the block.
    internal static string SpliceJsBindingsBlock(string existingYaml, Models.JsBindingsConfig? newJsBindings)
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

    private static WinappConfig Parse(string yaml)
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
                if (t.Equals("packages:", StringComparison.OrdinalIgnoreCase))
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
                        currentName = t.Substring("- name:".Length).Trim().Trim('"', '\'');
                    }
                    else if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentName = t.Substring("name:".Length).Trim().Trim('"', '\'');
                    }
                    else if (t.StartsWith("version:", StringComparison.OrdinalIgnoreCase) && currentName is not null)
                    {
                        var version = t.Substring("version:".Length).Trim().Trim('"', '\'');
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

        if (t.Equals("packages:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.Packages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (t.Equals("additionalWinmds:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.AdditionalWinmds;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (t.Equals("additionalRefs:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.AdditionalRefs;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (t.Equals("skipPackages:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.SkipPackages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (t.Equals("refOnlyPackages:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.RefOnlyPackages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (t.Equals("emitPackages:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.EmitPackages;
            currentExtra = null;
            inClassesList = false;
            return;
        }
        if (t.Equals("extraTypes:", StringComparison.OrdinalIgnoreCase))
        {
            listMode = JsListMode.ExtraTypes;
            currentExtra = null;
            inClassesList = false;
            return;
        }

        if (listMode == JsListMode.Packages && t.StartsWith("- ", StringComparison.Ordinal))
        {
            var pkg = t[2..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(pkg)
                && !js.Packages.Contains(pkg, StringComparer.OrdinalIgnoreCase))
            {
                js.Packages.Add(pkg);
            }
            return;
        }

        if (listMode == JsListMode.AdditionalWinmds && t.StartsWith("- ", StringComparison.Ordinal))
        {
            var path = t[2..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(path)
                && !js.AdditionalWinmds.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                js.AdditionalWinmds.Add(path);
            }
            return;
        }

        if (listMode == JsListMode.AdditionalRefs && t.StartsWith("- ", StringComparison.Ordinal))
        {
            var path = t[2..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(path)
                && !js.AdditionalRefs.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                js.AdditionalRefs.Add(path);
            }
            return;
        }

        if (listMode == JsListMode.SkipPackages && t.StartsWith("- ", StringComparison.Ordinal))
        {
            var pkg = t[2..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(pkg)
                && !js.SkipPackages.Contains(pkg, StringComparer.OrdinalIgnoreCase))
            {
                js.SkipPackages.Add(pkg);
            }
            return;
        }

        if (listMode == JsListMode.RefOnlyPackages && t.StartsWith("- ", StringComparison.Ordinal))
        {
            var pkg = t[2..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(pkg)
                && !js.RefOnlyPackages.Contains(pkg, StringComparer.OrdinalIgnoreCase))
            {
                js.RefOnlyPackages.Add(pkg);
            }
            return;
        }

        if (listMode == JsListMode.EmitPackages && t.StartsWith("- ", StringComparison.Ordinal))
        {
            var pkg = t[2..].Trim().Trim('"', '\'');
            if (!string.IsNullOrEmpty(pkg)
                && !js.EmitPackages.Contains(pkg, StringComparer.OrdinalIgnoreCase))
            {
                js.EmitPackages.Add(pkg);
            }
            return;
        }

        if (listMode == JsListMode.ExtraTypes)
        {
            // New item begins with `- namespace:` (the dash anchors a new entry).
            if (t.StartsWith("- namespace:", StringComparison.OrdinalIgnoreCase))
            {
                currentExtra = new JsBindingsExtraType
                {
                    Namespace = t.Substring("- namespace:".Length).Trim().Trim('"', '\''),
                };
                js.ExtraTypes.Add(currentExtra);
                inClassesList = false;
                return;
            }
            if (currentExtra is null)
            {
                return;
            }
            if (t.StartsWith("namespace:", StringComparison.OrdinalIgnoreCase))
            {
                currentExtra.Namespace = t.Substring("namespace:".Length).Trim().Trim('"', '\'');
                inClassesList = false;
                return;
            }
            if (t.Equals("classes:", StringComparison.OrdinalIgnoreCase))
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
                            var name = item.Trim().Trim('"', '\'');
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
                    var name = rest.Trim('"', '\'');
                    currentExtra.Classes.Add(name);
                    inClassesList = false;
                    return;
                }
            }
            if (inClassesList && t.StartsWith("- ", StringComparison.Ordinal))
            {
                currentExtra.Classes.Add(t[2..].Trim().Trim('"', '\''));
                return;
            }
        }
    }

    private static bool TryReadScalar(string t, string prefix, out string value)
    {
        if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = t.Substring(prefix.Length).Trim().Trim('"', '\'');
            return true;
        }
        value = string.Empty;
        return false;
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
    private static bool IsTopLevelKey(string trimmedLine, string key)
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

    private static string Stringify(WinappConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("packages:");
        foreach (var p in cfg.Packages)
        {
            sb.AppendLine($"  - name: {p.Name}");
            sb.AppendLine($"    version: {p.Version}");
        }

        if (cfg.JsBindings is { } js)
        {
            sb.AppendLine();
            AppendJsBindingsBlock(sb, js);
        }
        return sb.ToString();
    }

    // Render the jsBindings: block. Shared by Stringify and SpliceJsBindingsBlock.
    private static void AppendJsBindingsBlock(StringBuilder sb, Models.JsBindingsConfig js)
    {
        sb.AppendLine("jsBindings:");
        sb.AppendLine($"  lang: {js.Lang}");
        sb.AppendLine($"  output: {js.Output}");
        if (js.Packages.Count > 0)
        {
            sb.AppendLine("  packages:");
            foreach (var pkg in js.Packages)
            {
                sb.AppendLine($"    - {pkg}");
            }
        }
        if (js.AdditionalWinmds.Count > 0)
        {
            sb.AppendLine("  additionalWinmds:");
            foreach (var path in js.AdditionalWinmds)
            {
                sb.AppendLine($"    - {path}");
            }
        }
        if (js.AdditionalRefs.Count > 0)
        {
            sb.AppendLine("  additionalRefs:");
            foreach (var path in js.AdditionalRefs)
            {
                sb.AppendLine($"    - {path}");
            }
        }
        if (js.SkipPackages.Count > 0)
        {
            sb.AppendLine("  skipPackages:");
            foreach (var pkg in js.SkipPackages)
            {
                sb.AppendLine($"    - {pkg}");
            }
        }
        if (js.RefOnlyPackages.Count > 0)
        {
            sb.AppendLine("  refOnlyPackages:");
            foreach (var pkg in js.RefOnlyPackages)
            {
                sb.AppendLine($"    - {pkg}");
            }
        }
        if (js.EmitPackages.Count > 0)
        {
            sb.AppendLine("  emitPackages:");
            foreach (var pkg in js.EmitPackages)
            {
                sb.AppendLine($"    - {pkg}");
            }
        }
        if (js.ExtraTypes.Count > 0)
        {
            sb.AppendLine("  extraTypes:");
            foreach (var et in js.ExtraTypes)
            {
                sb.AppendLine($"    - namespace: {et.Namespace}");
                if (et.Classes.Count > 0)
                {
                    sb.AppendLine("      classes:");
                    foreach (var cls in et.Classes)
                    {
                        sb.AppendLine($"        - {cls}");
                    }
                }
            }
        }
    }
}
