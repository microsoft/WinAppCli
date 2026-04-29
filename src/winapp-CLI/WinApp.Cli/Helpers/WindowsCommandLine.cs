// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine.Parsing;
using System.Text;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Utilities for building Windows command-line argument strings.
/// </summary>
/// <remarks>
/// <para>
/// Windows passes a single string to a launched application.  The application calls
/// <c>CommandLineToArgvW</c> (or the equivalent CRT startup code) to split that string
/// back into an <c>argv</c> array.  To round-trip correctly, each token must be encoded
/// so that <c>CommandLineToArgvW</c> recovers the original value exactly.
/// </para>
/// <para>
/// The escaping rules are documented at
/// https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw
/// and summarised as follows:
/// <list type="bullet">
///   <item><c>2n</c> backslashes followed by <c>"</c> → <c>n</c> literal backslashes,
///         the <c>"</c> is a quote delimiter (not literal).</item>
///   <item><c>2n+1</c> backslashes followed by <c>"</c> → <c>n</c> literal backslashes
///         followed by a literal <c>"</c> character.</item>
///   <item><c>n</c> backslashes <em>not</em> followed by <c>"</c> → <c>n</c> literal backslashes.</item>
/// </list>
/// The inverse (encoding) algorithm used here is:
/// <list type="number">
///   <item>If the token is empty, emit <c>""</c>.</item>
///   <item>If the token contains no whitespace or double-quote characters, emit it unchanged
///         (backslashes alone never require quoting).</item>
///   <item>Otherwise wrap in double quotes.  Inside the quoted value:
///     <list type="bullet">
///       <item>Before an embedded <c>"</c>: emit <c>2n+1</c> backslashes where <c>n</c> is the
///             number of immediately preceding backslashes, then the escaped <c>"</c>.</item>
///       <item>Before the closing <c>"</c>: emit <c>2n</c> backslashes where <c>n</c> is the
///             number of trailing backslashes in the token (so the parser does not consume the
///             closing quote).</item>
///       <item>Backslashes not immediately preceding a <c>"</c> are emitted literally.</item>
///     </list>
///   </item>
/// </list>
/// </para>
/// </remarks>
internal static class WindowsCommandLine
{
    /// <summary>
    /// Extracts passthrough arguments from a parsed token stream and identifies
    /// any unrecognised tokens that appeared before the <c>--</c> separator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first <see cref="TokenType.DoubleDash"/> token is treated as the separator.
    /// Everything after it is collected as a passthrough argument for the launched application.
    /// Any subsequent <c>--</c> tokens (e.g. <c>winapp run . -- -- --flag</c>) are forwarded
    /// as the literal string <c>"--"</c>.
    /// </para>
    /// <para>
    /// Because <c>TreatUnmatchedTokensAsErrors</c> must be <see langword="false"/> to allow the
    /// passthrough pattern, the parser cannot distinguish between tokens the user intentionally
    /// placed after <c>--</c> and tokens that are genuinely unrecognised options typed before it.
    /// This method reconciles the two using a count-based approach: for every token value in
    /// <paramref name="unmatchedTokens"/>, one occurrence is consumed from the passthrough budget;
    /// any remainder is classified as unknown.
    /// </para>
    /// </remarks>
    /// <param name="tokens">
    /// The full token list from <see cref="ParseResult.Tokens"/>.
    /// </param>
    /// <param name="unmatchedTokens">
    /// The unmatched token values from <see cref="ParseResult.UnmatchedTokens"/>.
    /// </param>
    /// <returns>
    /// A tuple of:
    /// <list type="bullet">
    ///   <item><c>PassthroughArgs</c> — tokens to forward to the launched application.</item>
    ///   <item><c>UnknownTokens</c> — tokens that appeared before <c>--</c> and were not
    ///         recognised by the parser.</item>
    /// </list>
    /// </returns>
    public static (IReadOnlyList<string> PassthroughArgs, IReadOnlyList<string> UnknownTokens)
        ExtractPassthroughArgs(IEnumerable<Token> tokens, IReadOnlyList<string> unmatchedTokens)
    {
        // Walk the token stream once: the first DoubleDash is the separator; everything after
        // it is a passthrough argument. Subsequent DoubleDash tokens become literal "--" values.
        var passthrough = new List<string>();
        var seenDoubleDash = false;
        foreach (var token in tokens)
        {
            if (!seenDoubleDash && token.Type == TokenType.DoubleDash)
            {
                seenDoubleDash = true;
                continue;
            }

            if (seenDoubleDash)
            {
                passthrough.Add(token.Value);
            }
        }

        // Identify pre-dash unknown tokens by consuming from the passthrough budget.
        // A count-based dictionary handles the case where the same value appears both
        // before '--' (bad) and after '--' (legitimate passthrough) — a naïve set would
        // cancel them out and silently swallow the unknown token.
        var unknown = new List<string>();
        if (unmatchedTokens.Count > 0)
        {
            var budget = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var a in passthrough)
            {
                budget[a] = budget.GetValueOrDefault(a) + 1;
            }

            foreach (var t in unmatchedTokens)
            {
                if (budget.TryGetValue(t, out var count) && count > 0)
                {
                    budget[t] = count - 1;
                }
                else
                {
                    unknown.Add(t);
                }
            }
        }

