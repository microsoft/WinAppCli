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
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        ILogger<UiScrollIntoViewCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
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

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var selector = selectorService.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);

                if (element is null)
                {
                    logger.LogError("No element found matching '{Selector}'", selectorStr);
                    return 1;
                }

                await uiAutomation.ScrollIntoViewAsync(session, element, cancellationToken);
                logger.LogInformation("Scrolled {ElementId} into view", element.Id);
                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                logger.LogError("Failed to access UI element — the element may no longer exist or the app may have navigated. Try re-running 'inspect'.");
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
                logger.LogError("{Message}", ex.Message);
                return 1;
            }
        }
    }
}
