// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Exercises the ported find-ui search engine + provider registry directly with
/// synthetic scenarios so the tests are hermetic (no GitHub fetch).
/// </summary>
[TestClass]
public class FindUiSearchTests
{
    private static Scenario Scn(string source, string controlId, string controlName, string id, string header, string? desc = null)
        => new()
        {
            Id = id,
            ControlId = controlId,
            ControlName = controlName,
            HeaderText = header,
            Source = source,
            ControlDescription = desc,
            Xaml = $"<{controlName} />",
        };

    private static SearchEngine BuildEngine() => new(
        [
            Scn("gallery", "tabview", "TabView", "tabview-1", "Add, close, and rearrange tabs", "Displays a collection of tabs."),
            Scn("gallery", "colorpicker", "ColorPicker", "colorpicker-1", "ColorPicker properties", "Selectable color spectrum."),
            Scn("toolkit", "colorpicker", "ColorPicker", "colorpicker-1", "Basic usage", "Extended color picker."),
            Scn("toolkit", "datagrid", "DataGrid", "datagrid-1", "Sortable grid", "Tabular data grid."),
        ],
        corePatterns: [],
        enrichmentTags: new(),
        curatedKeywords: new());

    [TestMethod]
    public void SearchGrouped_MatchesControlByName()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("tabview", maxControls: 5);
        Assert.IsTrue(groups.Count > 0, "expected at least one match");
        Assert.AreEqual("TabView", groups[0].ControlName);
        Assert.AreEqual("gallery-tabview-1", groups[0].Scenarios[0].Id);
    }

    [TestMethod]
    public void SearchGrouped_SourceFilter_RestrictsToProvider()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("color picker", maxControls: 5, sourceFilter: "toolkit");
        Assert.IsTrue(groups.Count > 0);
        Assert.IsTrue(groups.All(g => g.Source == "toolkit"), "every result must be from the toolkit source");
    }

    [TestMethod]
    public void SearchGrouped_NoMatch_ReturnsEmpty()
    {
        var engine = BuildEngine();
        var groups = engine.SearchGrouped("zzzznonexistentcontrol", maxControls: 5);
        Assert.AreEqual(0, groups.Count);
    }

    [TestMethod]
    public void GetPattern_ByPrefixedId_ReturnsScenario()
    {
        var engine = BuildEngine();
        var (formatted, found) = engine.GetPattern("gallery-tabview-1");
        Assert.IsTrue(found);
        StringAssert.Contains(formatted, "TabView");
    }

    [TestMethod]
    public void GetPattern_RespectsSourcePrefix_OnCollidingId()
    {
        var engine = BuildEngine();
        // Both gallery and toolkit expose colorpicker-1; the prefix must disambiguate.
        var (gallery, gFound) = engine.GetPattern("gallery-colorpicker-1");
        var (toolkit, tFound) = engine.GetPattern("toolkit-colorpicker-1");
        Assert.IsTrue(gFound);
        Assert.IsTrue(tFound);
        StringAssert.Contains(gallery, "ColorPicker properties");
        StringAssert.Contains(toolkit, "Basic usage");
    }

    [TestMethod]
    public void GetPattern_UnknownId_ReturnsNotFound()
    {
        var engine = BuildEngine();
        var (_, found) = engine.GetPattern("does-not-exist");
        Assert.IsFalse(found);
    }

    [TestMethod]
    public void ListAll_EnumeratesEveryScenario()
    {
        var engine = BuildEngine();
        var ids = engine.ListAll().Select(x => x.id).ToList();
        CollectionAssert.Contains(ids, "gallery-tabview-1");
        CollectionAssert.Contains(ids, "toolkit-datagrid-1");
        Assert.AreEqual(4, ids.Count);
    }

    [TestMethod]
    public void ProviderRegistry_ValidatesSourceFilterValues()
    {
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("gallery"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("toolkit"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("core"));
        Assert.IsTrue(ProviderRegistry.IsValidSourceFilter("GALLERY"), "source filter is case-insensitive");
        Assert.IsFalse(ProviderRegistry.IsValidSourceFilter("wpf"));
    }

    [TestMethod]
    public void ProviderRegistry_ForScenarioId_MapsPrefixToProvider()
    {
        Assert.AreEqual("gallery", ProviderRegistry.ForScenarioId("gallery-tabview-1")?.Id);
        Assert.AreEqual("toolkit", ProviderRegistry.ForScenarioId("toolkit-datagrid-1")?.Id);
        Assert.IsNull(ProviderRegistry.ForScenarioId("core-navview"));
    }
}
