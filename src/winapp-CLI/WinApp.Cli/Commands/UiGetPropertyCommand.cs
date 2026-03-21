// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiGetPropertyCommand : Command, IShortDescription
{
    public string ShortDescription => "Read property values from an element";

    public UiGetPropertyCommand()
        : base("get-property", "Read UIA property values from an element. Specify --property for a single property or omit for all.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.PropertyOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiGetPropertyCommand> logger) : AsynchronousCommandLineAction
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
            var propertyName = parseResult.GetValue(SharedUiOptions.PropertyOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} A selector is required.", UiSymbols.Error);
                return 1;
            }

            return await statusService.ExecuteWithStatusAsync(
                "Reading properties...",
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

                        var props = await uiAutomation.GetPropertiesAsync(session, element, propertyName, ct);

                        if (json)
                        {
                            var result = new UiPropertyResult { ElementId = element.Id, Properties = props };
                            ansiConsole.Profile.Out.Writer.WriteLine(
                                JsonSerializer.Serialize(result, UiJsonContext.Default.UiPropertyResult));
                        }

                        return (0, $"{element.Id}: {props.Count} properties");
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
