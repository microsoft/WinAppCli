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

internal class UiClickCommand : Command, IShortDescription
{
    public string ShortDescription => "Click an element at its screen coordinates using mouse simulation";

    public static Option<bool> DoubleClickOption { get; } = new("--double")
    {
        Description = "Perform a double-click instead of a single click"
    };

    public static Option<bool> RightClickOption { get; } = new("--right")
    {
        Description = "Perform a right-click instead of a left click"
    };

    public UiClickCommand()
        : base("click", "Click an element by slug or text search using mouse simulation. " +
               "Works on elements that don't support InvokePattern (e.g., column headers, list items). " +
               "Use --double for double-click, --right for right-click.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(DoubleClickOption);
        Options.Add(RightClickOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IAnsiConsole ansiConsole,
        ILogger<UiClickCommand> logger) : AsynchronousCommandLineAction
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

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "click");
                return 1;
            }

            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var doubleClick = parseResult.GetValue(DoubleClickOption);
            var rightClick = parseResult.GetValue(RightClickOption);

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

                var clickType = doubleClick ? "double-click" : rightClick ? "right-click" : "click";

                // Get element center from bounding rect
                int centerX = (int)(element.X + element.Width / 2.0);
                int centerY = (int)(element.Y + element.Height / 2.0);

                if (element.Width == 0 || element.Height == 0)
                {
                    logger.LogError("{Symbol} Element has zero size — cannot click.", UiSymbols.Error);
                    return 1;
                }

                // Bring target window to foreground
                if (session.WindowHandle != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)session.WindowHandle));
                    await Task.Delay(100, cancellationToken); // let window activate
                }

                // Perform the click via SendInput
                MouseInput.Click(centerX, centerY, doubleClick, rightClick);

                var elementId = element.Selector ?? element.Id;

                if (json)
                {
                    var result = new UiClickResult
                    {
                        ElementId = elementId,
                        ClickType = clickType,
                        X = centerX,
                        Y = centerY,
                        Hwnd = session.WindowHandle
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiClickResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {ClickType} on {ElementId} at ({X}, {Y})",
                        UiSymbols.Check, clickType, elementId, centerX, centerY);
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
