// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.ApiSearch;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp find-api</c> — lexical search and inspection over the Windows/WinRT
/// API surface (types, members, enums, namespaces) that a project's referenced
/// <c>.winmd</c>/<c>.dll</c> metadata exposes. The bare form is a search
/// (<c>winapp find-api "acrylic brush"</c>); sub-verbs (<c>members</c>,
/// <c>check-property</c>, <c>types</c>, <c>enums</c>, <c>namespaces</c>,
/// <c>packages</c>, <c>stats</c>, <c>projects</c>, <c>refresh</c>) drill in. The
/// index is built from the project's restored packages on first use and refreshed
/// automatically when <c>project.assets.json</c> changes.
/// </summary>
internal sealed class FindApiCommand : Command, IShortDescription
{
    public string ShortDescription => "Search a project's Windows/WinRT API surface (types, members, enums)";

    public static Argument<string[]> QueryArgument { get; } = new("query")
    {
        Description = "What to search for, e.g. \"acrylic brush\" or \"NavigationView\". Matched lexically against type and member names across the project's indexed API metadata. Pass several quoted queries to run them in a single call.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    public static Option<int> MaxOption { get; } = new("--max")
    {
        Description = "Maximum number of namespace-grouped results to return.",
        DefaultValueFactory = _ => 30,
    };

    public static Option<string?> ProjectDirOption { get; } = FindApiShared.CreateProjectDirOption();

    public static Option<string?> ProjectOption { get; } = FindApiShared.CreateProjectOption();

    public FindApiCommand(
        FindApiMembersCommand membersCommand,
        FindApiCheckPropertyCommand checkPropertyCommand,
        FindApiTypesCommand typesCommand,
        FindApiEnumsCommand enumsCommand,
        FindApiNamespacesCommand namespacesCommand,
        FindApiPackagesCommand packagesCommand,
        FindApiStatsCommand statsCommand,
        FindApiProjectsCommand projectsCommand,
        FindApiRefreshCommand refreshCommand)
        : base("find-api", "Search and inspect the Windows/WinRT API surface (types, members, enums) available to a project, resolved from its referenced .winmd/.dll metadata. The bare form searches; sub-verbs drill into a specific type or the index itself. Search, members, enums, and check-property each accept several subjects in one call — batch your lookups rather than issuing one call per question. The index is built from the project's restored NuGet/SDK packages and refreshed automatically when the project is restored.")
    {
        Arguments.Add(QueryArgument);
        Options.Add(MaxOption);
        Options.Add(ProjectDirOption);
        Options.Add(ProjectOption);
        Options.Add(WinAppRootCommand.JsonOption);

        Subcommands.Add(membersCommand);
        Subcommands.Add(checkPropertyCommand);
        Subcommands.Add(typesCommand);
        Subcommands.Add(enumsCommand);
        Subcommands.Add(namespacesCommand);
        Subcommands.Add(packagesCommand);
        Subcommands.Add(statsCommand);
        Subcommands.Add(projectsCommand);
        Subcommands.Add(refreshCommand);
    }

    public sealed class Handler(IApiMetadataService service, IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) => Task.FromResult(Execute(parseResult));

        private int Execute(ParseResult parseResult)
        {
            bool json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            List<string> queries = FindApiShared.ReadSubjects(parseResult.GetValue(QueryArgument));

            if (queries.Count == 0)
            {
                return FindApiShared.Fail(
                    console,
                    json,
                    "Provide a search query (e.g. winapp find-api \"acrylic brush\"), or use a sub-verb: members, check-property, enums, packages, stats, refresh.");
            }

            int max = parseResult.GetValue(MaxOption);
            if (max < 1)
            {
                return FindApiShared.Fail(console, json, "--max must be at least 1.");
            }

            var scope = FindApiShared.ReadScope(parseResult, ProjectDirOption, ProjectOption);

            if (queries.Count == 1)
            {
                var single = service.Search(queries[0], max, scope);
                return FindApiShared.Emit(
                    console, json, "search", single, WinAppJsonContext.Default.ApiSearchOutput,
                    data => FindApiShared.RenderSearch(console, data),
                    data =>
                    {
                        bool hasHits = data.Results.Count > 0 || data.Ambiguous is { Count: > 0 };
                        return (hasHits ? 0 : 1, data.Results.Count, hasHits);
                    });
            }

            var results = queries.ConvertAll(q => (q, service.Search(q, max, scope)));
            return FindApiShared.EmitBatch(
                console, json, "search", results,
                (ok, errors) => new ApiSearchBatchOutput
                {
                    Count = ok.Count,
                    Results = ok,
                    Errors = errors.Count > 0 ? errors : null,
                },
                WinAppJsonContext.Default.ApiSearchBatchOutput,
                data =>
                {
                    // Which query produced which block is not otherwise recoverable once
                    // several are rendered back to back.
                    console.MarkupLineInterpolated($"[grey]Query: {data.Query}[/]");
                    FindApiShared.RenderSearch(console, data);
                },
                data => (data.Results.Count, data.Results.Count > 0 || data.Ambiguous is { Count: > 0 }));
        }
    }
}
