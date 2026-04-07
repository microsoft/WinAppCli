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

internal class UiGetTextCommand : Command, IShortDescription
{
    public string ShortDescription => "Read text content from an element";

    public UiGetTextCommand()
        : base("get-text", "Read text content from an element. " +
               "Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox), then Name (labels). " +
               "Usage: winapp ui get-text <selector> -a <app>")
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
        IAnsiConsole ansiConsole,
        ILogger<UiGetTextCommand> logger) : AsynchronousCommandLineAction
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
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "get-text");
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

                var text = await uiAutomation.GetTextAsync(session, element, cancellationToken);

                if (json)
                {
                    var result = new UiGetTextResult
                    {
                        ElementId = element.Selector ?? element.Id,
                        Text = text
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiGetTextResult));
                    return 0;
                }

                if (text is null)
                {
                    logger.LogInformation("No text content found on {ElementId}", element.Selector ?? element.Id);
                }
                else
                {
                    // Strip carriage returns (Windows line endings → Unix) but preserve newlines
                    ansiConsole.WriteLine(text.Replace("\r\n", "\n").TrimEnd('\r', '\n'));
                }

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
