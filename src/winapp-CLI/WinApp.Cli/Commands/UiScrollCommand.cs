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
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Commands;

internal class UiScrollCommand : Command, IShortDescription
{
    public string ShortDescription => "Scroll a container element";

    /// <summary>One mouse-wheel detent in WHEEL_DELTA units, the granularity SendInput's wheel expects.</summary>
    private const int WheelDelta = 120;

    public static Option<string?> DirectionOption { get; }
    public static Option<string?> ToOption { get; }
    public static Option<int?> WheelOption { get; }

    static UiScrollCommand()
    {
        DirectionOption = new Option<string?>("--direction")
        {
            Description = "Scroll direction: up, down, left, right"
        };

        ToOption = new Option<string?>("--to")
        {
            Description = "Scroll to position: top, bottom"
        };

        WheelOption = new Option<int?>("--wheel")
        {
            Description = "Rotate the mouse wheel over the element by this many notches (1 = one notch up, -1 = one notch down). " +
                          "Synthesizes real wheel input instead of using ScrollPattern."
        };
    }

    public UiScrollCommand()
        : base("scroll", "Scroll a container element using ScrollPattern. " +
               "Use --direction to scroll incrementally, --to to jump to top/bottom, or --wheel to synthesize mouse-wheel input.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(DirectionOption);
        Options.Add(ToOption);
        Options.Add(WheelOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IMouseInput mouseInput,
        IForegroundGuard foregroundGuard,
        IDesktopForegroundService desktopForeground,
        ISystemUiQuery systemQuery,
        IInteractiveDesktopLock desktopLock,
        IAnsiConsole ansiConsole,
        ILogger<UiScrollCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        // Cursor-settle pause (ms) after positioning over the target, before the confirm read + wheel.
        private const int CursorSettleMs = 30;

        protected override string Operation => "ui scroll";

        /// <remarks>
        /// Spec §6.1: <c>--wheel</c> injects OS-wide mouse input at the cursor, so it is desktop-exclusive.
        /// <c>--direction</c> / <c>--to</c> use the UIA <c>ScrollPattern</c>, which works in the background
        /// and stays an observation.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult)
            => parseResult.GetValue(WheelOption) is not null
                ? UiTurnMode.DesktopExclusive
                : UiTurnMode.Observe;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var direction = parseResult.GetValue(DirectionOption);
            var to = parseResult.GetValue(ToOption);
            var wheel = parseResult.GetValue(WheelOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "scroll", json);
                return 1;
            }

            if (direction is null && to is null && wheel is null)
            {
                logger.LogError("{Symbol} Specify --direction (up/down/left/right), --to (top/bottom), or --wheel (delta).", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "Specify --direction (up/down/left/right), --to (top/bottom), or --wheel (delta).");
                return 1;
            }

            int modeCount = (direction is not null ? 1 : 0) + (to is not null ? 1 : 0) + (wheel is not null ? 1 : 0);
            if (modeCount > 1)
            {
                logger.LogError("{Symbol} Specify only one of --direction, --to, or --wheel.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "Specify only one of --direction, --to, or --wheel — they are mutually exclusive.");
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
            var direction = parseResult.GetValue(DirectionOption);
            var to = parseResult.GetValue(ToOption);
            var wheel = parseResult.GetValue(WheelOption);

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

                var targetHwnd = element.WindowHandle ?? session.WindowHandle;

                if (wheel is int notches)
                {
                    if (element.Width == 0 || element.Height == 0)
                    {
                        logger.LogError("{Symbol} Element has zero size — cannot scroll-wheel over it.", UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot scroll-wheel over it.", selectorStr);
                        return 1;
                    }

                    // Foreground, re-resolve, cursor positioning and the wheel injection all share the
                    // desktop and run in one section; the result formatting below does not.
                    await using (await turn.EnterAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // Re-resolve first (spec §10.5): the container may have moved, closed, or been
                        // replaced while this command waited, so the HWND we foreground must be fresh.
                        var stable = await GestureTargeting.ResolveStableAsync(
                            uiAutomation, session, selector, element,
                            GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                        if (!GestureTargeting.TryReport(stable, logger, json, selectorStr, "scroll --wheel"))
                        {
                            return 1;
                        }
                        var centerX = stable.CenterX;
                        var centerY = stable.CenterY;
                        // TryReport returned true, so a settled element was resolved and Element is populated.
                        targetHwnd = stable.Element.WindowHandle ?? session.WindowHandle;

                        if (!DesktopTargetValidation.TryConfirmTargetWindow(
                                systemQuery, targetHwnd, session.ProcessId, logger, json, "scroll --wheel", parseResult.InvocationConfiguration.Error))
                        {
                            return 1;
                        }

                        if (targetHwnd != 0)
                        {
                            desktopForeground.RequestForeground(targetHwnd);
                            await Task.Delay(100, cancellationToken);
                        }

                        // Verify the target STILL holds the foreground as the first gate before the OS-wide
                        // wheel injection. The re-resolve above awaits UIA reads (with delays) during which
                        // another window could steal focus, so we check here — after the awaits, not before
                        // them. Also distinguishes a locked/secure desktop from a wrong-window foreground.
                        if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "scroll --wheel"))
                        {
                            return 1;
                        }

                        // Close the residual re-resolve→wheel race (mirrors click/drag): ScrollWheel positions
                        // the cursor and settles before injecting, which is an unguarded window in which a
                        // still-animating target could drift, routing the wheel to whatever is now under the
                        // pointer. Position the cursor, let it settle, confirm the target hasn't moved, re-check
                        // the foreground, then inject with settleMs: 0 — so a reported ✅ means the wheel went to
                        // the element.
                        mouseInput.MoveCursor(centerX, centerY);
                        await Task.Delay(CursorSettleMs, cancellationToken);

                        var confirmed = await GestureTargeting.ConfirmStillAsync(
                            uiAutomation, session, selector, stable.Element, cancellationToken);
                        if (!GestureTargeting.TryReport(confirmed, logger, json, selectorStr, "scroll --wheel"))
                        {
                            return 1;
                        }
                        centerX = confirmed.CenterX;
                        centerY = confirmed.CenterY;

                        // Final foreground gate after the awaited confirm read (focus could shift during it).
                        if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "scroll --wheel"))
                        {
                            return 1;
                        }

                        // --wheel is expressed in notches for ergonomics; SendInput's mouse wheel works in
                        // WHEEL_DELTA units (120 per detent), so scale up to the raw delta the OS expects. The
                        // cursor is already positioned and the target just confirmed, so skip the inner settle.
                        mouseInput.ScrollWheel(centerX, centerY, notches * WheelDelta, settleMs: 0);
                    }
                }
                else
                {
                    // UIA scrolling is background-safe: no foreground, no cursor, no desktop section.
                    await uiAutomation.ScrollContainerAsync(session, element, direction, to, cancellationToken);
                }

                if (json)
                {
                    var result = new UiScrollResult
                    {
                        ElementId = (element.Selector ?? element.Id ?? ""),
                        Direction = direction,
                        To = to,
                        Wheel = wheel,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiScrollResult));
                }
                else
                {
                    logger.LogInformation("Scrolled {Selector}", selectorStr);
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
