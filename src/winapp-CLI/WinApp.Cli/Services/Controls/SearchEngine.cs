// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

internal sealed class SearchEngine
{
    private readonly Scenario[] _scenarios;
    private readonly CorePattern[] _corePatterns;
    private readonly Dictionary<string, string[]> _enrichmentTags;
    private readonly Dictionary<string, List<Scenario>> _scenariosByControl;
    private readonly string[] _uniqueControls;

    public SearchEngine(Scenario[] scenarios, CorePattern[] corePatterns, Dictionary<string, string[]> enrichmentTags)
    {
        _scenarios = scenarios;
        _corePatterns = corePatterns;
        _enrichmentTags = enrichmentTags;

        _scenariosByControl = new();
        foreach (var s in scenarios)
        {
            // Use composite key (source + controlId) to keep Toolkit and Gallery
            // controls with the same controlId (e.g., "colorpicker") separate.
            var key = $"{s.Source}:{s.ControlId}";
            if (!_scenariosByControl.TryGetValue(key, out var list))
            {
                list = new List<Scenario>();
                _scenariosByControl[key] = list;
            }
            list.Add(s);
        }
        _uniqueControls = _scenariosByControl.Keys.ToArray();
    }

    public record SearchResult(string Id, string Scenario, string Type, double Score);

