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
    /// Splits a parsed token stream into legitimate post-<c>--</c> passthrough arguments
    /// and invalid pre-<c>--</c> absorbed tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When a command uses a <c>ZeroOrMore</c> positional <see cref="Argument{T}"/> to collect
    /// post-<c>--</c> tokens, System.CommandLine also absorbs any unrecognised option-like tokens
    /// that appear <em>before</em> <c>--</c> into that same argument. This method separates the
    /// two groups without relying on token types (which are <c>Argument</c> for both option values
    /// and positionals):
    /// </para>
    /// <list type="bullet">
    ///   <item><c>PassthroughArgs</c> — tokens from the stream that appeared after <c>--</c>;
    ///         these should be forwarded to the launched application.</item>
    ///   <item><c>InvalidPreDashTokens</c> — entries from <paramref name="allAbsorbedArgs"/> that
    ///         cannot be matched to a post-<c>--</c> token; these are unrecognised tokens that were
    ///         absorbed by the ZeroOrMore argument from before <c>--</c> and should be rejected.</item>
    /// </list>
    /// <para>
    /// Matching is count-based so duplicate values are handled correctly: e.g.,
    /// <c>winapp run . --flag -- --flag</c> yields two <c>--flag</c> entries in
    /// <paramref name="allAbsorbedArgs"/> but only one in the post-dash walk, so exactly one is
    /// classified as invalid.
    /// </para>
    /// <para>
    /// A second <c>--</c> token after the separator is forwarded to the app as the literal
    /// string <c>"--"</c>.
    /// </para>
    /// </remarks>
    /// <param name="tokens">The full token list from <see cref="ParseResult.Tokens"/>.</param>
    /// <param name="allAbsorbedArgs">
    /// All values absorbed by the <c>ZeroOrMore</c> passthrough argument
    /// (i.e., the result of <c>GetValue(PassthroughArgument)</c>).
    /// </param>
    public static (IReadOnlyList<string> PassthroughArgs, IReadOnlyList<string> InvalidPreDashTokens)
        SplitPassthroughTokens(IEnumerable<Token> tokens, IEnumerable<string> allAbsorbedArgs)
    {
        // Walk the token list to find all tokens that appear AFTER the '--' separator.
        var postDash = new List<string>();
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
                postDash.Add(token.Value);
            }
        }

        // Any value in allAbsorbedArgs that cannot be matched to a post-'--' token
        // was absorbed from before '--' and represents an invalid unrecognised option.
        var budget = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in postDash)
        {
            budget[t] = budget.GetValueOrDefault(t) + 1;
        }

        var invalid = new List<string>();
        foreach (var a in allAbsorbedArgs)
        {
            if (budget.TryGetValue(a, out var count) && count > 0)
            {
                budget[a] = count - 1;
            }
            else
            {
                invalid.Add(a);
            }
        }

        return (postDash, invalid);
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
