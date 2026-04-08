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

internal class UiInvokeCommand : Command, IShortDescription
{
    public string ShortDescription => "Activate an element via UIA patterns (Invoke, Toggle, etc.)";

    public UiInvokeCommand()
        : base("invoke", "Activate an element by slug or text search. " +
               "Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.")
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
        ILogger<UiInvokeCommand> logger) : AsynchronousCommandLineAction
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
                UiErrors.MissingSelector(logger, "invoke");
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

                string pattern;
                try
                {
                    pattern = await uiAutomation.InvokeAsync(session, element, cancellationToken);
                }
                catch (InvalidOperationException) when (element.InvokableAncestor is { } ancestor)
                {
                    // Element isn't invokable but has an invokable ancestor — invoke that instead
                    pattern = await uiAutomation.InvokeAsync(session, ancestor, cancellationToken);
                    logger.LogInformation("Invoked ancestor {Selector} \"{Name}\" via {Pattern} (matched text element was not invokable)",
                        ancestor.Selector ?? ancestor.Id, ancestor.Name, pattern);
                    if (json)
                    {
                        var result = new UiInvokeResult { ElementId = ancestor.Selector ?? ancestor.Id, Pattern = pattern };
                        ansiConsole.Profile.Out.Writer.WriteLine(
                            JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                    }
                    return 0;
                }

                if (json)
                {
                    var result = new UiInvokeResult { ElementId = element.Selector ?? element.Id, Pattern = pattern };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                }

                logger.LogInformation("Invoked {ElementId} via {Pattern}", element.Selector ?? element.Id, pattern);
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
