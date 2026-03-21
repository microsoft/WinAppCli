// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiScrollIntoViewCommand : Command, IShortDescription
{
    public string ShortDescription => "Scroll an element into the visible area";

    public UiScrollIntoViewCommand()
        : base("scroll-into-view", "Scroll the specified element into the visible area using UIA ScrollItemPattern.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiScrollIntoViewCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var mode = parseResult.GetValue(SharedUiOptions.ModeOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                logger.LogError("{Symbol} Specify --app (name/title/PID) or --window (HWND).", UiSymbols.Error);
                return 1;
            }
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} A selector is required.", UiSymbols.Error);
                return 1;
            }

            return await statusService.ExecuteWithStatusAsync(
                "Scrolling element into view...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var session = await sessionService.ResolveSessionAsync(app, window, mode, ct);
                        var selector = selectorService.Parse(selectorStr);
                        var element = selector.IsElementId
                            ? await uiAutomation.FindElementByIdAsync(session, selector.ElementId!, ct)
                            : await uiAutomation.FindSingleElementAsync(session, selector, ct);

                        if (element is null)
                        {
                            return (1, $"{UiSymbols.Error} No element found matching '{selectorStr}'");
                        }

                        await uiAutomation.ScrollIntoViewAsync(session, element, ct);
                        return (0, $"Scrolled {element.Id} into view");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} {ex.Message}");
                    }
                },
                cancellationToken);
        }
    }
}
