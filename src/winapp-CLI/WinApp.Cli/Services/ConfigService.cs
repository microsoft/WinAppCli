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
        var yaml = Stringify(cfg);
        File.WriteAllText(ConfigPath.FullName, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ConfigPath.Refresh();
    }

    // internal for direct unit-test coverage of the scalar sanitization rules.
    internal static WinappConfig Parse(string yaml)
    {
        var cfg = new WinappConfig();
        using var sr = new StringReader(yaml);
        string? line;
        string? currentName = null;
        while ((line = sr.ReadLine()) != null)
        {
            var t = line.Trim();
            if (t.StartsWith('#') || t.Length == 0)
            {
                continue;
            }

            if (t.Equals("packages:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (t.StartsWith("- name:", StringComparison.OrdinalIgnoreCase))
            {
                currentName = SanitizeScalar(t.Substring("- name:".Length));
            }
            else if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                currentName = SanitizeScalar(t.Substring("name:".Length));
            }
            else if (t.StartsWith("version:", StringComparison.OrdinalIgnoreCase) && currentName is not null)
            {
                var version = SanitizeScalar(t.Substring("version:".Length));
                cfg.Packages.Add(new PackagePin { Name = currentName, Version = version });
                currentName = null;
            }
        }
        return cfg;
    }

    // Sanitize a YAML scalar following `name:` / `version:`:
    //   * Strip a whitespace-prefixed inline `#` comment, when outside any quoted scalar.
    //   * Strip a matching pair of outer single or double quotes.
    //   * For single-quoted scalars, unescape `''` → `'` per the YAML spec.
    // Must stay in lockstep with the TS `sanitizeScalar` in
    // `src/winapp-npm/src/jsbindings/yaml-packages-hash.ts` so that the npm
    // JS-bindings stale-lockfile check (which re-parses winapp.yaml on the JS
    // side) computes the same hash as the native restore that wrote the lockfile.
    internal static string SanitizeScalar(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }
        var opener = (trimmed[0] == '"' || trimmed[0] == '\'') ? trimmed[0] : '\0';
        var trackQuoteState = opener != '\0';

        int cutoff = trimmed.Length;
        bool inSingle = false;
        bool inDouble = false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (trackQuoteState)
            {
                if (inDouble)
                {
                    // Honor `\X` escapes inside double-quoted scalars so an
                    // escaped quote can't be mistaken for the closing quote.
                    if (c == '\\' && i + 1 < trimmed.Length)
                    {
                        i++;
                        continue;
                    }
                    if (c == '"')
                    {
                        inDouble = false;
                    }
                    continue;
                }
                if (inSingle)
                {
                    if (c == '\'')
                    {
                        inSingle = false;
                    }
                    continue;
                }
                if (c == '"')
                {
                    inDouble = true;
                    continue;
                }
                if (c == '\'')
                {
                    inSingle = true;
                    continue;
                }
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

        var value = trimmed.Substring(0, cutoff).TrimEnd();
        if (value.Length >= 2 && opener != '\0' && value[0] == opener && value[value.Length - 1] == opener)
        {
            var inner = value.Substring(1, value.Length - 2);
            if (opener == '\'')
            {
                return inner.Replace("''", "'");
            }
            return inner;
        }
        return value;
    }

    private static string Stringify(WinappConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("packages:");
        foreach (var p in cfg.Packages)
        {
            sb.AppendLine($"  - name: {p.Name}");
            sb.AppendLine($"    version: {p.Version}");
        }
        return sb.ToString();
    }
}
