// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.Gallery;

namespace WinApp.Cli.Commands;

internal class GalleryListCommand : Command, IShortDescription
{
    public string ShortDescription => "List every available gallery pattern, grouped by source";

    public GalleryListCommand()
        : base("list",
            "List every available gallery pattern grouped by source (core platform patterns, WinUI Gallery, Community Toolkit). Useful for discovery and to see exact ids accepted by `winapp gallery get`.")
    {
    }

    public class Handler(
        IGalleryDataService dataService,
        ILogger<GalleryListCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                var engine = dataService.GetEngine();
                var writer = parseResult.InvocationConfiguration.Output;

                writer.WriteLine("Available patterns:");
                writer.WriteLine();

                string? lastType = null;
                foreach (var (id, scenario) in engine.ListAll())
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
                logger.LogError("Gallery list failed: {Message}", ex.Message);
                logger.LogDebug("{StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
