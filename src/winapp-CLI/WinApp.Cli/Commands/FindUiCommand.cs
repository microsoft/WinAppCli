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
using WinApp.Cli.Telemetry.Events;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp find-ui</c> — lexical search over WinUI controls and samples.
/// WinUI-only by design: the corpus is the WinUI 3 Gallery, the Windows
/// Community Toolkit gallery, and the microsoft-ui-reactor ReactorGallery
/// (plus a few curated core patterns). It does not cover WPF, WinForms, or
/// other UI frameworks.
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
            Description = "Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. gallery-tabview-1).",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };

        ListOption = new Option<bool>("--list")
        {
            Description = "List every discoverable control/sample id instead of searching. Covers Gallery, Toolkit, and core; the opt-in Reactor source is excluded (search it with --source reactor)."
        };

        SourceOption = new Option<string?>("--source")
        {
            Description = "Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), reactor (microsoft-ui-reactor, C#-only declarative WinUI), or core (curated patterns). Reactor is opt-in — it is excluded from a normal search, so pass --source reactor to search it (only do this for a Reactor/MVU project; its C#-only samples don't paste into a standard XAML app)."
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
        : base("find-ui", "Search WinUI controls and samples for a working code example. WinUI-only: covers the WinUI 3 Gallery and the Windows Community Toolkit by default (plus the microsoft-ui-reactor ReactorGallery as an opt-in source via --source reactor); not WPF/WinForms. The corpus is fetched from GitHub on first use and cached per-user, so the first run requires network access.")
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
            // The embedded core patterns need no network. A request that a core-only
            // corpus can satisfy — browse (--list), an explicit --source core search,
            // or fetching only core-prefixed ids — should still work offline, so tell
            // the service a core-only engine is acceptable when the network corpus is
            // unavailable. A normal search or a gallery/toolkit/reactor --id still
            // surfaces the friendly "connect and run once" error on a cold offline cache.
            var allowCoreOnly = list
                || string.Equals(source, "core", StringComparison.OrdinalIgnoreCase)
                || (ids.Length > 0 && ids.All(id => ProviderRegistry.ForScenarioId(id) is null));

            // Reactor is opt-in: its C#-only declarative samples can't paste into a
            // standard XAML app, so they must never surface in a default search.
            // Only load/fetch Reactor when the caller explicitly asks for it — a
            // "--source reactor" search or a "reactor-*" id fetch.
            var includeReactor = ProviderRegistry.IsReactorSource(source)
                || ids.Any(ProviderRegistry.IsReactorScenarioId);
            try
            {
                engine = await searchService.GetEngineAsync(refresh, allowCoreOnly, includeReactor, BuildFetchNotice(json), cancellationToken).ConfigureAwait(false);
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
                return EmitCode(engine, ids, includeReactor, json);
            }

            return EmitSearch(engine, query!, max, source, includeReactor, json);
        }

        private int EmitSearch(SearchEngine engine, string query, int max, string? source, bool includeReactor, bool json)
        {
            var groups = engine.SearchGrouped(query, maxControls: max, maxScenariosPerControl: 3, sourceFilter: source);

            // Usage telemetry: mode + registry-validated source + match count. The
            // free-form query is never captured. Emitted for both output formats.
            FindUiUsageEvent.LogSearch(source, includeReactor, json, groups.Count);

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
                // A default search excludes the opt-in Reactor source. If the user
                // hasn't already scoped to a source, nudge them toward it so a
                // Reactor-only match isn't silently invisible.
                if (source is null)
                {
                    logger.LogInformation(
                        "Reactor samples are excluded by default; add --source reactor to search them (Reactor/MVU projects only).");
                }
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

        private int EmitCode(SearchEngine engine, string[] ids, bool includeReactor, bool json)
        {
            var entries = new List<FindUiCodeEntryJson>(ids.Length);
            // Telemetry uses the corpus-canonical id (e.g. "gallery-tabview-1"), never
            // the caller's raw --id token — the fallback resolver accepts bare control
            // ids ("gallery-gridview"), so echoing the request would both misreport the
            // fetched scenario and leak an unvalidated user string. Unresolved ids are
            // reflected in the count only (RequestedIdCount - resolved), never emitted.
            var resolvedCanonicalIds = new List<string>(ids.Length);
            foreach (var id in ids)
            {
                var (formatted, found, canonicalId) = engine.GetPattern(id);
                entries.Add(new FindUiCodeEntryJson { Id = id, Found = found, Content = formatted });
                if (found && canonicalId is not null)
                {
                    resolvedCanonicalIds.Add(canonicalId);
                }
            }

            FindUiUsageEvent.LogFetch(includeReactor, json, resolvedCanonicalIds, ids.Length);

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

                var entry = entries[i];
                if (!entry.Found)
                {
                    // Not-found message (e.g. "Pattern 'x' not found.") — flag it clearly.
                    console.MarkupLine($"[red]{Markup.Escape(entry.Content)}[/]");
                }
                else
                {
                    WriteScenarioContent(entry.Content);
                }
            }
            return entries.All(e => e.Found) ? 0 : 1;
        }

        /// <summary>
        /// Render a formatted scenario to the console with light color to break up the
        /// wall of code (headings, metadata labels, and code-fence markers), while
        /// keeping code bodies byte-for-byte verbatim. Structural lines go through
        /// <see cref="IAnsiConsole"/> markup (escaped, so brackets in headers are safe);
        /// code and free text are written literally with <c>WriteLine</c> so markup is
        /// never interpreted and snippets stay copy-pasteable. When output is redirected
        /// or color is unavailable, Spectre strips the styling and the plain markdown is
        /// reproduced unchanged. The JSON <c>content</c> field is never touched.
        /// </summary>
        private void WriteScenarioContent(string content)
        {
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                // "## Control: Header [Source]" — the scenario heading.
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    console.MarkupLine($"[bold cyan]{Markup.Escape(line)}[/]");
                }
                // Metadata labels: **Setup:**, **Namespace:**, **XAML:**, **C#:**, **Important:**
                else if (TryFormatLabelLine(line, out var labelMarkup))
                {
                    console.MarkupLine(labelMarkup);
                }
                // Code-fence markers (```xml / ```csharp / ```) — dim so they quietly delimit blocks.
                else if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    console.MarkupLine($"[grey]{Markup.Escape(line)}[/]");
                }
                else
                {
                    // Code and free text: write literally (no markup parsing) so brackets
                    // and other markup-significant characters survive untouched.
                    console.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// If <paramref name="line"/> opens with a bold markdown label such as
        /// <c>**Setup:**</c>, produce console markup that tints just the label and leaves
        /// any trailing content (e.g. <c>NuGet `pkg`</c>) in the default color. Returns
        /// false for non-label lines. Both segments are escaped so backticks/brackets are literal.
        /// </summary>
        private static bool TryFormatLabelLine(string line, out string markup)
        {
            markup = "";
            if (!line.StartsWith("**", StringComparison.Ordinal))
            {
                return false;
            }
            var close = line.IndexOf(":**", StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }
            var label = line[..(close + 3)];   // includes the trailing ":**"
            var rest = line[(close + 3)..];    // may be empty (standalone label line)
            markup = $"[yellow]{Markup.Escape(label)}[/]{Markup.Escape(rest)}";
            return true;
        }

        /// <summary>
        /// Build the one-time "fetching…" notice callback handed to the search service.
        /// The service invokes it (once) only when a provider actually starts a network
        /// fetch — a warm cache never triggers it. The notice is written to a dedicated
        /// <b>stderr</b> console so stdout (and <c>--json</c> payloads in particular) stay
        /// clean, and it is suppressed entirely when info-level output is off
        /// (<c>--json</c> / <c>--quiet</c>). Returns null in those suppressed modes so no
        /// stderr console is even created.
        /// </summary>
        private Action<string>? BuildFetchNotice(bool json)
        {
            // --json/--quiet drop info-level output; skip the notice (and its stderr console).
            if (json || !logger.IsEnabled(LogLevel.Information))
            {
                return null;
            }

            IAnsiConsole? stderrConsole = null;
            var shown = false;
            // Providers load sequentially in ControlsSearchService, so this callback is
            // never invoked concurrently; a plain latch is sufficient.
            return _ =>
            {
                if (shown)
                {
                    return;
                }
                shown = true;
                stderrConsole ??= AnsiConsole.Create(
                    new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
                stderrConsole.MarkupLine("[grey]Fetching WinUI controls from GitHub...[/]");
            };
        }

        private int EmitList(SearchEngine engine, bool json)
        {
            var items = engine.ListAll().Select(x => new FindUiScenarioJson { Id = x.id, Header = x.scenario }).ToList();

            // Usage telemetry: mode + item count. No per-id detail (a browse isn't a
            // targeted lookup) and no free-form input to capture.
            FindUiUsageEvent.LogList(json, items.Count);

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
