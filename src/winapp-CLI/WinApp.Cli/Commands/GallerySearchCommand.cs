// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.Gallery;

namespace WinApp.Cli.Commands;

internal class GallerySearchCommand : Command, IShortDescription
{
    public string ShortDescription => "Search controls and patterns by description";

    public static Argument<string> QueryArgument { get; } = new Argument<string>("query")
    {
        Description = "Free-text query (e.g. \"tabbed document interface\", \"share contract\", \"settings card\")."
    };

    public static Option<int> MaxOption { get; } = new Option<int>("--max")
    {
        Description = "Maximum number of matches to return.",
        DefaultValueFactory = _ => 5
    };

    public GallerySearchCommand()
        : base("search",
            "Search WinUI 3 Gallery, Community Toolkit, and core platform patterns for samples that match a free-text query.")
    {
        Arguments.Add(QueryArgument);
        Options.Add(MaxOption);
    }

    public class Handler(
        IGalleryDataService dataService,
        ILogger<GallerySearchCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var query = parseResult.GetValue(QueryArgument);
            var max = parseResult.GetValue(MaxOption);
            if (max <= 0)
            {
                max = 5;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                logger.LogError("A non-empty query is required. Example: winapp gallery search \"tabbed document interface\"");
                return Task.FromResult(1);
            }

            try
            {
                var engine = dataService.GetEngine();
                var results = engine.Search(query, max);

                var writer = parseResult.InvocationConfiguration.Output;
                if (results.Count == 0)
                {
                    writer.WriteLine($"No patterns found for: \"{query}\"");
                    return Task.FromResult(0);
                }

                writer.WriteLine($"Found {results.Count} matches for \"{query}\":");
                writer.WriteLine();
                foreach (var r in results)
                {
                    writer.WriteLine($"  {r.Id}");
                    writer.WriteLine($"    {r.Scenario}");
                    writer.WriteLine();
                }
                writer.WriteLine("To get full code: winapp gallery get <id>");
                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("Gallery search failed: {Message}", ex.Message);
                logger.LogDebug("{StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