        return (passthrough, unknown);
    }

    /// <summary>
    /// Encodes a single argument token so that <c>CommandLineToArgvW</c> recovers
    /// the original value exactly.
    /// </summary>
    /// <param name="argument">The raw argument value to encode.</param>
    /// <returns>
    /// A string that, when embedded in a Windows command line, will be parsed back
    /// to <paramref name="argument"/> by <c>CommandLineToArgvW</c>.
    /// </returns>
    public static string EscapeArgument(string argument)
    {
        // An empty token must be represented as "" — a bare empty string would be
        // swallowed by the parser and produce no argument.
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        // Scan for characters that require quoting.  Backslashes alone never need
        // quoting; only whitespace and double-quote force the wrapping.
        var needsQuoting = false;
        foreach (var c in argument)
        {
            if (char.IsWhiteSpace(c) || c == '"')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
        {
            return argument;
        }

        // Build the quoted form character by character, buffering backslash runs
        // so we can apply the 2n / 2n+1 rule when we see what follows them.
        var sb = new StringBuilder(argument.Length + 8);
        sb.Append('"');

        var pendingBackslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                pendingBackslashes++;
                continue;
            }

            if (c == '"')
            {
                // 2n+1 backslashes before the embedded quote: the parser sees n literal
                // backslashes followed by an escaped (literal) double-quote character.
                sb.Append('\\', pendingBackslashes * 2 + 1);
                sb.Append('"');
                pendingBackslashes = 0;
                continue;
            }

            // Any other character: flush buffered backslashes literally, then emit the char.
            if (pendingBackslashes > 0)
            {
                sb.Append('\\', pendingBackslashes);
                pendingBackslashes = 0;
            }

            sb.Append(c);
        }

        // Trailing backslashes sit just before the closing quote character.
        // Apply the 2n rule so the parser does not treat the closing quote as escaped.
        sb.Append('\\', pendingBackslashes * 2);

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Joins a sequence of argument tokens into a single Windows command-line string,
    /// encoding each token with <see cref="EscapeArgument"/> so that
    /// <c>CommandLineToArgvW</c> recovers the original tokens exactly.
    /// </summary>
    /// <param name="arguments">The argument tokens to join.</param>
    /// <returns>
    /// A single command-line string, or <see langword="null"/> if
    /// <paramref name="arguments"/> is empty.
    /// </returns>
    public static string? JoinArguments(IEnumerable<string> arguments)
    {
        var args = arguments.ToList();
        if (args.Count == 0)
        {
            return null;
        }

        return string.Join(" ", args.Select(EscapeArgument));
    }
}
