// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

[TestClass]
public class FindUiCommandTests : BaseCommandTests
{
    private IControlsSearchService _fakeService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        // Resolve the current fake at invoke time so each test can pick engine-vs-unavailable.
        return services.AddSingleton<IControlsSearchService>(_ => _fakeService);
    }

    private static SearchEngine BuildEngine() => new(
        [
            Scn("gallery", "tabview", "TabView", "tabview-1", "Add, close, and rearrange tabs"),
            Scn("toolkit", "datagrid", "DataGrid", "datagrid-1", "Sortable grid"),
        ],
        corePatterns: [],
        enrichmentTags: new(),
        curatedKeywords: new());

    private static Scenario Scn(string source, string controlId, string controlName, string id, string header)
        => new() { Id = id, ControlId = controlId, ControlName = controlName, HeaderText = header, Source = source, Xaml = $"<{controlName} />" };

    private FindUiCommand Command()
    {
        _fakeService ??= FakeControlsSearchService.WithEngine(BuildEngine());
        return GetRequiredService<FindUiCommand>();
    }

    [TestMethod]
    public async Task NoArgs_PrintsGuidance_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), []);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task InvalidSource_Json_EmitsError_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--source", "wpf", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "--source must be one of");
    }

    [TestMethod]
    public async Task MaxZero_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--max", "0"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task SourceWithList_Rejected_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--list", "--source", "toolkit", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "--source only applies to search");
    }

    [TestMethod]
    public async Task Search_Hit_ListsScenarioId_Exit0()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
    }

    [TestMethod]
    public async Task Search_Miss_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["zzzznotacontrol"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task Search_Json_EmitsMatchCount_Exit0()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "\"matchCount\"");
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview-1");
    }

    [TestMethod]
    public async Task Id_Found_Exit0()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "TabView");
    }

    [TestMethod]
    public async Task Id_Unknown_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "nope-xyz"]);
        Assert.AreEqual(1, exit);
    }

    [TestMethod]
    public async Task List_Exit0_ListsScenarios()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--list"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "toolkit-datagrid-1");
    }

    [TestMethod]
    public async Task DataUnavailable_Json_EmitsError_Exit1()
    {
        _fakeService = FakeControlsSearchService.Unavailable();
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "No WinUI control data is available");
    }

    [TestMethod]
    public async Task MultipleModes_Rejected_Exit1()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--list", "--json"]);
        Assert.AreEqual(1, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "Choose one of");
    }

    [TestMethod]
    public async Task Id_MultipleValues_AllFetched()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "gallery-tabview-1", "--id", "toolkit-datagrid-1"]);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "TabView");
        StringAssert.Contains(TestAnsiConsole.Output, "DataGrid");
    }

    [TestMethod]
    public async Task BareIdWithoutValue_Rejected()
    {
        _fakeService = FakeControlsSearchService.WithEngine(BuildEngine());
        // --id requires at least one operand (OneOrMore); a bare --id is a parse error.
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["--id", "--list"]);
        Assert.AreNotEqual(0, exit, "a bare --id with no value must not silently fall through to another mode");
    }

    [TestMethod]
    public async Task Refresh_PassedThroughToService()
    {
        var fake = FakeControlsSearchService.WithEngine(BuildEngine());
        _fakeService = fake;
        var exit = await ParseAndInvokeWithCaptureAsync(Command(), ["tabview", "--refresh"]);
        Assert.AreEqual(0, exit);
        Assert.IsTrue(fake.LastForceRefresh, "--refresh should be forwarded to the search service");
    }
}
