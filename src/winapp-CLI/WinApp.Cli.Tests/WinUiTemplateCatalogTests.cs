// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for <see cref="WinUiTemplateCatalog"/>, which scrapes the fixed-width tables emitted by
/// <c>dotnet new list</c> and <c>dotnet new update --check-only</c> (there is no machine-readable
/// output). These pin the brittle table parsing independently of the command orchestration.
/// </summary>
[TestClass]
public class WinUiTemplateCatalogTests
{
    private const string ListOutput =
        "These templates matched your input: 'winui'.\n" +
        "\n" +
        "Template Name             Short Name                                     Language  Type     Author     Tags                      \n" +
        "------------------------  ---------------------------------------------  --------  -------  ---------  --------------------------\n" +
        "WinUI Blank App           winui,winui3,wasdk-single                      [C#]      project  Microsoft  Windows/WinUI/Desktop/XAML\n" +
        "WinUI Class Library       winui-lib,winui3-lib,wasdk-classlib            [C#]      project  Microsoft  Windows/WinUI/Library     \n" +
        "WinUI Blank Page          winui-page                                     [C#]      item     Microsoft  Windows/WinUI/Item/Page   \n";

    [TestMethod]
    public void ParseList_ReturnsEveryTemplateRow()
    {
        var entries = WinUiTemplateCatalog.ParseList(ListOutput);

        Assert.AreEqual(3, entries.Count, "Every data row under the header must be parsed.");
        Assert.AreEqual("winui", entries[0].ShortName);
        Assert.AreEqual("WinUI Blank App", entries[0].DisplayName);
        Assert.AreEqual("project", entries[0].Type);
        Assert.AreEqual("Windows/WinUI/Desktop/XAML", entries[0].Tags);
    }

    [TestMethod]
    public void ParseList_CanonicalShortNameIsFirstAlias_AndAllAliasesMatch()
    {
        var lib = WinUiTemplateCatalog.ParseList(ListOutput)[1];

        Assert.AreEqual("winui-lib", lib.ShortName, "The canonical short name is the first listed alias.");
        Assert.IsTrue(lib.MatchesShortName("wasdk-classlib"), "Any listed alias must match.");
        Assert.IsTrue(lib.MatchesShortName("WINUI-LIB"), "Matching is case-insensitive.");
        Assert.IsFalse(lib.MatchesShortName("winui"), "A different template's short name must not match.");
    }

    [TestMethod]
    public void ParseList_ClassifiesProjectAndItemTemplates()
    {
        var entries = WinUiTemplateCatalog.ParseList(ListOutput);

        Assert.IsTrue(entries[0].IsProject);
        Assert.IsFalse(entries[0].IsItem);
        Assert.IsTrue(entries[2].IsItem, "An item-typed row must be classified as an item template.");
        Assert.IsFalse(entries[2].IsProject);
    }

    [TestMethod]
    public void ParseList_NoTable_ReturnsEmpty()
    {
        Assert.AreEqual(0, WinUiTemplateCatalog.ParseList("No templates found.").Count);
        Assert.AreEqual(0, WinUiTemplateCatalog.ParseList(string.Empty).Count);
    }

    [TestMethod]
    public void ParseUpdateCheck_ReturnsCurrentAndLatestForPackage()
    {
        const string output =
            "Package                                          Current      Latest\n" +
            "-----------------------------------------------  -----------  -----------\n" +
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates   0.0.5-alpha  0.0.6-alpha\n";

        var (current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            output, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual("0.0.5-alpha", current);
        Assert.AreEqual("0.0.6-alpha", latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_UpToDate_ReturnsNulls()
    {
        var (current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            "All template packages are up-to-date.", "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.IsNull(current);
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_DifferentPackage_IsIgnored()
    {
        const string output =
            "Package        Current  Latest\n" +
            "-------------  -------  ------\n" +
            "Some.Other.Id  1.0.0    2.0.0\n";

        var (current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            output, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.IsNull(current, "Only the requested package's row must be considered.");
        Assert.IsNull(latest);
    }
}
