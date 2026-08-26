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

        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            output, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.UpdateAvailable, outcome);
        Assert.AreEqual("0.0.5-alpha", current);
        Assert.AreEqual("0.0.6-alpha", latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_UpToDate_ReturnsNulls()
    {
        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            "All template packages are up-to-date.", "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, outcome);
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

        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            output, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, outcome,
            "A recognizable table listing only other packages means our pack is authoritatively up-to-date.");
        Assert.IsNull(current, "Only the requested package's row must be considered.");
        Assert.IsNull(latest);
    }

    [TestMethod]
    public void ParseUpdateCheck_UnrecognizedOutput_ReturnsUnrecognized()
    {
        // Exit 0 but output we can't interpret (an SDK format change or truncated stdout) must not be
        // mistaken for "up-to-date", otherwise the throttle would cache a non-result for a day.
        var (outcome, current, latest) = WinUiTemplateCatalog.ParseUpdateCheck(
            "Determining projects to restore...\nSome unexpected banner.", "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        Assert.AreEqual(UpdateCheckOutcome.Unrecognized, outcome);
        Assert.IsNull(current);
        Assert.IsNull(latest);
    }

    private const string UninstallOutput =
        "Currently installed items:\n" +
        "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
        "      Version: 0.0.6-alpha\n" +
        "      Details:\n" +
        "         Author: Microsoft\n" +
        "      Templates:\n" +
        "         WinUI Blank App (winui,winui3,wasdk-single) C#\n" +
        "         WinUI Blank Page (Item) (winui-page,winui3-page) C#\n" +
        "      Uninstall Command:\n" +
        "         dotnet new uninstall Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
        "   Contoso.WinUI.Extras\n" +
        "      Version: 1.0.0\n" +
        "      Templates:\n" +
        "         Contoso WinUI Widget (winui-widget) C#\n" +
        "      Uninstall Command:\n" +
        "         dotnet new uninstall Contoso.WinUI.Extras\n";

    private static readonly string[] ExpectedMicrosoftAliases =
        ["winui", "winui3", "wasdk-single", "winui-page", "winui3-page"];

    [TestMethod]
    public void ParsePackTemplateShortNames_ReturnsOnlyTheRequestedPackAliases()
    {
        var owned = WinUiTemplateCatalog.ParsePackTemplateShortNames(
            UninstallOutput, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates");

        // Every alias of the Microsoft pack's templates, including the "(Item)" row whose display name
        // itself contains parentheses (only the last group is the alias list).
        CollectionAssert.AreEquivalent(ExpectedMicrosoftAliases, owned.ToArray());
        Assert.IsFalse(owned.Contains("winui-widget"), "A different pack's template must not be included.");
    }

    [TestMethod]
    public void ParsePackTemplateShortNames_MissingPackage_ReturnsEmpty()
    {
        Assert.AreEqual(0, WinUiTemplateCatalog.ParsePackTemplateShortNames(UninstallOutput, "Not.Installed").Count);
        Assert.AreEqual(0, WinUiTemplateCatalog.ParsePackTemplateShortNames(string.Empty, "Anything").Count);
    }

    private const string PackageId = "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates";

    /// <summary>
    /// A minimal templatecache.json with a WinUI project template (offering net8/9/10 via a
    /// <c>dotnetVersion</c> symbol mapped to the <c>dotnet-version</c> CLI option) and an item template
    /// with no framework choice, both mounted on the Microsoft pack's nupkg.
    /// </summary>
    private const string CacheJson =
        "{\"TemplateInfo\":[" +
        "{\"MountPointUri\":\"C:\\\\Users\\\\me\\\\.templateengine\\\\packages\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.6-alpha.nupkg\"," +
        "\"ShortNameList\":[\"winui\",\"winui3\"]," +
        "\"Parameters\":[" +
        "{\"Name\":\"name\",\"DataType\":\"string\"}," +
        "{\"Name\":\"dotnetVersion\",\"DataType\":\"choice\",\"Choices\":{\"net8.0\":{},\"net9.0\":{},\"net10.0\":{}}}]," +
        "\"HostData\":\"{\\\"symbolInfo\\\":{\\\"dotnetVersion\\\":{\\\"longName\\\":\\\"dotnet-version\\\",\\\"shortName\\\":\\\"tfm\\\"}}}\"}," +
        "{\"MountPointUri\":\"C:\\\\Users\\\\me\\\\.templateengine\\\\packages\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.6-alpha.nupkg\"," +
        "\"ShortNameList\":[\"winui-page\"]," +
        "\"Parameters\":[{\"Name\":\"name\",\"DataType\":\"string\"}]," +
        "\"HostData\":\"{\\\"symbolInfo\\\":{}}\"}]}";

    [TestMethod]
    public void DeriveTfmOption_ExactSdkTfmOffered_PinsThatFramework()
    {
        var (found, option, tfm) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui", 8);

        Assert.IsTrue(found, "The template belongs to the pack and is present in the cache.");
        Assert.AreEqual("dotnet-version", option, "The CLI option must come from the host longName, not a hard-coded name.");
        Assert.AreEqual("net8.0", tfm, "When the SDK's own TFM is offered, pin exactly that.");
    }

    [TestMethod]
    public void DeriveTfmOption_SdkNewerThanEveryChoice_PinsHighestOfferedFramework()
    {
        // SDK 11 with a pack that only offers up to net10.0: pin the highest supported TFM (net10.0)
        // instead of silently omitting the option and inheriting whatever the template defaults to.
        var (found, option, tfm) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui", 11);

        Assert.IsTrue(found);
        Assert.AreEqual("dotnet-version", option);
        Assert.AreEqual("net10.0", tfm);
    }

    [TestMethod]
    public void DeriveTfmOption_NoHostMapping_FallsBackToRawSymbolName()
    {
        // A pack that exposes the symbol with no host longName mapping: dotnet surfaces it under the raw
        // symbol name (--dotnetVersion), which is exactly the older-pack case a hard-coded option missed.
        const string noHostMapping =
            "{\"TemplateInfo\":[{" +
            "\"MountPointUri\":\"x\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.0.0.5-alpha.nupkg\"," +
            "\"ShortNameList\":[\"winui-mvvm\"]," +
            "\"Parameters\":[{\"Name\":\"dotnetVersion\",\"DataType\":\"choice\",\"Choices\":{\"net8.0\":{},\"net9.0\":{},\"net10.0\":{}}}]," +
            "\"HostData\":\"{\\\"symbolInfo\\\":{}}\"}]}";

        var (found, option, tfm) = WinUiTemplateCatalog.DeriveTfmOption(noHostMapping, PackageId, "winui-mvvm", 9);

        Assert.IsTrue(found);
        Assert.AreEqual("dotnetVersion", option, "With no host mapping the raw symbol name is the option dotnet exposes.");
        Assert.AreEqual("net9.0", tfm);
    }

    [TestMethod]
    public void DeriveTfmOption_TemplateWithoutFrameworkChoice_FoundButNothingToPin()
    {
        var (found, option, tfm) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui-page", 8);

        Assert.IsTrue(found, "The item template is present in the cache.");
        Assert.IsNull(option, "An item template declares no framework, so there is no option to pass.");
        Assert.IsNull(tfm);
    }

    [TestMethod]
    public void DeriveTfmOption_SdkOlderThanEveryChoice_FoundButNoTfm()
    {
        // Pack offers only net9/net10 but SDK is 8: no offered framework is buildable by this SDK, so
        // there is nothing safe to pin (the option is left off and the template picks its own default).
        const string minNine =
            "{\"TemplateInfo\":[{" +
            "\"MountPointUri\":\"x\\\\Microsoft.WindowsAppSDK.WinUI.CSharp.Templates.9.9.9.nupkg\"," +
            "\"ShortNameList\":[\"winui\"]," +
            "\"Parameters\":[{\"Name\":\"dotnetVersion\",\"DataType\":\"choice\",\"Choices\":{\"net9.0\":{},\"net10.0\":{}}}]," +
            "\"HostData\":\"{\\\"symbolInfo\\\":{\\\"dotnetVersion\\\":{\\\"longName\\\":\\\"dotnet-version\\\"}}}\"}]}";

        var (found, option, tfm) = WinUiTemplateCatalog.DeriveTfmOption(minNine, PackageId, "winui", 8);

        Assert.IsTrue(found);
        Assert.AreEqual("dotnet-version", option);
        Assert.IsNull(tfm, "No offered framework is <= the SDK major, so nothing is pinned.");
    }

    [TestMethod]
    public void DeriveTfmOption_TemplateNotInThisCache_ReturnsNotFound()
    {
        // Wrong package (mount point isn't the Microsoft pack) and unknown short name both mean "keep
        // looking" — the caller then tries the next cache file or the heuristic.
        var (foundWrongPackage, _, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, "Some.Other.Pack", "winui", 9);
        Assert.IsFalse(foundWrongPackage);

        var (foundWrongShort, _, _) = WinUiTemplateCatalog.DeriveTfmOption(CacheJson, PackageId, "winui-nope", 9);
        Assert.IsFalse(foundWrongShort);

        var (foundEmpty, _, _) = WinUiTemplateCatalog.DeriveTfmOption(string.Empty, PackageId, "winui", 9);
        Assert.IsFalse(foundEmpty);

        var (foundMalformed, _, _) = WinUiTemplateCatalog.DeriveTfmOption("{ not json", PackageId, "winui", 9);
        Assert.IsFalse(foundMalformed);
    }
}
