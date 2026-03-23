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
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.PropertyOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IAnsiConsole ansiConsole,
        ILogger<UiGetPropertyCommand> logger) : AsynchronousCommandLineAction
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
            var propertyName = parseResult.GetValue(SharedUiOptions.PropertyOption);
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

                var props = await uiAutomation.GetPropertiesAsync(session, element, propertyName, cancellationToken);

                if (json)
                {
                    var result = new UiPropertyResult { ElementId = element.Id, Properties = props };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiPropertyResult));
                }
                else
                {
                    foreach (var kvp in props)
                    {
                        var value = kvp.Value?.ToString() ?? "(null)";
                        ansiConsole.WriteLine($"  {kvp.Key}: {value}");
                    }
                }

                logger.LogInformation("{ElementId}: {Count} properties", element.Id, props.Count);
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
