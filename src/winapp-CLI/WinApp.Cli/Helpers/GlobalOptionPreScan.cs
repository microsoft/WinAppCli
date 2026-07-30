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
                    // Only treat as true/false when the value is a valid boolean.
                    // An unrecognised value (e.g. "bogus") is NOT coerced to true — return
                    // false so the real System.CommandLine parser surfaces the invalid value
                    // and the spurious --json/--verbose conflict is not triggered.
                    if (bool.TryParse(val, out var parsedVal))
                    {
                        return parsedVal;
                    }
                    return false; // indeterminate — real parser will report the error
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

    /// <summary>
    /// Detects a boolean option supplied with an <c>=</c>-attached value that is not a valid
    /// boolean (e.g. <c>--json=bogus</c>) before the first standalone <c>--</c> separator.
    /// </summary>
    /// <remarks>
    /// System.CommandLine silently coerces such an attached value to <see langword="true"/>
    /// (no parse error), while <see cref="GetBooleanFlagValue"/> reads it as <see langword="false"/>.
    /// That disagreement means the human-readable log line (emitted because the pre-scan left
    /// logging un-suppressed) and the machine-readable JSON envelope (emitted because the parsed
    /// option is <see langword="true"/>) would both reach stderr, corrupting <c>--json</c> output.
    /// Callers use this to fail fast with a single clean <c>invalid_arguments</c> error instead,
    /// mirroring how the parser rejects other malformed option values (e.g. <c>--pressure nope</c>).
    /// The bare form, valid <c>--json=true</c>/<c>--json=false</c> spellings, and space-separated
    /// values are not considered invalid here. ALL occurrences before the separator are scanned:
    /// a later invalid value is reported even when an earlier occurrence was valid, so that a
    /// repeated option such as <c>--json=true --json=bogus</c> is still rejected (M2).
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> and sets <paramref name="invalidValue"/> when an invalid attached
    /// value is found; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryFindInvalidBooleanValue(string[] args, string name, IEnumerable<string> aliases, out string invalidValue)
    {
        invalidValue = string.Empty;
        var aliasSet = aliases as ICollection<string> ?? aliases.ToList();

        foreach (var token in args)
        {
            if (token == "--")
            {
                // Passthrough payload begins here — stop scanning.
                return false;
            }

            foreach (var n in new[] { name }.Concat(aliasSet))
            {
                var prefix = n + "=";
                if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var val = token[prefix.Length..];
                    if (!bool.TryParse(val, out _))
                    {
                        // Invalid '='-attached value on this option. Report the first such
                        // occurrence; do NOT early-return false on an earlier VALID occurrence —
                        // e.g. `--json=true --json=bogus` must still be rejected (M2).
                        invalidValue = val;
                        return true;
                    }

                    // Valid boolean attached value for this token — keep scanning the remaining
                    // tokens in case a later duplicate of the same option carries a bad value.
                    break;
                }
            }
        }

        return false;
    }
}