    /// <summary>Two-layer search: find controls first, then pick best scenario.</summary>
    public List<SearchResult> Search(string query, int maxResults = 5, string? sourceFilter = null)
    {
        // Phrase preprocessing: merge known multi-word phrases (e.g. "data grid" → "datagrid")
        var preprocessed = Synonyms.Preprocess(query);
        var queryWords = BM25.Tokenize(preprocessed);
        if (queryWords.Length == 0) return [];
        var queryLower = preprocessed;

        // Expand query with synonyms (datagrid → listview, modal → contentdialog, etc.)
        var expandedWords = Synonyms.Expand(queryWords);

        // Build docs for core patterns
        var coreDocs = _corePatterns.Select(p => BM25.BuildDoc(
            (string.Join(" ", p.Tags), 3.0),
            (p.Scenario, 2.0),
            (p.Description, 1.0),
            (p.Id, 1.0)
        )).ToArray();

        // Build docs for gallery controls
        var controlDocs = _uniqueControls.Select(key =>
        {
            var scenarios = _scenariosByControl[key];
            var controlId = scenarios[0].ControlId;
            var controlName = scenarios[0].ControlName;
            var enrichTags = _enrichmentTags.TryGetValue(controlId, out var tags) ? tags : [];
            return BM25.BuildDoc(
                (controlName, 3.0),
                (controlId, 3.0),
                (string.Join(" ", enrichTags), 3.0),
                (scenarios[0].HeaderText, 1.5)
            );
        }).ToArray();

        // Always score against the full corpus so IDF stays stable regardless of filter.
        var allDocs = coreDocs.Concat(controlDocs).ToArray();
        var corpus = BM25.BuildCorpus(allDocs);

        var results = new List<SearchResult>();

        // Score core patterns
        for (int i = 0; i < _corePatterns.Length; i++)
        {
            var s = BM25.Score(coreDocs[i], expandedWords, corpus);
            if (s > 0) results.Add(new(_corePatterns[i].Id, _corePatterns[i].Scenario, "core", s));
        }

        // Score gallery controls, then pick best scenario
        for (int i = 0; i < _uniqueControls.Length; i++)
        {
            var s = BM25.Score(controlDocs[i], expandedWords, corpus);
            var controlName = _scenariosByControl[_uniqueControls[i]][0].ControlName;
            var controlLower = controlName.ToLowerInvariant();
            // Boost if ORIGINAL query (not synonym-expanded) contains the exact control name
            if (controlLower.Length > 2 && queryWords.Any(w => w == controlLower))
                s *= 2.0;
            if (s <= 0) continue;

            var scenarios = _scenariosByControl[_uniqueControls[i]];
            var bestScenario = PickBestScenario(scenarios, expandedWords);
            var prefix = bestScenario.Source == "toolkit" ? "toolkit-" : "gallery-";
            results.Add(new($"{prefix}{bestScenario.Id}", $"{bestScenario.ControlName}: {bestScenario.HeaderText}", bestScenario.Source, s));
        }

        if (sourceFilter != null)
        {
            results = results.Where(r => r.Type == sourceFilter).ToList();
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results.Take(maxResults).ToList();
    }

    private Scenario PickBestScenario(List<Scenario> scenarios, string[] queryWords)
    {
        if (scenarios.Count == 1) return scenarios[0];

        var scenDocs = scenarios.Select(s => BM25.BuildDoc(
            (s.HeaderText, 2.0),
            (s.ControlName, 1.0)
        )).ToArray();
        var corpus = BM25.BuildCorpus(scenDocs);

        int bestIdx = 0;
        double bestScore = 0;
        for (int i = 0; i < scenarios.Count; i++)
        {
            var s = BM25.Score(scenDocs[i], queryWords, corpus);
            if (s > bestScore) { bestScore = s; bestIdx = i; }
        }
        return scenarios[bestIdx];
    }

    public (string formatted, bool found) GetPattern(string id)
    {
        // Check core patterns
        var core = _corePatterns.FirstOrDefault(p => p.Id == id);
        if (core != null) return (FormatCorePattern(core), true);

        // Strip known prefixes ("gallery-" or "toolkit-") and remember which source the
        // caller asked for, so we don't return a Gallery scenario for a "toolkit-..." id
        // (or vice-versa) when the bare ids happen to collide.
        string? expectedSource = null;
        var bareId = id;
        if (id.StartsWith("gallery-", StringComparison.Ordinal))
        {
            expectedSource = "gallery";
            bareId = id["gallery-".Length..];
        }
        else if (id.StartsWith("toolkit-", StringComparison.Ordinal))
        {
            expectedSource = "toolkit";
            bareId = id["toolkit-".Length..];
        }

        bool MatchesSource(Scenario s) => expectedSource == null || s.Source == expectedSource;

        var scenario =
            _scenarios.FirstOrDefault(s => s.Id == bareId && MatchesSource(s))
            ?? _scenarios.FirstOrDefault(s => s.ControlId == bareId && MatchesSource(s));

        if (scenario != null) return (FormatScenario(scenario), true);
        return ($"Pattern '{id}' not found.", false);
    }

    public IEnumerable<(string id, string scenario)> ListAll(string? sourceFilter = null)
    {
        if (sourceFilter == null || sourceFilter == "core")
        {
            foreach (var p in _corePatterns)
                yield return (p.Id, p.Scenario);
        }

        if (sourceFilter == "core") yield break;

        // Show ALL scenarios grouped by (source, control) so multi-scenario controls are
        // discoverable AND collisions like ColorPicker (Gallery vs Toolkit) stay separate.
        var byControl = _scenarios
            .Where(s => sourceFilter == null || s.Source == sourceFilter)
            .GroupBy(s => $"{s.Source}:{s.ControlId}")
            .OrderBy(g => g.First().Source)         // gallery first, toolkit after
            .ThenBy(g => g.First().ControlName);

        foreach (var group in byControl)
        {
            var first = group.First();
            var prefix = first.Source == "toolkit" ? "toolkit-" : "gallery-";
            foreach (var s in group)
                yield return ($"{prefix}{s.Id}", $"{s.ControlName}: {s.HeaderText}");
        }
    }

    private static string FormatCorePattern(CorePattern p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## {p.Scenario}");
        sb.AppendLine();
        sb.AppendLine(p.Description);
        if (p.Prerequisites.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Prerequisites:**");
            foreach (var pre in p.Prerequisites) sb.AppendLine($"- {pre}");
        }
        if (p.Xaml != null)
        {
            sb.AppendLine();
            sb.AppendLine("**XAML:**");
            sb.AppendLine("```xml");
            sb.AppendLine(p.Xaml);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("**C#:**");
        sb.AppendLine("```csharp");
        sb.AppendLine(p.CSharp);
        sb.AppendLine("```");
        if (p.Notes.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Important:**");
            foreach (var n in p.Notes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }

    private static string FormatScenario(Scenario s)
    {
        var sb = new System.Text.StringBuilder();
        var sourceTag = s.Source == "toolkit" ? " [CommunityToolkit]" : "";
        sb.AppendLine($"## {s.ControlName}: {s.HeaderText}{sourceTag}");
        sb.AppendLine();

        // Toolkit-specific prerequisites
        if (s.Source == "toolkit" && (!string.IsNullOrEmpty(s.NuGetPackage) || s.XmlnsImports.Length > 0))
        {
            sb.AppendLine("**Prerequisites:**");
            if (!string.IsNullOrEmpty(s.NuGetPackage))
                sb.AppendLine($"- Install NuGet package: `{s.NuGetPackage}`");
            foreach (var ns in s.XmlnsImports)
                sb.AppendLine($"- Add XAML namespace: `{ns}`");
            sb.AppendLine();
        }

        if (s.Xaml != null)
        {
            sb.AppendLine("**XAML:**");
            sb.AppendLine("```xml");
            sb.AppendLine(s.Xaml);
            sb.AppendLine("```");
        }
        if (s.CSharp != null)
        {
            sb.AppendLine();
            sb.AppendLine("**C#:**");
            sb.AppendLine("```csharp");
            sb.AppendLine(s.CSharp);
            sb.AppendLine("```");
        }
        var notes = Notes.GetNotes(s.ControlName);
        if (notes.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Important:**");
            foreach (var n in notes) sb.AppendLine($"- {n}");
        }
        return sb.ToString();
    }
}
