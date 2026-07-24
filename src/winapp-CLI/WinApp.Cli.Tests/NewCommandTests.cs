// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the NewCommand: option/argument parsing, template alias mapping, and defaults.
/// </summary>
[TestClass]
public class NewCommandTests : BaseCommandTests
{
    [TestMethod]
    public void Parse_NoArgs_HasNoErrors()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse([]);

        Assert.IsEmpty(parseResult.Errors,
            $"'winapp new' with no args should parse. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_NameOption_BindsName()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--name", "MyApp"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("MyApp", parseResult.GetValue(NewCommand.NameOption));
    }

    [TestMethod]
    [DataRow("blank", nameof(WinUiTemplate.Blank))]
    [DataRow("navview", nameof(WinUiTemplate.NavView))]
    [DataRow("tabview", nameof(WinUiTemplate.TabView))]
    [DataRow("mvvm", nameof(WinUiTemplate.Mvvm))]
    [DataRow("lib", nameof(WinUiTemplate.Lib))]
    [DataRow("unittest", nameof(WinUiTemplate.UnitTest))]
    public void Parse_TemplateAlias_ParsesCaseInsensitively(string alias, string expectedName)
    {
        var command = GetRequiredService<NewCommand>();
        var expected = Enum.Parse<WinUiTemplate>(expectedName);

        var parseResult = command.Parse(["--template", alias]);

        Assert.IsEmpty(parseResult.Errors,
            $"Template alias '{alias}' should parse. Errors: {string.Join("; ", parseResult.Errors)}");
        Assert.AreEqual(expected, parseResult.GetValue(NewCommand.TemplateOption));
    }

    [TestMethod]
    public void Parse_UnknownTemplate_ReportsError()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--template", "bogus"]);

