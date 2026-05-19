// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Exercises the four <c>winapp controls</c> subcommands at the parse-and-invoke
/// boundary using a <see cref="FakeControlsDataService"/> so we never hit the
/// network or the on-disk cache. These tests target happy-path output and the
/// most likely error-path: invalid arguments / not-found ids.
/// </summary>
[TestClass]
public class ControlsCommandTests : BaseCommandTests
{
    private FakeControlsDataService _fake = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fake = new FakeControlsDataService();
        services.AddSingleton<IControlsDataService>(_fake);
        return services;
    }

    // ----- search -----

    [TestMethod]
    public async Task ControlsSearch_HappyPath_PrintsRankedResults()
    {
        var cmd = GetRequiredService<ControlsSearchCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["tabview"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "Found");
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview");
        StringAssert.Contains(TestAnsiConsole.Output, "winapp controls get");
    }

    [TestMethod]
    public async Task ControlsSearch_EmptyQuery_FailsWithError()
    {
        var cmd = GetRequiredService<ControlsSearchCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["   "]);

        Assert.AreEqual(1, exit, "Whitespace-only query must be rejected.");
        StringAssert.Contains(ConsoleStdErr.ToString(), "non-empty query is required");
    }

    [TestMethod]
    public async Task ControlsSearch_InvalidSource_FailsWithError()
    {
        var cmd = GetRequiredService<ControlsSearchCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["tabview", "--source", "bogus"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Invalid --source");
    }

    [TestMethod]
    public async Task ControlsSearch_MaxZero_FailsRatherThanSilentlyCoercing()
    {
        // Regression: previously `--max 0` was silently coerced to 5.
        var cmd = GetRequiredService<ControlsSearchCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["tabview", "--max", "0"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--max must be greater than 0");
    }

    [TestMethod]
    public async Task ControlsSearch_NoMatches_ExitsZeroWithMessage()
    {
        var cmd = GetRequiredService<ControlsSearchCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["zzznosuchcontrol"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "No patterns found");
    }

    // ----- get -----

    [TestMethod]
    public async Task ControlsGet_KnownId_PrintsFormattedScenarioAndExitsZero()
    {
        var cmd = GetRequiredService<ControlsGetCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["gallery-tabview"]);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "TabView");
        StringAssert.Contains(TestAnsiConsole.Output, "**XAML:**");
    }

    [TestMethod]
    public async Task ControlsGet_UnknownId_ExitsNonZeroWithNotFoundMessage()
    {
        var cmd = GetRequiredService<ControlsGetCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["gallery-zzznosuch"]);

        Assert.AreNotEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "not found");
    }

    // ----- list -----

    [TestMethod]
    public async Task ControlsList_NoSourceFilter_PrintsAllGroupHeaders()
    {
        var cmd = GetRequiredService<ControlsListCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, []);

        Assert.AreEqual(0, exit);
        StringAssert.Contains(TestAnsiConsole.Output, "Available patterns");
        StringAssert.Contains(TestAnsiConsole.Output, "gallery-tabview");
    }

    [TestMethod]
    public async Task ControlsList_InvalidSource_Fails()
    {
        var cmd = GetRequiredService<ControlsListCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, ["--source", "bogus"]);

        Assert.AreEqual(1, exit);
        StringAssert.Contains(ConsoleStdErr.ToString(), "Invalid --source");
    }

    // ----- refresh -----

    [TestMethod]
    public async Task ControlsRefresh_HappyPath_CallsClearCacheAndExitsZero()
    {
        var cmd = GetRequiredService<ControlsRefreshCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, []);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(1, _fake.ClearCacheCalls, "ClearCache should be invoked exactly once.");
    }

    [TestMethod]
    public async Task ControlsRefresh_ClearCacheFails_SurfacesNonZeroExitCode()
    {
        // Regression for M4: previously ClearCache swallowed exceptions and refresh
        // always reported success.
        _fake.ClearCacheException = new IOException("simulated lock");
        var cmd = GetRequiredService<ControlsRefreshCommand>();
        var exit = await ParseAndInvokeWithCaptureAsync(cmd, []);

        Assert.AreNotEqual(0, exit, "ClearCache failure must propagate to a non-zero exit code.");
    }
}

/// <summary>
/// In-memory <see cref="IControlsDataService"/> backed by a tiny fixed dataset.
/// Built specifically for ControlsCommand tests — keeps them hermetic.
/// </summary>
internal sealed class FakeControlsDataService : IControlsDataService
{
    public int ClearCacheCalls { get; private set; }
    public Exception? ClearCacheException { get; set; }

    private readonly SearchEngine _engine = BuildEngine();

    public SearchEngine GetEngine() => _engine;

    public void ClearCache()
    {
        ClearCacheCalls++;
        if (ClearCacheException != null)
        {
            throw ClearCacheException;
        }
    }

    private static SearchEngine BuildEngine()
    {
        var scenarios = new[]
        {
            new Scenario
            {
                Id = "tabview",
                ControlId = "tabview",
                ControlName = "TabView",
                HeaderText = "Basic TabView",
                Xaml = "<TabView />",
                CSharp = "// add tab",
                Source = "gallery",
            },
            new Scenario
            {
                Id = "settingscard",
                ControlId = "settingscard",
                ControlName = "SettingsCard",
                HeaderText = "Basic SettingsCard",
                Xaml = "<controls:SettingsCard />",
                CSharp = "",
                Source = "toolkit",
                NuGetPackage = "CommunityToolkit.WinUI.Controls.SettingsControls",
            },
        };

        var tags = new Dictionary<string, string[]>
        {
            ["tabview"] = ["tabs", "documents"],
            ["settingscard"] = ["settings", "card"],
        };

        return new SearchEngine(scenarios, [], tags);
    }
}
