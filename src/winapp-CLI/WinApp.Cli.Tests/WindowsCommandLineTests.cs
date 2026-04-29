// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine.Parsing;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="WindowsCommandLine"/>.
/// </summary>
/// <remarks>
/// Expected values in these tests are derived directly from the encoding rules
/// documented for <c>CommandLineToArgvW</c>:
/// https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw
/// <list type="bullet">
///   <item><c>2n</c> backslashes + <c>"</c> → <c>n</c> backslashes + quote delimiter</item>
///   <item><c>2n+1</c> backslashes + <c>"</c> → <c>n</c> backslashes + literal <c>"</c></item>
///   <item><c>n</c> backslashes (not before <c>"</c>) → <c>n</c> literal backslashes</item>
/// </list>
/// </remarks>
[TestClass]
public class WindowsCommandLineTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // EscapeArgument — no-quoting cases
    // ──────────────────────────────────────────────────────────────────────────

    #region EscapeArgument — no quoting needed

    [TestMethod]
    public void EscapeArgument_EmptyString_ReturnsEmptyQuotedPair()
    {
        // An empty token would be lost in the parser; it must be ""
        Assert.AreEqual("\"\"", WindowsCommandLine.EscapeArgument(string.Empty));
    }

    [TestMethod]
    public void EscapeArgument_PlainWord_ReturnedUnchanged()
    {
        Assert.AreEqual("hello", WindowsCommandLine.EscapeArgument("hello"));
    }

    [TestMethod]
    public void EscapeArgument_FlagLikeToken_ReturnedUnchanged()
    {
        // -- prefixes and / switches contain no special chars
        Assert.AreEqual("--flag", WindowsCommandLine.EscapeArgument("--flag"));
        Assert.AreEqual("/flag", WindowsCommandLine.EscapeArgument("/flag"));
    }

    [TestMethod]
    public void EscapeArgument_BackslashesAloneNoSpacesOrQuotes_ReturnedUnchanged()
    {
        // A Windows path with no spaces or double-quotes should pass through verbatim.
        // Backslashes by themselves never need quoting.
        Assert.AreEqual(@"C:\Windows\System32", WindowsCommandLine.EscapeArgument(@"C:\Windows\System32"));
    }

    [TestMethod]
    public void EscapeArgument_TrailingBackslashNoSpacesOrQuotes_ReturnedUnchanged()
    {
        // Trailing backslash on a path that does not require quoting is left as-is.
        Assert.AreEqual(@"C:\trailing\", WindowsCommandLine.EscapeArgument(@"C:\trailing\"));
    }

    [TestMethod]
    public void EscapeArgument_MultipleBackslashesNoSpecialChars_ReturnedUnchanged()
    {
        Assert.AreEqual(@"\\server\share", WindowsCommandLine.EscapeArgument(@"\\server\share"));
    }

    #endregion

    // ──────────────────────────────────────────────────────────────────────────
    // EscapeArgument — whitespace-triggered quoting
    // ──────────────────────────────────────────────────────────────────────────

    #region EscapeArgument — whitespace triggers quoting

    [TestMethod]
    public void EscapeArgument_SpaceInValue_GetsQuoted()
    {
        Assert.AreEqual("\"hello world\"", WindowsCommandLine.EscapeArgument("hello world"));
    }

    [TestMethod]
    public void EscapeArgument_TabInValue_GetsQuoted()
    {
        Assert.AreEqual("\"hello\tworld\"", WindowsCommandLine.EscapeArgument("hello\tworld"));
    }

    [TestMethod]
    public void EscapeArgument_LeadingAndTrailingSpaces_GetsQuoted()
    {
        Assert.AreEqual("\" hello world \"", WindowsCommandLine.EscapeArgument(" hello world "));
    }

    [TestMethod]
    public void EscapeArgument_PathWithSpaceNoTrailingBackslash_GetsQuoted()
    {
        // C:\my path  →  "C:\my path"
        // Backslashes inside are not before a quote, so emit literally.
        Assert.AreEqual("\"C:\\my path\"", WindowsCommandLine.EscapeArgument(@"C:\my path"));
    }

    [TestMethod]
    public void EscapeArgument_PathWithSpaceAndTrailingBackslash_TrailingBackslashDoubled()
    {
        // C:\my path\  →  "C:\my path\\"
        // The trailing \ precedes the closing ", so it must be doubled (2n rule: 1 → 2).
        // CommandLineToArgvW would otherwise treat \" as an escaped quote, not a closer.
        Assert.AreEqual("\"C:\\my path\\\\\"", WindowsCommandLine.EscapeArgument(@"C:\my path\"));
    }

    [TestMethod]
    public void EscapeArgument_PathWithSpaceAndMultipleTrailingBackslashes_AllDoubled()
    {
        // C:\my path\\  →  "C:\my path\\\\"
        // 2 trailing backslashes before the closing " → 4 backslashes (2*2 rule).
        Assert.AreEqual("\"C:\\my path\\\\\\\\\"", WindowsCommandLine.EscapeArgument(@"C:\my path\\"));
    }

    #endregion

    // ──────────────────────────────────────────────────────────────────────────
    // EscapeArgument — double-quote in value
    // ──────────────────────────────────────────────────────────────────────────

    #region EscapeArgument — embedded double-quotes

    [TestMethod]
    public void EscapeArgument_EmbeddedDoubleQuote_Escaped()
    {
        // say "hi"  →  "say \"hi\""
        // Each embedded " gets a \, no preceding backslashes so it is simply \".
        Assert.AreEqual("\"say \\\"hi\\\"\"", WindowsCommandLine.EscapeArgument("say \"hi\""));
    }

    [TestMethod]
    public void EscapeArgument_OnlyDoubleQuote_Escaped()
    {
        // The token is just a double-quote character: "
        // It triggers quoting; the " itself needs escaping → "\""
        Assert.AreEqual("\"\\\"\"", WindowsCommandLine.EscapeArgument("\""));
    }

    [TestMethod]
    public void EscapeArgument_BackslashImmediatelyBeforeEmbeddedQuote_BackslashDoubledPlusEscape()
    {
        // Token: before\"after  (one backslash immediately before the embedded ")
        // 2n+1 rule: 1 backslash → emit 1*2+1 = 3 backslashes then "
        // Result: "before\\\"after"
        Assert.AreEqual("\"before\\\\\\\"after\"", WindowsCommandLine.EscapeArgument("before\\\"after"));
    }

    [TestMethod]
    public void EscapeArgument_TwoBackslashesBeforeEmbeddedQuote_AllDoubledPlusEscape()
    {
        // Token: before\\"after  (two backslashes immediately before the embedded ")
        // 2n+1 rule: 2 backslashes → emit 2*2+1 = 5 backslashes then "
        // Result: "before\\\\\"after"   (in C# literal: "\"before\\\\\\\\\\\"after\"")
        Assert.AreEqual("\"before\\\\\\\\\\\"after\"", WindowsCommandLine.EscapeArgument("before\\\\\"after"));
    }

    [TestMethod]
    public void EscapeArgument_TrailingEmbeddedQuoteAfterBackslash()
    {
        // Token: path\"  (path, backslash, then double-quote at end)
        // The trailing " also triggers quoting.
        // The backslash before ": 2n+1=3 backslashes + escaped ".
        // No trailing backslashes after the ", so closing quote is plain.
        // Result: "path\\\""
        Assert.AreEqual("\"path\\\\\\\"\"", WindowsCommandLine.EscapeArgument("path\\\""));
    }

    [TestMethod]
    public void EscapeArgument_PathWithSpaceEmbeddedQuoteAndTrailingBackslash()
    {
        // Token value: C:\temp\bin "quoted"  (the real-world test case from the PR review)
        // Has space (→ quote) and embedded quotes.
        // Trace:
        //   C:\temp\bin  → C:\temp\bin  (backslashes not before ", emit literally)
        //   (space)      → (space)
        //   "            → \" (0 preceding backslashes → 0*2+1=1 backslash + ")
        //   quoted       → quoted
        //   "            → \" (0 preceding backslashes → \")
        // Closing: 0 trailing backslashes → plain "
        // Result: "C:\temp\bin \"quoted\""
        // In C#:  "\"C:\\temp\\bin \\\"quoted\\\"\""
        Assert.AreEqual(
            "\"C:\\temp\\bin \\\"quoted\\\"\"",
            WindowsCommandLine.EscapeArgument(@"C:\temp\bin ""quoted"""));
    }

    #endregion

    // ──────────────────────────────────────────────────────────────────────────
    // EscapeArgument — round-trip via CommandLineToArgvW
    // ──────────────────────────────────────────────────────────────────────────

    #region EscapeArgument — round-trip via CommandLineToArgvW

    [TestMethod]
    [DataRow("")]
    [DataRow("hello")]
    [DataRow("--flag")]
    [DataRow("hello world")]
    [DataRow("hello\tworld")]
    [DataRow(@"C:\Windows\System32")]
    [DataRow(@"C:\my path\")]
    [DataRow(@"C:\my path\\")]
    [DataRow("say \"hi\"")]
    [DataRow("before\\\"after")]
    [DataRow("before\\\\\"after")]
    [DataRow(@"\\server\share with spaces")]
    [DataRow("trailing\\\\")]
    public void EscapeArgument_RoundTrip_CommandLineToArgvW_RecoverOriginal(string original)
    {
        // Arrange
        var escaped = WindowsCommandLine.EscapeArgument(original);

        // Act — use CommandLineToArgvW (available via Windows.Win32 already in this project) to
        // split the escaped token back.  We embed it in a fake "program.exe {token}" line and
        // take argv[1] so we're not special-casing the first arg.
        var commandLine = $"program.exe {escaped}";
        var argv = CommandLineToArgv(commandLine);

        // Assert
        Assert.AreEqual(2, argv.Length, $"Expected exactly 2 argv entries for command line: {commandLine}");
        Assert.AreEqual(original, argv[1],
            $"Round-trip failed.\n  Original : {original}\n  Escaped  : {escaped}\n  Recovered: {argv[1]}");
    }

    /// <summary>
    /// Calls the real <c>CommandLineToArgvW</c> Win32 API to parse a command line string.
    /// </summary>
    private static string[] CommandLineToArgv(string commandLine)
    {
        var ptr = CommandLineToArgvWNative(commandLine, out var argc);
        if (ptr == IntPtr.Zero)
        {
            throw new InvalidOperationException("CommandLineToArgvW returned null.");
        }

        try
        {
            var result = new string[argc];
            for (var i = 0; i < argc; i++)
            {
                var argPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                result[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(argPtr) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            LocalFreeNative(ptr);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvWNative(string lpCmdLine, out int pNumArgs);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static extern IntPtr LocalFreeNative(IntPtr hMem);

    #endregion

    // ──────────────────────────────────────────────────────────────────────────
    // JoinArguments
    // ──────────────────────────────────────────────────────────────────────────

    #region JoinArguments

    [TestMethod]
    public void JoinArguments_EmptySequence_ReturnsNull()
    {
        Assert.IsNull(WindowsCommandLine.JoinArguments([]));
    }

    [TestMethod]
    public void JoinArguments_SingleSimpleArg_ReturnedUnchanged()
    {
        Assert.AreEqual("--flag", WindowsCommandLine.JoinArguments(["--flag"]));
    }

    [TestMethod]
    public void JoinArguments_MultipleSimpleArgs_JoinedWithSpace()
    {
        Assert.AreEqual("--flag1 --flag2 value", WindowsCommandLine.JoinArguments(["--flag1", "--flag2", "value"]));
    }

    [TestMethod]
    public void JoinArguments_ArgWithSpace_Quoted()
    {
        Assert.AreEqual("--title \"hello world\"", WindowsCommandLine.JoinArguments(["--title", "hello world"]));
    }

    [TestMethod]
    public void JoinArguments_ArgWithEmbeddedQuote_Escaped()
    {
        Assert.AreEqual("--message \"say \\\"hi\\\"\"", WindowsCommandLine.JoinArguments(["--message", "say \"hi\""]));
    }

    [TestMethod]
    public void JoinArguments_EmptyStringArg_RepresentedAsQuotedPair()
    {
        // An empty token mid-list must survive as ""
        Assert.AreEqual("before \"\" after", WindowsCommandLine.JoinArguments(["before", "", "after"]));
    }

    [TestMethod]
    public void JoinArguments_LiteralDoubleDash_PassedThrough()
    {
        // winapp run . -- -- passes a literal "--" as the first app arg
        Assert.AreEqual("--", WindowsCommandLine.JoinArguments(["--"]));
    }

    #endregion

    // ──────────────────────────────────────────────────────────────────────────
    // SplitPassthroughTokens
    // ──────────────────────────────────────────────────────────────────────────

    #region SplitPassthroughTokens

    // Helper: build a minimal Token list from (value, type) pairs.
    private static IEnumerable<Token> MakeTokens(params (string Value, TokenType Type)[] items)
        => items.Select(i => new Token(i.Value, i.Type, default!));

    [TestMethod]
    public void SplitPassthroughTokens_NoDoubleDash_ReturnsEmpty()
    {
        var tokens = MakeTokens((".", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(tokens, []);

        Assert.AreEqual(0, passthrough.Count);
        Assert.AreEqual(0, invalid.Count);
    }

    [TestMethod]
    public void SplitPassthroughTokens_BareDoubleDash_ReturnsEmptyPassthrough()
    {
        var tokens = MakeTokens((".", TokenType.Argument), ("--", TokenType.DoubleDash));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(tokens, []);

        Assert.AreEqual(0, passthrough.Count);
        Assert.AreEqual(0, invalid.Count);
    }

    [TestMethod]
    public void SplitPassthroughTokens_TokensAfterDoubleDash_ReturnedAsPassthrough()
    {
        // allAbsorbed matches postDash exactly → no invalids
        var tokens = MakeTokens(
            (".", TokenType.Argument),
            ("--", TokenType.DoubleDash),
            ("--flag", TokenType.Argument),
            ("value", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(
            tokens, ["--flag", "value"]);

        CollectionAssert.AreEqual(new List<string> { "--flag", "value" }, passthrough.ToList());
        Assert.AreEqual(0, invalid.Count);
    }

    [TestMethod]
    public void SplitPassthroughTokens_SecondDoubleDashAfterSeparator_ForwardedAsLiteralValue()
    {
        // winapp run . -- -- --flag  →  app receives "--" and "--flag"
        var tokens = MakeTokens(
            (".", TokenType.Argument),
            ("--", TokenType.DoubleDash),
            ("--", TokenType.DoubleDash),
            ("--flag", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(
            tokens, ["--", "--flag"]);

        CollectionAssert.AreEqual(new List<string> { "--", "--flag" }, passthrough.ToList());
        Assert.AreEqual(0, invalid.Count);
    }

    [TestMethod]
    public void SplitPassthroughTokens_UnknownTokenAbsorbedBeforeDash_ReportedAsInvalid()
    {
        // winapp run . --bad-opt -- --app-flag
        // allAbsorbed contains both --bad-opt (pre-dash) and --app-flag (post-dash)
        var tokens = MakeTokens(
            (".", TokenType.Argument),
            ("--", TokenType.DoubleDash),
            ("--app-flag", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(
            tokens, ["--bad-opt", "--app-flag"]);

        CollectionAssert.AreEqual(new List<string> { "--app-flag" }, passthrough.ToList());
        CollectionAssert.AreEqual(new List<string> { "--bad-opt" }, invalid.ToList());
    }

    [TestMethod]
    public void SplitPassthroughTokens_UnknownTokenWithNoDoubleDash_ReportedAsInvalid()
    {
        // winapp run . --bad-opt  (no -- at all): allAbsorbed has --bad-opt, postDash empty
        var tokens = MakeTokens((".", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(
            tokens, ["--bad-opt"]);

        Assert.AreEqual(0, passthrough.Count);
        CollectionAssert.AreEqual(new List<string> { "--bad-opt" }, invalid.ToList());
    }

    [TestMethod]
    public void SplitPassthroughTokens_SameValueBeforeAndAfterDash_PreDashCountedAsInvalid()
    {
        // winapp run . --flag -- --flag
        // allAbsorbed = ["--flag", "--flag"], postDash = ["--flag"]
        // The budget has count 1 for "--flag"; one entry matches, the other is invalid.
        var tokens = MakeTokens(
            (".", TokenType.Argument),
            ("--", TokenType.DoubleDash),
            ("--flag", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(
            tokens, ["--flag", "--flag"]);

        CollectionAssert.AreEqual(new List<string> { "--flag" }, passthrough.ToList());
        CollectionAssert.AreEqual(new List<string> { "--flag" }, invalid.ToList());
    }

    [TestMethod]
    public void SplitPassthroughTokens_OptionValueBeforeDash_NotFlaggedAsInvalid()
    {
        // winapp run . --args "--existing" -- --new-flag
        // Option values like "--existing" (value of --args) are NOT in allAbsorbed
        // because they are consumed by their option, not by the ZeroOrMore argument.
        // So SplitPassthroughTokens must not flag them as invalid.
        var tokens = MakeTokens(
            (".", TokenType.Argument),
            ("--args", TokenType.Option),
            ("--existing", TokenType.Argument),  // option value: NOT in allAbsorbed
            ("--", TokenType.DoubleDash),
            ("--new-flag", TokenType.Argument));
        var (passthrough, invalid) = WindowsCommandLine.SplitPassthroughTokens(
            tokens, ["--new-flag"]);  // allAbsorbed only contains the post-dash token

        CollectionAssert.AreEqual(new List<string> { "--new-flag" }, passthrough.ToList());
        Assert.AreEqual(0, invalid.Count);
    }

    #endregion
}
