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

internal class UiFocusCommand : Command, IShortDescription
{
    public string ShortDescription => "Move keyboard focus to an element";

    public UiFocusCommand()
        : base("focus", "Move keyboard focus to the specified element using UIA SetFocus.")
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
        ILogger<UiFocusCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui focus";

        /// <remarks>
        /// UIA <c>SetFocus</c> moves the shared keyboard focus, which is exactly the resource that lets
        /// concurrent workflows dismiss each other's transient UI.
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
                UiErrors.MissingSelector(logger, "focus", json);
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
                // Advisory resolution: gives a clean element_not_found without holding the desktop.
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var selector = selectorService.Parse(selectorStr);
                var advisory = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);

                if (advisory is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                UiElement element;

                // Only the focus change itself needs the desktop; the result formatting below does not.
                await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Spec §10.5: focus the element as it exists now, not as it existed before the wait.
                    element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken)
                        ?? throw new UiElementNotFoundException(selectorStr);

                    if (!DesktopTargetValidation.TryConfirmTargetWindow(
                            systemQuery, element.WindowHandle ?? session.WindowHandle, session.ProcessId,
                            logger, json, "focus", parseResult.InvocationConfiguration.Error))
                    {
                        return 1;
                    }

                    await uiAutomation.FocusAsync(session, element, cancellationToken);
                }

                if (json)
                {
                    var result = new UiFocusResult { ElementId = (element.Selector ?? element.Id ?? ""), Hwnd = session.WindowHandle };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiFocusResult));
                }
                else
                {
                    logger.LogInformation("Focused {ElementId}", (element.Selector ?? element.Id ?? ""));
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
