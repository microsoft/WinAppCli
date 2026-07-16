// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp find-ui</c> — lexical search over WinUI controls and samples.
/// WinUI-only by design: the corpus is the WinUI 3 Gallery plus the Windows
/// Community Toolkit gallery (and a few curated core patterns). It does not
/// cover WPF, WinForms, or other UI frameworks.
/// </summary>
internal sealed class FindUiCommand : Command, IShortDescription
{
    public string ShortDescription => "Search WinUI controls & samples for a working example";

    public static Argument<string?> QueryArgument { get; }
    public static Option<string[]> IdOption { get; }
    public static Option<bool> ListOption { get; }
    public static Option<string?> SourceOption { get; }
    public static Option<int> MaxOption { get; }
    public static Option<bool> RefreshOption { get; }

    static FindUiCommand()
    {
        QueryArgument = new Argument<string?>("query")
        {
            Description = "What you're looking for, e.g. \"tabbed layout\" or \"color picker\". Matched lexically against WinUI control names, sample headers, and tags.",
            Arity = ArgumentArity.ZeroOrOne
        };

        IdOption = new Option<string[]>("--id")
        {
            Description = "Fetch full XAML + C# (and prerequisite notes) for one or more scenario ids from a prior search (e.g. gallery-tabview-1).",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true
        };

        ListOption = new Option<bool>("--list")
        {
            Description = "List every discoverable control/sample id instead of searching."
        };

        SourceOption = new Option<string?>("--source")
        {
            Description = "Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), or core (curated patterns)."
        };

        MaxOption = new Option<int>("--max")
        {
            Description = "Maximum number of matched controls to return.",
            DefaultValueFactory = _ => 3
        };

        RefreshOption = new Option<bool>("--refresh")
        {
            Description = "Bypass the local cache and re-fetch the WinUI corpus from GitHub."
        };
    }

    public FindUiCommand()
        : base("find-ui", "Search WinUI controls and samples for a working code example. WinUI-only: covers the WinUI 3 Gallery and Windows Community Toolkit (not WPF/WinForms). The corpus is fetched from GitHub on first use and cached per-user, so the first run requires network access.")
    {
        Arguments.Add(QueryArgument);
        Options.Add(IdOption);
        Options.Add(ListOption);
        Options.Add(SourceOption);
        Options.Add(MaxOption);
        Options.Add(RefreshOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IControlsSearchService searchService,
        IAnsiConsole console,
        ILogger<FindUiCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var query = parseResult.GetValue(QueryArgument);
            var ids = parseResult.GetValue(IdOption) ?? [];
            var list = parseResult.GetValue(ListOption);
            var source = parseResult.GetValue(SourceOption);
            var max = parseResult.GetValue(MaxOption);
            var refresh = parseResult.GetValue(RefreshOption);

            if (source is not null && !ProviderRegistry.IsValidSourceFilter(source))
            {
                var valid = string.Join(", ", ProviderRegistry.SourceFilterValues);
                return Fail(json, $"--source must be one of: {valid} (got: {source})");
            }

            // --source only affects search. Reject it with --list/--id rather than
            // silently ignoring it, so scripted callers don't get a false sense of filtering.
            if (source is not null && (list || ids.Length > 0))
            {
                return Fail(json, "--source only applies to search; it can't be combined with --list or --id.");
            }

            if (max < 1)
            {
                return Fail(json, "--max must be at least 1.");
            }

            // Exactly one mode: search (query), fetch (--id), or browse (--list).
            var modes = 0;
            if (!string.IsNullOrWhiteSpace(query)) { modes++; }
            if (ids.Length > 0) { modes++; }
            if (list) { modes++; }

            if (modes == 0)
            {
                return Fail(json, "Provide a search query (e.g. winapp find-ui \"tabbed layout\"), or use --id <id> to fetch code, or --list to browse.");
            }
            if (modes > 1)
            {
                return Fail(json, "Choose one of: a search query, --id <id>, or --list — they can't be combined.");
            }

            SearchEngine engine;
            try
            {
                engine = await searchService.GetEngineAsync(refresh, cancellationToken).ConfigureAwait(false);
            }
            catch (ControlsDataUnavailableException ex)
            {
                return Fail(json, ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "find-ui failed to load the WinUI corpus");
                return Fail(json, $"Failed to load the WinUI corpus: {ex.Message}");
            }

            if (list)
            {
                return EmitList(engine, json);
            }

            if (ids.Length > 0)
            {
                return EmitCode(engine, ids, json);
            }

            return EmitSearch(engine, query!, max, source, json);
        }

        private int EmitSearch(SearchEngine engine, string query, int max, string? source, bool json)
        {
            var groups = engine.SearchGrouped(query, maxControls: max, maxScenariosPerControl: 3, sourceFilter: source);

            if (json)
            {
                var result = new FindUiSearchJsonOutput
                {
                    Query = query,
                    MatchCount = groups.Count,
                    Matches = groups.Select(g => new FindUiMatchJson
                    {
                        Source = g.Source,
                        Control = g.ControlName,
                        Score = Math.Round(g.Score, 4),
                        Description = g.ControlDescription,
                        Scenarios = g.Scenarios.Select(s => new FindUiScenarioJson { Id = s.Id, Header = s.Header }).ToList()
                    }).ToList()
                };
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, WinAppJsonContext.Default.FindUiSearchJsonOutput));
                return groups.Count > 0 ? 0 : 1;
            }

