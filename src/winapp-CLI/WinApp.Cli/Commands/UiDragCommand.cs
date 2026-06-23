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
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiDragCommand : Command, IShortDescription
{
    public string ShortDescription => "Drag from one element/point to another element/point";

    // The command accepts two forms, disambiguated by how many positional args are supplied:
    //   2-arg (preferred): drag <from> <to>            — each is a selector (element center) or app x,y coords
    //   3-arg (legacy):    drag <selector> <from> <to> — <from>/<to> are x,y offsets from the element's top-left
    public static Argument<string?> FromArgument { get; } = new("from")
    {
        Description = "Start point — an element selector (drags from its center) or app coordinates x,y as reported by " +
                      "'ui inspect' (e.g. pn-list-d736 or 100,200). Legacy 3-arg form: the element selector to drag within.",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Argument<string?> ToArgument { get; } = new("to")
    {
        Description = "End point — an element selector (drops at its center) or app coordinates x,y as reported by " +
                      "'ui inspect' (e.g. pn-target-d746 or 300,400). Legacy 3-arg form: the start x,y offset from the element's top-left.",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Argument<string?> LegacyToOffsetArgument { get; } = new("to-offset")
    {
        Description = "(Legacy 3-arg form only) the end x,y offset from the element's top-left corner, e.g. 60,30. " +
                      "Omit this to use the preferred 2-arg form.",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Option<bool> RightButtonOption { get; } = new("--right")
    {
        Description = "Drag with the right mouse button instead of the left button"
    };

    public UiDragCommand()
        : base("drag", "Press the mouse button at one point, move to another, then release. Preferred form: " +
               "'drag <from> <to>', where <from>/<to> are each an element selector (uses the element's center) or " +
               "app-relative x,y coordinates as reported by 'ui inspect'. Legacy form: 'drag <selector> <fromX,fromY> " +
               "<toX,toY>' with offsets from the element's top-left corner. Useful for reorder/resize/slider gestures " +
               "and drag-and-drop. Use --right for a right-button drag.")
    {
        Arguments.Add(FromArgument);
        Arguments.Add(ToArgument);
        Arguments.Add(LegacyToOffsetArgument);
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
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var arg0 = parseResult.GetValue(FromArgument);
            var arg1 = parseResult.GetValue(ToArgument);
            var legacyToOffset = parseResult.GetValue(LegacyToOffsetArgument);
            var rightButton = parseResult.GetValue(RightButtonOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // A third positional value means the legacy 'drag <selector> <fromOffset> <toOffset>' form.
            var legacy = !string.IsNullOrWhiteSpace(legacyToOffset);

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                int fromX, fromY, toX, toY;
                long targetHwnd;
                string elementId = "", fromToken = "", toToken = "";

                if (legacy)
                {
                    if (string.IsNullOrWhiteSpace(arg0))
                    {
                        UiErrors.MissingSelector(logger, "drag", json);
                        return 1;
                    }

                    if (!TryParsePoint(arg1, out int offFromX, out int offFromY))
                    {
                        logger.LogError("{Symbol} Invalid <from> offset '{Arg}' — expected x,y (e.g. 40,50). You passed 3 arguments, which selects the legacy 'drag <selector> <fromOffset> <toOffset>' form; for selector→selector or selector→coords use just 2 arguments: 'drag <from> <to>'.", UiSymbols.Error, arg1);
                        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Invalid <from> offset — expected x,y (e.g. 40,50). With 3 arguments drag uses the legacy '<selector> <fromOffset> <toOffset>' form; for selector→selector or selector→coords pass only 2 arguments.");
                        return 1;
                    }

                    if (!TryParsePoint(legacyToOffset, out int offToX, out int offToY))
                    {
                        logger.LogError("{Symbol} Invalid <to> offset '{Arg}' — expected x,y (e.g. 60,30). With 3 arguments drag uses the legacy '<selector> <fromOffset> <toOffset>' form; for selector→selector or selector→coords pass only 2 arguments: 'drag <from> <to>'.", UiSymbols.Error, legacyToOffset);
                        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Invalid <to> offset — expected x,y (e.g. 60,30). With 3 arguments drag uses the legacy '<selector> <fromOffset> <toOffset>' form; for selector→selector or selector→coords pass only 2 arguments.");
                        return 1;
                    }

                    var selector = selectorService.Parse(arg0);
                    var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
                    if (element is null)
                    {
                        UiErrors.ElementNotFound(logger, arg0, json);
                        return 1;
                    }

                    // Offsets are relative to the element's top-left corner (element coords are screen space).
                    fromX = (int)(element.X + offFromX);
                    fromY = (int)(element.Y + offFromY);
                    toX = (int)(element.X + offToX);
                    toY = (int)(element.Y + offToY);
                    targetHwnd = element.WindowHandle ?? session.WindowHandle;
                    elementId = element.Selector ?? element.Id ?? "";
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(arg0) || string.IsNullOrWhiteSpace(arg1))
                    {
                        logger.LogError("{Symbol} Specify both <from> and <to> — each is an element selector or x,y coordinates.", UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                            "Specify both <from> and <to> — each is an element selector or x,y coordinates.");
                        return 1;
                    }

                    var from = await ResolveEndpointAsync(arg0, "from", session, json, cancellationToken);
                    if (!from.Ok)
                    {
                        return 1;
                    }

                    var to = await ResolveEndpointAsync(arg1, "to", session, json, cancellationToken);
                    if (!to.Ok)
                    {
                        return 1;
                    }

                    fromX = from.X;
                    fromY = from.Y;
                    toX = to.X;
                    toY = to.Y;
                    // Prefer the HWND of whichever endpoint resolved from a real element; fall back to the
                    // session window when both endpoints are bare coordinates.
                    targetHwnd = from.Hwnd != 0 ? from.Hwnd : (to.Hwnd != 0 ? to.Hwnd : session.WindowHandle);
                    fromToken = arg0;
                    toToken = arg1;
                }

                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                // The drag is injected OS-wide at screen coordinates; if SetForegroundWindow didn't take,
                // it would land on whatever window is on top at those coords. Verify the target won focus.
                if (targetHwnd != 0 && !ForegroundGuard.ForegroundBelongsTo(targetHwnd))
                {
                    logger.LogError(
                        "{Symbol} Target window is not in the foreground — refusing to drag to avoid acting on the wrong window. Focus or click the window first.",
                        UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                        "Target window is not in the foreground — refusing to drag to avoid injecting into the wrong window. Bring it to the foreground first.");
                    return 1;
                }

                mouseInput.Drag(fromX, fromY, toX, toY, rightButton);

                var button = rightButton ? "right" : "left";

                if (json)
                {
                    var result = new UiDragResult
                    {
                        ElementId = elementId,
                        From = fromToken,
                        To = toToken,
                        FromX = fromX,
                        FromY = fromY,
                        ToX = toX,
                        ToY = toY,
                        Button = button,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiDragResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {Button}-dragged from ({FromX}, {FromY}) to ({ToX}, {ToY})",
                        UiSymbols.Check, button, fromX, fromY, toX, toY);
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

        private readonly record struct Endpoint(bool Ok, int X, int Y, long Hwnd);

        /// <summary>
        /// Resolves a drag endpoint token into screen coordinates. A token of the form <c>x,y</c> is taken as
        /// app coordinates (the same space <c>ui inspect</c> reports); anything else is treated as an element
        /// selector and resolves to that element's center. Emits the appropriate error and returns
        /// <see cref="Endpoint.Ok"/> = <see langword="false"/> on failure.
        /// </summary>
        private async Task<Endpoint> ResolveEndpointAsync(
            string token, string label, UiSessionInfo session, bool json, CancellationToken cancellationToken)
        {
            if (TryParsePoint(token, out int px, out int py))
            {
                return new Endpoint(true, px, py, 0);
            }

            var selector = selectorService.Parse(token);
            var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
            if (element is null)
            {
                UiErrors.ElementNotFound(logger, token, json);
                return new Endpoint(false, 0, 0, 0);
            }

            if (element.Width == 0 || element.Height == 0)
            {
                logger.LogError("{Symbol} Element for <{Label}> has zero size — cannot use its center as a drag point.", UiSymbols.Error, label);
                UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                    $"Element for <{label}> has zero size — cannot use its center as a drag point.", token);
                return new Endpoint(false, 0, 0, 0);
            }

            int centerX = (int)(element.X + element.Width / 2.0);
            int centerY = (int)(element.Y + element.Height / 2.0);
            return new Endpoint(true, centerX, centerY, element.WindowHandle ?? 0);
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
