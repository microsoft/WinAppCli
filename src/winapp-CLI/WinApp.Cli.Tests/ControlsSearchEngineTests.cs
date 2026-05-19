// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Pure-function tests for the controls search infrastructure: BM25 ranking,
/// synonym/phrase preprocessing and expansion, and the SearchEngine glue.
/// No DI, no filesystem, no network — just deterministic in-memory inputs.
/// </summary>
[TestClass]
public class ControlsSearchEngineTests
{
    private static readonly string[] _helloWorldDataGrid = ["hello", "world", "data-grid"];
    private static readonly string[] _justCd = ["cd"];
    private static readonly string[] _tabviewQuery = ["tabview"];
    private static readonly string[] _datagridTokens = ["datagrid"];

    // ----- BM25 -----

    [TestMethod]
    public void BM25_Tokenize_LowercasesAndStripsPunctuation()
    {
        var tokens = BM25.Tokenize("Hello, WORLD! data-grid");

        CollectionAssert.AreEquivalent(_helloWorldDataGrid, tokens);
    }

    [TestMethod]
    public void BM25_Tokenize_DropsSingleCharTokens()
    {
        var tokens = BM25.Tokenize("a b cd e f");

        CollectionAssert.AreEquivalent(_justCd, tokens);
    }

    [TestMethod]
    public void BM25_Score_RanksExactMatchHigherThanNoMatch()
    {
        var match = BM25.BuildDoc(("tabview tabs documents", 1.0));
        var miss = BM25.BuildDoc(("button click action", 1.0));
        var corpus = BM25.BuildCorpus(new[] { match, miss });

        var matchScore = BM25.Score(match, _tabviewQuery, corpus);
        var missScore = BM25.Score(miss, _tabviewQuery, corpus);

        Assert.IsTrue(matchScore > 0, "Doc containing query term must score > 0.");
        Assert.AreEqual(0.0, missScore, "Doc without query term must score exactly 0.");
        Assert.IsTrue(matchScore > missScore);
    }

    [TestMethod]
    public void BM25_Score_FieldWeightsAreRespected()
    {
        // Same word, but heavier field weight on the left doc.
        var heavy = BM25.BuildDoc(("tabview", 3.0));
        var light = BM25.BuildDoc(("tabview", 1.0));
        var corpus = BM25.BuildCorpus(new[] { heavy, light });

        var heavyScore = BM25.Score(heavy, _tabviewQuery, corpus);
        var lightScore = BM25.Score(light, _tabviewQuery, corpus);

        Assert.IsTrue(heavyScore > lightScore,
            $"Heavier-weighted field should rank higher (heavy={heavyScore}, light={lightScore}).");
    }

    // ----- Synonyms -----

    [TestMethod]
    public void Synonyms_Preprocess_CollapsesKnownPhrase()
    {
        var result = Synonyms.Preprocess("show me a data grid please");

        StringAssert.Contains(result, "datagrid",
            "Two-word 'data grid' phrase should be collapsed to the single token 'datagrid'.");
    }

    [TestMethod]
    public void Synonyms_Expand_AddsRelatedTerms()
    {
        var expanded = Synonyms.Expand(_datagridTokens);

        // We don't pin the exact synonym list, but expansion must produce at least
        // the original token. (The synonym table is documented behavior; if it ever
        // returns an empty array, callers would silently lose recall.)
        Assert.IsTrue(expanded.Length >= 1, "Expansion must return at least the original token.");
        CollectionAssert.Contains(expanded, "datagrid");
    }

    // ----- SearchEngine end-to-end -----

    [TestMethod]
    public void SearchEngine_Search_ReturnsRankedMatchesWithSourcePrefixes()
    {
        var engine = BuildEngine();

        var results = engine.Search("tabview", maxResults: 5);

        Assert.IsTrue(results.Count > 0, "Searching for an exact control name must return at least one result.");
        Assert.IsTrue(
            results[0].Id.StartsWith("gallery-", StringComparison.Ordinal) || results[0].Id.StartsWith("toolkit-", StringComparison.Ordinal),
            $"Result ids must carry a source prefix (got '{results[0].Id}').");
    }

    [TestMethod]
    public void SearchEngine_Search_SourceFilter_RestrictsResults()
    {
        var engine = BuildEngine();

        var galleryOnly = engine.Search("card", maxResults: 10, sourceFilter: "gallery");
        var toolkitOnly = engine.Search("card", maxResults: 10, sourceFilter: "toolkit");

        Assert.IsTrue(galleryOnly.All(r => r.Type == "gallery"),
            "Gallery filter must exclude toolkit results.");
        Assert.IsTrue(toolkitOnly.All(r => r.Type == "toolkit"),
            "Toolkit filter must exclude gallery results.");
    }

    [TestMethod]
    public void SearchEngine_Search_EmptyQuery_ReturnsEmpty()
    {
        var engine = BuildEngine();

        var results = engine.Search("   ", maxResults: 5);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void SearchEngine_GetPattern_RespectsSourcePrefix()
    {
        var engine = BuildEngine();

        var (formatted, found) = engine.GetPattern("gallery-tabview");

        Assert.IsTrue(found);
        StringAssert.Contains(formatted, "TabView");
    }

    [TestMethod]
    public void SearchEngine_GetPattern_UnknownId_ReturnsNotFound()
    {
        var engine = BuildEngine();

        var (formatted, found) = engine.GetPattern("gallery-zznosuch");

        Assert.IsFalse(found);
        StringAssert.Contains(formatted, "not found");
    }

    [TestMethod]
    public void SearchEngine_GetPattern_StripsControlCharsFromOutput()
    {
        // Regression for L4: snippet output must not forward ANSI escape codes / control bytes
        // from upstream fetched docs to the user's terminal.
        var scenarios = new[]
        {
            new Scenario
            {
                Id = "evil",
                ControlId = "evil",
                ControlName = "Evil",
                HeaderText = "Evil header \x1b[31mRED\x1b[0m text",
                Xaml = "<X>\x1b[31m</X>",
                Source = "gallery",
            }
        };
        var engine = new SearchEngine(scenarios, [], new Dictionary<string, string[]>());

        var (formatted, found) = engine.GetPattern("gallery-evil");

        Assert.IsTrue(found);
        Assert.IsFalse(formatted.Contains('\x1b'),
            "Output must not forward raw ANSI escape (0x1B) bytes from upstream content.");
    }

    [TestMethod]
    public void SearchEngine_ListAll_EnumeratesAcrossSources()
    {
        var engine = BuildEngine();

        var all = engine.ListAll().ToList();

        Assert.IsTrue(all.Count >= 2, "ListAll should enumerate the fixture scenarios.");
        Assert.IsTrue(all.Any(t => t.id.StartsWith("gallery-", StringComparison.Ordinal)), "Gallery entries should be present.");
        Assert.IsTrue(all.Any(t => t.id.StartsWith("toolkit-", StringComparison.Ordinal)), "Toolkit entries should be present.");
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
                Source = "gallery",
            },
            new Scenario
            {
                Id = "settingscard",
                ControlId = "settingscard",
                ControlName = "SettingsCard",
                HeaderText = "Basic settings card",
                Xaml = "<controls:SettingsCard />",
                Source = "toolkit",
                NuGetPackage = "CommunityToolkit.WinUI.Controls.SettingsControls",
            },
        };
        return new SearchEngine(scenarios, [], new Dictionary<string, string[]>
        {
            ["tabview"] = ["tabs", "documents"],
            ["settingscard"] = ["settings", "card"],
        });
    }
}
