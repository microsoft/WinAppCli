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
}
