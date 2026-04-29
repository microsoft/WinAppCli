// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Detects single-dash option typos like <c>-app</c> when a long option <c>--app</c> exists.
/// </summary>
/// <remarks>
/// POSIX short-option bundling is disabled in <see cref="WinAppParserConfiguration"/>, so without
/// this guard the parser would treat <c>-app</c> as the positional <c>selector</c> argument and then
/// emit a confusing "Unrecognized command or argument" error pointing at the next token. This helper
/// catches the case before invocation and surfaces a clearer "Did you mean --app?" message.
/// See issue #467.
/// </remarks>
internal static class OptionTypoValidator
{
    /// <summary>
    /// Returns the offending token (e.g., "-app") when it looks like a long-option typo, or null.
    /// </summary>
    public static string? FindLikelyLongOptionTypo(IReadOnlyList<string> args, ParseResult parseResult)
    {
        var leaf = parseResult.CommandResult.Command;

        // Honor commands that intentionally pass through unknown tokens (e.g. tool, ms-store).
        if (!leaf.TreatUnmatchedTokensAsErrors)
        {
            return null;
        }

        var longNames = CollectLongOptionNames(parseResult.CommandResult);
        if (longNames.Count == 0)
        {
            return null;
        }

        var afterDoubleDash = false;
        foreach (var token in args)
        {
            if (afterDoubleDash) { continue; }
            if (token == "--") { afterDoubleDash = true; continue; }

            // Only consider tokens like "-abc..." (single dash, 2+ chars after).
            if (token.Length <= 2 || token[0] != '-' || token[1] == '-')
            {
                continue;
            }

            var rest = token.AsSpan(1);
            if (!char.IsLetter(rest[0]))
            {
                continue;
            }

            // Stop at '=' so "-app=foo" still flags "-app" against "--app".
            var end = rest.IndexOf('=');
            if (end < 0) { end = rest.Length; }

            var nameSpan = rest[..end];
            var allValid = true;
            for (var i = 0; i < nameSpan.Length; i++)
            {
                var c = nameSpan[i];
                if (!char.IsLetterOrDigit(c) && c != '-')
                {
                    allValid = false;
                    break;
                }
            }
            if (!allValid)
            {
                continue;
            }

            var candidate = "--" + nameSpan.ToString();
            if (longNames.Contains(candidate))
            {
                return "-" + nameSpan.ToString();
            }
        }

        return null;
    }

    private static HashSet<string> CollectLongOptionNames(CommandResult leaf)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        SymbolResult? cursor = leaf;
        while (cursor is CommandResult cr)
        {
            foreach (var opt in cr.Command.Options)
            {
                if (opt.Name.StartsWith("--", StringComparison.Ordinal))
                {
                    names.Add(opt.Name);
                }
                foreach (var alias in opt.Aliases)
                {
                    if (alias.StartsWith("--", StringComparison.Ordinal))
                    {
                        names.Add(alias);
                    }
                }
            }
            cursor = cr.Parent;
        }
        return names;
    }
}