        Assert.IsNotEmpty(parseResult.Errors, "Unknown template value should produce a parse error.");
    }

    [TestMethod]
    [DataRow("99")]   // undefined enum value
    [DataRow("2")]    // numeric alias that would otherwise coerce to TabView
    [DataRow("-1")]
    public void Parse_NumericTemplate_ReportsError(string value)
    {
        // The default enum binder accepts numeric values, silently scaffolding the wrong/blank
        // template. The custom parser must reject anything that isn't one of the six named aliases.
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--template", value]);

        Assert.IsNotEmpty(parseResult.Errors, $"Numeric template '{value}' should produce a parse error, not silently bind an enum.");
    }

    [TestMethod]
    public void Parse_NoPromptAlias_MapsToUseDefaults()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--no-prompt"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.IsTrue(parseResult.GetValue(NewCommand.UseDefaultsOption),
            "--no-prompt should map to the --use-defaults option.");
    }

    [TestMethod]
    [DataRow(nameof(WinUiTemplate.Blank), "winui")]
    [DataRow(nameof(WinUiTemplate.NavView), "winui-navview")]
    [DataRow(nameof(WinUiTemplate.TabView), "winui-tabview")]
    [DataRow(nameof(WinUiTemplate.Mvvm), "winui-mvvm")]
    [DataRow(nameof(WinUiTemplate.Lib), "winui-lib")]
    [DataRow(nameof(WinUiTemplate.UnitTest), "winui-unittest")]
    public void TemplateInfo_MapsToOfficialShortName(string templateName, string expectedShortName)
    {
        var template = Enum.Parse<WinUiTemplate>(templateName);

        var (shortName, label) = NewCommand.TemplateInfo(template);

        Assert.AreEqual(expectedShortName, shortName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(label), "Each template should have a friendly picker label.");
    }

    [TestMethod]
    [DataRow(nameof(WinUiTemplate.Blank), "app")]
    [DataRow(nameof(WinUiTemplate.NavView), "app")]
    [DataRow(nameof(WinUiTemplate.Lib), "class library")]
    [DataRow(nameof(WinUiTemplate.UnitTest), "unit test project")]
    public void ProjectKind_DescribesTheArtifact(string templateName, string expectedKind)
    {
        var template = Enum.Parse<WinUiTemplate>(templateName);

        Assert.AreEqual(expectedKind, NewCommand.ProjectKind(template));
    }

    [TestMethod]
    [DataRow("0.0.6-alpha", true)]
    [DataRow("1.2.3", true)]
    [DataRow("1.0.0-preview.2", true)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("latest", false)]      // does not start with a digit
    [DataRow("1.0 --add-source http://evil", false)] // whitespace / injection shape
    [DataRow("1.0\"", false)]       // quote
    public void IsPlausibleVersion_AcceptsOnlyVersionShapedInput(string version, bool expected)
    {
        Assert.AreEqual(expected, NewCommand.IsPlausibleVersion(version));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_MatchingVersion_ReturnsTrue()
    {
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 0.0.6-alpha\n" +
            "   Details: ...\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_DifferentVersion_ReturnsFalse()
    {
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 0.0.5-alpha\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"),
            "An installed-but-different version must not satisfy an explicit --template-version.");
    }

    [TestMethod]
    public void IsTemplatePackInstalled_NotInstalled_ReturnsFalse()
    {
        var listing = "Some.Other.Template.Pack\n   Version: 1.0.0\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_MatchAfterOtherPackage_ReturnsTrue()
    {
        // The WinUI pack is listed after another package; its own indented Version line must be read.
        var listing =
            "Some.Other.Template.Pack\n" +
            "   Version: 9.9.9\n" +
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 0.0.6-alpha\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_MalformedBlockWithoutVersion_ReturnsFalse()
    {
        // The WinUI pack header has no Version line before the next package header. The next
        // package's version must NOT be misattributed to the WinUI pack.
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "Some.Other.Template.Pack\n" +
            "   Version: 0.0.6-alpha\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"),
            "A following package's version line must not be read as the WinUI pack version.");
    }

    [TestMethod]
    [DataRow("MyApp", true)]
    [DataRow("My.App", true)]
    [DataRow("My_App-1", true)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow(".", false)]
    [DataRow("..", false)]
    [DataRow(@"..\Escaped", false)]     // path traversal
    [DataRow("sub/dir", false)]          // forward-slash separator
    [DataRow(@"C:\rooted", false)]       // rooted path (contains ':' and '\')
    [DataRow("bad*name", false)]         // invalid filename char
    [DataRow("CON", false)]              // reserved device name
    [DataRow("con", false)]              // reserved device name (case-insensitive)
    [DataRow("LPT1", false)]             // reserved device name
    [DataRow("NUL.txt", false)]          // reserved device name with extension
    [DataRow("MyApp.", false)]           // trailing dot (invalid on Windows)
    [DataRow("MyApp ", false)]           // trailing space (invalid on Windows)
    public void IsValidProjectName_RejectsPathSeparatorsAndInvalidChars(string name, bool expected)
    {
        Assert.AreEqual(expected, NewCommand.IsValidProjectName(name));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_RealDotnetFormat_ReturnsTrue()
    {
        // Mirrors actual `dotnet new uninstall` output: a "Currently installed items:" banner, then
        // the package id indented, with Version/Details/Templates nested one level deeper.
        var listing =
            "Currently installed items:\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "      Version: 0.0.6-alpha\n" +
            "      Details:\n" +
            "         Author: Microsoft\n" +
            "      Templates:\n" +
            "         WinUI Blank App (winui) C#\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.7-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_CrlfLineEndings_ReturnsTrue()
    {
        // dotnet output on Windows is CRLF; splitting on '\n' leaves a trailing '\r' that must not
        // break header matching, indentation detection, or version parsing.
        var listing =
            "Currently installed items:\r\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\r\n" +
            "      Version: 0.0.6-alpha\r\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_IndentedNextPackage_DoesNotMisreadVersion()
    {
        // The WinUI pack header has no Version line before the next (equally indented) package. The
        // sibling package's version must NOT be attributed to the WinUI pack.
        var listing =
            "Currently installed items:\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Some.Other.Template.Pack\n" +
            "      Version: 0.0.6-alpha\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"),
            "A sibling package's version line must not be read as the WinUI pack version.");
    }
}
