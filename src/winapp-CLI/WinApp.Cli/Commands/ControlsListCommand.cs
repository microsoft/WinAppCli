// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Commands;

internal class ControlsListCommand : Command, IShortDescription
{
    public string ShortDescription => "List every available control pattern, grouped by source";

    public static Option<string?> SourceOption { get; } = new Option<string?>("--source")
    {
        Description = "Constrain results to one source: gallery (WinUI 3 Gallery), toolkit (Community Toolkit), or core (curated platform patterns). Default: all sources.",
        HelpName = "gallery|toolkit|core"
    };

    private static readonly string[] ValidSources = ["gallery", "toolkit", "core"];

    public ControlsListCommand()
        : base("list",
            "List every available control pattern grouped by source (core platform patterns, WinUI Gallery, Community Toolkit). Useful for discovery and to see exact ids accepted by `winapp controls get`.")
    {
        Options.Add(SourceOption);
    }

    public class Handler(
        IControlsDataService dataService,
        ILogger<ControlsListCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var source = parseResult.GetValue(SourceOption);
            if (!string.IsNullOrWhiteSpace(source) && !ValidSources.Contains(source))
            {
                logger.LogError("Invalid --source value '{Source}'. Allowed values: gallery, toolkit, core.", source);
                return Task.FromResult(1);
            }

            try
            {
                var engine = dataService.GetEngine();
                var writer = parseResult.InvocationConfiguration.Output;

                writer.WriteLine("Available patterns:");
                writer.WriteLine();

                string? lastType = null;
                foreach (var (id, scenario) in engine.ListAll(string.IsNullOrWhiteSpace(source) ? null : source))
                {
                    string type;
                    if (id.StartsWith("gallery-", StringComparison.Ordinal))
                    {
                        type = "Gallery (WinUI 3)";
                    }
                    else if (id.StartsWith("toolkit-", StringComparison.Ordinal))
                    {
                        type = "CommunityToolkit";
                    }
                    else
                    {
                        type = "Core platform patterns";
                    }

                    if (type != lastType)
                    {
                        if (lastType != null)
                        {
                            writer.WriteLine();
                        }
                        writer.WriteLine($"## {type}");
                        lastType = type;
                    }
                    writer.WriteLine($"  - {id}: {scenario}");
                }
                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("Controls list failed: {Message}", ex.Message);
                logger.LogDebug("{StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
