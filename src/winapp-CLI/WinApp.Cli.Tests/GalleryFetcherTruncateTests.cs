// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ControlSnippetText.TruncateCode"/> — the brace-balanced
/// C# truncation. The key invariant: the emitted snippet must have balanced braces so
/// agents can paste it without a build break, even when the cut lands mid-line after a
/// brace opened between the previous newline and the cap.
/// </summary>
[TestClass]
public class GalleryFetcherTruncateTests
{
    private const string Marker = "// ...truncated";

    private static (int open, int close) BraceCounts(string s)
        => (s.Count(c => c == '{'), s.Count(c => c == '}'));

    [TestMethod]
    public void TruncateCode_ShortInput_ReturnedUnchanged()
    {
        var code = "void M()\n{\n    Do();\n}";
        Assert.AreEqual(code, ControlSnippetText.TruncateCode(code, 1000, Marker));
    }

    [TestMethod]
    public void TruncateCode_CutMidLineAfterOpenBrace_EmitsBalancedBraces()
    {
        // A brace opens on the line AFTER the last newline before the cap; the cut lands
        // mid-line. The synthetic closers must match the depth AT the cut (1, for the
        // outer method brace), not the depth at maxChars (2, incl. the lambda brace) —
        // otherwise an extra '}' is appended and the snippet won't compile.
        var code =
            "void Outer()\n" +
            "{\n" +
            "    Inner(() => { A(); B(); C(); D(); E(); F(); G(); H(); I(); J(); K(); L(); M(); N(); O(); });\n" +
            "}";

        var result = ControlSnippetText.TruncateCode(code, 40, Marker);

        var (open, close) = BraceCounts(result);
        Assert.AreEqual(open, close, $"truncated snippet must have balanced braces. Got:\n{result}");
        StringAssert.Contains(result, Marker);
    }

    [TestMethod]
    public void TruncateCode_CleanCloseWithinCap_CutsAtBalancedBoundary()
    {
        // A complete top-level block ends before the cap; truncation should cut there
        // and the result is balanced with no synthetic closers needed.
        var code =
            "void A()\n{\n    Do();\n}\n\n" +
            "void B()\n{\n    var reallyLongTail = 123456789012345678901234567890;\n    More();\n}";

        var result = ControlSnippetText.TruncateCode(code, 25, Marker);

        var (open, close) = BraceCounts(result);
        Assert.AreEqual(open, close, $"truncated snippet must have balanced braces. Got:\n{result}");
    }

    [TestMethod]
    public void TruncateCode_BraceInsideStringOrComment_NotCounted()
    {
        // Braces inside strings/comments must not skew the depth or the closer count.
        var code =
            "void M()\n" +
            "{\n" +
            "    var s = \"a { b } c\"; // trailing { comment brace\n" +
            "    var padding = 1234567890123456789012345678901234567890;\n" +
            "    Do();\n" +
            "}";

        var result = ControlSnippetText.TruncateCode(code, 45, Marker);

        var (open, close) = BraceCounts(result);
        Assert.AreEqual(open, close, $"string/comment braces must not unbalance output. Got:\n{result}");
    }
}
