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
using WinApp.Cli.Services.InteractiveDesktop;

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
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiInvokeCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui invoke";

        /// <remarks>
        /// Spec §6.4: the call itself is a UIA pattern, but an invoked control may synchronously
        /// activate, focus, toggle, select, expand, or open transient UI, so it is treated as
        /// desktop-exclusive and holds <c>active.lock</c> across the pattern call.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult) => UiTurnMode.DesktopExclusive;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "invoke", json);
                return 1;
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            // Preflight rejected a missing selector, so this is non-null by construction.
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument)!;
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            try
            {
                // Resolution before the section is advisory only: it produces a clear element_not_found
                // without holding the desktop. The element actually invoked is re-resolved inside.
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var selector = selectorService.Parse(selectorStr);
                var advisory = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);

                if (advisory is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                string pattern;
                UiElement element;
                UiElement invoked;

                // The pattern call is the desktop-sensitive moment; output formatting below is not.
                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Spec §10.5: never invoke an element resolved before the queue wait. Another
                    // workflow may have navigated, closed, or rebuilt the tree in the meantime.
                    element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken)
                        ?? throw new UiElementNotFoundException(selectorStr);

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, element.WindowHandle ?? session.WindowHandle, session.ProcessId,
                            logger, json, "invoke", parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    invoked = element;
                    try
                    {
                        pattern = await uiAutomation.InvokeAsync(session, element, cancellationToken);
                    }
                    catch (InvalidOperationException) when (element.InvokableAncestor is { } ancestor)
                    {
                        // Element isn't invokable but has an invokable ancestor — invoke that instead
                        pattern = await uiAutomation.InvokeAsync(session, ancestor, cancellationToken);
                        invoked = ancestor;
                    }
                }

                if (!ReferenceEquals(invoked, element))
                {
                    if (json)
                    {
                        var ancestorResult = new UiInvokeResult { ElementId = invoked.Selector ?? invoked.Id ?? "", Pattern = pattern, Hwnd = session.WindowHandle };
                        ansiConsole.Profile.Out.Writer.WriteLine(
                            JsonSerializer.Serialize(ancestorResult, UiJsonContext.Default.UiInvokeResult));
                    }
                    else
                    {
                        logger.LogInformation("Invoked ancestor {Selector} \"{Name}\" via {Pattern} (matched text element was not invokable)",
                            invoked.Selector ?? invoked.Id, invoked.Name, pattern);
                    }
                    return 0;
                }

                if (json)
                {
                    var result = new UiInvokeResult { ElementId = (element.Selector ?? element.Id ?? ""), Pattern = pattern, Hwnd = session.WindowHandle };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInvokeResult));
                }
                else
                {
                    logger.LogInformation("Invoked {ElementId} via {Pattern}", (element.Selector ?? element.Id ?? ""), pattern);
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex) when (!UiCoordinatedAction.IsCoordinationFault(ex))
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}
