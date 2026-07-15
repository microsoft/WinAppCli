// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Scans a raw argv array to detect whether a global boolean option (e.g.,
/// <c>--verbose</c>, <c>--quiet</c>, <c>--json</c>) was specified, before a real
/// System.CommandLine parse has been performed.
/// </summary>
/// <remarks>
/// The CLI configures the logger from these flags before parsing so that early
/// telemetry / first-run notices respect the level. A naïve <c>args.Contains(...)</c>
/// would treat tokens after a standalone <c>--</c> separator as winapp options even
/// though they are passthrough arguments for a subcommand
/// (e.g., <c>winapp run . -- --json</c> would erroneously enable JSON mode). This
/// helper stops scanning at the first <c>--</c>.
/// </remarks>
internal static class GlobalOptionPreScan
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="args"/> contains a token equal
    /// to <paramref name="name"/> or any of its <paramref name="aliases"/>, located
    /// before the first standalone <c>--</c> separator.
    /// </summary>
    public static bool IsFlagPresent(string[] args, string name, IEnumerable<string> aliases)
    {
        // Materialise aliases once so multi-pass `Contains` is cheap and behaves
        // identically across collection types (Option.Aliases is ICollection).
        var aliasSet = aliases as ICollection<string> ?? aliases.ToList();

        foreach (var token in args)
        {
            // Stop scanning at the first standalone '--' separator: anything after it
            // is passthrough payload for the subcommand, not a winapp global option.
            if (token == "--")
            {
                return false;
            }

            if (token == name || aliasSet.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the effective boolean value of a boolean option, understanding all spellings
    /// that System.CommandLine accepts:
    /// <list type="bullet">
    ///   <item><c>--json</c> (bare flag) → <see langword="true"/></item>
    ///   <item><c>--json true</c> → <see langword="true"/></item>
    ///   <item><c>--json false</c> → <see langword="false"/></item>
    ///   <item><c>--json=true</c> → <see langword="true"/></item>
    ///   <item><c>--json=false</c> → <see langword="false"/></item>
    ///   <item>absent → <see langword="false"/> (the default)</item>
    /// </list>
    /// Stops scanning at the first standalone <c>--</c> separator.
    /// </summary>
    public static bool GetBooleanFlagValue(string[] args, string name, IEnumerable<string> aliases)
    {
        var aliasSet = aliases as ICollection<string> ?? aliases.ToList();

        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--")
            {
                return false;
            }

            // --json=true / --json=false (attached value with =)
            foreach (var n in new[] { name }.Concat(aliasSet))
            {
                var prefix = n + "=";
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var val = token[prefix.Length..];
                    return !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                }
            }

            // --json / alias (bare or space-separated value)
            if (token == name || aliasSet.Contains(token))
            {
                // Peek at the next token for an explicit true/false value.
                if (i + 1 < args.Length && args[i + 1] != "--")
                {
                    var next = args[i + 1];
                    if (next.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    if (next.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return true; // bare flag = true
            }
        }

        return false; // absent = false
    }
}
