// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Hermetic tests for <see cref="ScenarioSanitizer"/> — the single corpus-boundary guard.
/// Covers the two review findings it closes: terminal-control-character stripping (H2) and
/// dropping structurally-broken XAML / brace-unbalanced C# (H1).
/// </summary>
[TestClass]
public class ScenarioSanitizerTests
{
    // ── XAML well-formedness ────────────────────────────────────────────────

    [TestMethod]
    public void XamlIsWellFormed_UndeclaredPrefixes_TreatedAsValid()
    {
        // Real snippets use controls:/x:/muxc: prefixes they never declare — that must
        // not be read as a structural fault.
        var xaml = "<controls:ControlExample x:Name=\"Demo\"><muxc:ItemsRepeater /></controls:ControlExample>";
        Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_MultipleTopLevelElements_Valid()
    {
        var xaml = "<TextBox Text=\"a\" />\n<Button Content=\"b\" />";
        Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_MismatchedEndTag_Invalid()
    {
        // The H1 corruption pattern: a fabricated self-closing tag / mismatched nesting.
        var xaml = "<AppBarButton><AppBarButton.KeyboardAccelerators /></AppBarButton></AppBarButton.KeyboardAccelerators>";
        Assert.IsFalse(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_RawAmpersand_Invalid()
    {
        var xaml = "<TextBlock Text=\"a & b\" />";
        Assert.IsFalse(ScenarioSanitizer.XamlIsWellFormed(xaml));
    }

    [TestMethod]
    public void XamlIsWellFormed_TruncationRepairedMarkup_Valid()
    {
        // TruncateXaml appends closers + a trailing comment; the repaired result must parse.
        var truncated = ControlSnippetText.TruncateXaml(
            "<StackPanel><Grid><TextBox Text=\"xxxxxxxxxxxxxxxxxxxxxxxxxxxx\" /></Grid></StackPanel>", 30);
        Assert.IsTrue(ScenarioSanitizer.XamlIsWellFormed(truncated), $"repaired XAML should parse:\n{truncated}");
    }

    // ── C# brace balance ────────────────────────────────────────────────────

    [TestMethod]
    public void CSharpBracesBalanced_Balanced_True()
    {
        Assert.IsTrue(ScenarioSanitizer.CSharpBracesBalanced("void M() { if (x) { Do(); } }"));
    }

    [TestMethod]
    public void CSharpBracesBalanced_TruncatedMidBlock_False()
    {
        Assert.IsFalse(ScenarioSanitizer.CSharpBracesBalanced("void M()\n{\n    ThumbnailDetailsTextBlock.Text ="));
    }

    [TestMethod]
    public void CSharpBracesBalanced_BracesInStringsAndComments_Ignored()
    {
        Assert.IsTrue(ScenarioSanitizer.CSharpBracesBalanced(
            "var s = \"a { b }\"; // trailing { comment\nvar v = @\"x } y\";"));
    }

    [TestMethod]
    public void CSharpBracesBalanced_ExtraClose_False()
    {
        Assert.IsFalse(ScenarioSanitizer.CSharpBracesBalanced("Do(); } More();"));
    }

    // ── Sanitize(Scenario) integration ──────────────────────────────────────

    [TestMethod]
    public void Sanitize_StripsEscapeAndOscSequences_FromEmittedFields()
    {
        // H2: a poisoned upstream sample carrying an OSC title-set + SGR color escape.
        var poison = "\u001b]0;PWNED\u0007code\u001b[41m more";
        var s = new Scenario
        {
            Id = "gallery-x-1",
            ControlName = "X\u001b[31m",
            HeaderText = "H\u0007",
            Xaml = "<TextBox />" + poison,
            CSharp = "var x = 1;" + poison,
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsFalse(s.ControlName.Contains('\u001b'), "ESC stripped from control name");
        Assert.IsFalse(s.HeaderText.Contains('\u0007'), "BEL stripped from header");
        // XAML with the trailing escapes stripped is still a well-formed <TextBox /> → kept, clean.
        Assert.IsNotNull(s.Xaml);
        Assert.IsFalse(s.Xaml!.Contains('\u001b'), "ESC stripped from XAML");
        Assert.IsFalse(s.Xaml!.Contains('\u0007'), "BEL stripped from XAML");
        Assert.IsNotNull(s.CSharp);
        Assert.IsFalse(s.CSharp!.Contains('\u001b'), "ESC stripped from C#");
    }

    [TestMethod]
    public void Sanitize_KeepsNewlinesAndTabs()
    {
        var s = new Scenario { Id = "gallery-x-1", Xaml = "<Grid>\n\t<TextBox />\n</Grid>" };
        ScenarioSanitizer.Sanitize(s);
        Assert.IsNotNull(s.Xaml);
        StringAssert.Contains(s.Xaml!, "\n");
        StringAssert.Contains(s.Xaml!, "\t");
    }

    [TestMethod]
    public void Sanitize_DropsMalformedXaml_ButKeepsValidCSharp()
    {
        var s = new Scenario
        {
            Id = "gallery-x-1",
            Xaml = "<AppBarButton></Foo>",
            CSharp = "void M() { Do(); }",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsNull(s.Xaml, "malformed XAML is dropped");
        Assert.IsNotNull(s.CSharp, "valid C# is retained");
    }

    [TestMethod]
    public void Sanitize_DropsUnbalancedCSharp_ButKeepsValidXaml()
    {
        var s = new Scenario
        {
            Id = "toolkit-x-1",
            Xaml = "<TextBox />",
            CSharp = "void M()\n{\n    var x =",
        };

        ScenarioSanitizer.Sanitize(s);

        Assert.IsNotNull(s.Xaml, "valid XAML is retained");
        Assert.IsNull(s.CSharp, "brace-unbalanced C# is dropped");
    }
}