            if (groups.Count == 0)
            {
                logger.LogInformation("No WinUI controls matched \"{Query}\".", query);
                return 1;
            }

            foreach (var g in groups)
            {
                var desc = string.IsNullOrWhiteSpace(g.ControlDescription) ? "" : $" [grey]— {Markup.Escape(g.ControlDescription!)}[/]";
                var control = string.IsNullOrEmpty(g.ControlName) ? "(core pattern)" : g.ControlName;
                console.MarkupLine($"[bold cyan]{Markup.Escape(control)}[/] [grey][[{g.Source}]][/]{desc}");
                foreach (var s in g.Scenarios)
                {
                    console.MarkupLine($"  [green]{Markup.Escape(s.Id)}[/]  {Markup.Escape(s.Header)}");
                }
            }
            console.MarkupLine("[grey]Fetch full code with:[/] winapp find-ui --id <id>");
            return 0;
        }

        private int EmitCode(SearchEngine engine, string[] ids, bool json)
        {
            var entries = ids.Select(id =>
            {
                var (formatted, found) = engine.GetPattern(id);
                return new FindUiCodeEntryJson { Id = id, Found = found, Content = formatted };
            }).ToList();

            if (json)
            {
                var result = new FindUiCodeJsonOutput { Results = entries };
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, WinAppJsonContext.Default.FindUiCodeJsonOutput));
                return entries.All(e => e.Found) ? 0 : 1;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    console.WriteLine();
                }
                console.WriteLine(entries[i].Content);
            }
            return entries.All(e => e.Found) ? 0 : 1;
        }

        private int EmitList(SearchEngine engine, bool json)
        {
            var items = engine.ListAll().Select(x => new FindUiScenarioJson { Id = x.id, Header = x.scenario }).ToList();

            if (json)
            {
                var result = new FindUiListJsonOutput { Count = items.Count, Items = items };
                console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(result, WinAppJsonContext.Default.FindUiListJsonOutput));
                return items.Count > 0 ? 0 : 1;
            }

            foreach (var item in items)
            {
                console.MarkupLine($"[green]{Markup.Escape(item.Id)}[/]  {Markup.Escape(item.Header)}");
            }
            logger.LogInformation("{Count} WinUI controls/samples available.", items.Count);
            return items.Count > 0 ? 0 : 1;
        }

        private int Fail(bool json, string message)
        {
            if (json)
            {
                return JsonErrorOutput.Write(console, message);
            }
            logger.LogError("{Symbol} {Message}", UiSymbols.Error, message);
            return 1;
        }
    }
}
