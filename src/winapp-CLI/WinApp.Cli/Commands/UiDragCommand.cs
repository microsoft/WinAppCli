// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiDragCommand : Command, IShortDescription
{
    public string ShortDescription => "Drag the mouse from one point to another within an element";

    public static Argument<string?> FromArgument { get; } = new("from")
    {
        Description = "Start point as x,y offset (in pixels) from the element's top-left corner, e.g. 40,50",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Argument<string?> ToArgument { get; } = new("to")
    {
        Description = "End point as x,y offset (in pixels) from the element's top-left corner, e.g. 60,30",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Option<bool> RightButtonOption { get; } = new("--right")
    {
        Description = "Drag with the right mouse button instead of the left button"
    };

    public UiDragCommand()
        : base("drag", "Press the mouse button at a point inside an element, move to another point, then release. " +
               "Coordinates are x,y offsets (in pixels) from the element's top-left corner. " +
               "Useful for reorder/resize/slider gestures and drag-and-drop. Use --right for a right-button drag.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Arguments.Add(FromArgument);
        Arguments.Add(ToArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(RightButtonOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IMouseInput mouseInput,
        IAnsiConsole ansiConsole,
        ILogger<UiDragCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var fromStr = parseResult.GetValue(FromArgument);
            var toStr = parseResult.GetValue(ToArgument);
            var rightButton = parseResult.GetValue(RightButtonOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "drag", json);
                return 1;
            }

            if (!TryParsePoint(fromStr, out int fromX, out int fromY))
            {
                logger.LogError("{Symbol} Invalid <from> point — expected x,y (e.g. 40,50).", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Invalid <from> point — expected x,y (e.g. 40,50).");
                return 1;
            }

            if (!TryParsePoint(toStr, out int toX, out int toY))
            {
                logger.LogError("{Symbol} Invalid <to> point — expected x,y (e.g. 60,30).", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Invalid <to> point — expected x,y (e.g. 60,30).");
                return 1;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var selector = selectorService.Parse(selectorStr);
                var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);

                if (element is null)
                {
                    UiErrors.ElementNotFound(logger, selectorStr, json);
                    return 1;
                }

                // Coordinates are offsets from the element's top-left corner (screen space).
                int fromScreenX = (int)(element.X + fromX);
                int fromScreenY = (int)(element.Y + fromY);
                int toScreenX = (int)(element.X + toX);
                int toScreenY = (int)(element.Y + toY);

                var targetHwnd = element.WindowHandle ?? session.WindowHandle;

                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                mouseInput.Drag(fromScreenX, fromScreenY, toScreenX, toScreenY, rightButton);

                var elementId = element.Selector ?? element.Id ?? "";
                var button = rightButton ? "right" : "left";

                if (json)
                {
                    var result = new UiDragResult
                    {
                        ElementId = elementId,
                        FromX = fromScreenX,
                        FromY = fromScreenY,
                        ToX = toScreenX,
                        ToY = toScreenY,
                        Button = button,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiDragResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {Button}-dragged {ElementId} from ({FromX}, {FromY}) to ({ToX}, {ToY})",
                        UiSymbols.Check, button, elementId, fromScreenX, fromScreenY, toScreenX, toScreenY);
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        private static bool TryParsePoint(string? value, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var parts = value.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }
    }
}
