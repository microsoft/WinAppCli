// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Commands;

internal class ControlsRefreshCommand : Command, IShortDescription
{
    public string ShortDescription => "Force a re-fetch of the controls dataset from GitHub";

    public ControlsRefreshCommand()
        : base("refresh",
            "Delete the cached WinUI Gallery and Community Toolkit dataset so the next `winapp controls search/get/list` re-fetches the latest snapshot from GitHub.")
    {
    }

    public class Handler(
        IControlsDataService dataService,
        ILogger<ControlsRefreshCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                dataService.ClearCache();
                logger.LogInformation("Controls cache cleared. The next `winapp controls` invocation will re-fetch from GitHub.");
                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("Controls refresh failed: {Message}", ex.Message);
                logger.LogDebug("{StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
