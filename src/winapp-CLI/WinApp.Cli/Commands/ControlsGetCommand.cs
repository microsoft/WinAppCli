// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Commands;

internal class ControlsGetCommand : Command, IShortDescription
{
    public string ShortDescription => "Print the full XAML + C# for a pattern";

    public static Argument<string> IdArgument { get; } = new Argument<string>("id")
    {
        Description = "Pattern id from `winapp controls search` (e.g. gallery-tabview, toolkit-segmented, jumplist-recent-files)."
    };

    public ControlsGetCommand()
        : base("get",
            "Print the full XAML, C#, and pitfall notes for a single control pattern, identified by the id returned from `winapp controls search`.")
    {
        Arguments.Add(IdArgument);
    }

    public class Handler(
        IControlsDataService dataService,
        ILogger<ControlsGetCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var id = parseResult.GetValue(IdArgument);
            if (string.IsNullOrWhiteSpace(id))
            {
                logger.LogError("A pattern id is required. Use `winapp controls search <query>` to find one.");
                return Task.FromResult(1);
            }

            try
            {
                var engine = dataService.GetEngine();
                var (formatted, found) = engine.GetPattern(id);

                var writer = parseResult.InvocationConfiguration.Output;
                writer.WriteLine(formatted);
                return Task.FromResult(found ? 0 : 1);
            }
            catch (Exception ex)
            {
                logger.LogError("Controls get failed: {Message}", ex.Message);
                logger.LogDebug("{StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
