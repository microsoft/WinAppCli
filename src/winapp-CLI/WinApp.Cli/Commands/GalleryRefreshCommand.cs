// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Services.Gallery;

namespace WinApp.Cli.Commands;

internal class GalleryRefreshCommand : Command, IShortDescription
{
    public string ShortDescription => "Force a re-fetch of the gallery dataset from GitHub";

    public GalleryRefreshCommand()
        : base("refresh",
            "Delete the cached Gallery and Community Toolkit dataset so the next `winapp gallery search/get/list` re-fetches the latest snapshot from GitHub.")
    {
    }

    public class Handler(
        IGalleryDataService dataService,
        ILogger<GalleryRefreshCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            try
            {
                dataService.ClearCache();
                logger.LogInformation("Gallery cache cleared. The next `winapp gallery` invocation will re-fetch from GitHub.");
                return Task.FromResult(0);
            }
            catch (Exception ex)
            {
                logger.LogError("Gallery refresh failed: {Message}", ex.Message);
                logger.LogDebug("{StackTrace}", ex.StackTrace);
                return Task.FromResult(1);
            }
        }
    }
}
