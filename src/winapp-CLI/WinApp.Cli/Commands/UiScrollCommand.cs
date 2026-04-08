// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiScrollCommand : Command, IShortDescription
{
    public string ShortDescription => "Scroll a container element";

    public static Option<string?> DirectionOption { get; }
    public static Option<string?> ToOption { get; }

    static UiScrollCommand()
    {
        DirectionOption = new Option<string?>("--direction")
        {
            Description = "Scroll direction: up, down, left, right"
        };

        ToOption = new Option<string?>("--to")
        {
            Description = "Scroll to position: top, bottom"
        };
    }

    public UiScrollCommand()
        : base("scroll", "Scroll a container element using ScrollPattern. " +
               "Use --direction to scroll incrementally, or --to to jump to top/bottom.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(DirectionOption);
        Options.Add(ToOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IAnsiConsole ansiConsole,
        ILogger<UiScrollCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger);
                return 1;
            }

            var direction = parseResult.GetValue(DirectionOption);
            var to = parseResult.GetValue(ToOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "scroll");
                return 1;
            }

            if (direction is null && to is null)
            {
                logger.LogError("Specify --direction (up/down/left/right) or --to (top/bottom).");
                return 1;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var selector = selectorService.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr);
                    return 1;
                }

                await uiAutomation.ScrollContainerAsync(session, element, direction, to, cancellationToken);

                if (json)
                {
                    var result = new UiScrollResult
                    {
                        ElementId = element.Selector ?? element.Id,
                        Direction = direction,
                        To = to
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiScrollResult));
                }

                logger.LogInformation("Scrolled {Selector}", selectorStr);
                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex);
                return 1;
            }
        }
    }
}
