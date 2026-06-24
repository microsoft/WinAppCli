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
        IAnsiConsole ansiConsole,
        ILogger<UiScrollCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
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

            var direction = parseResult.GetValue(DirectionOption);
            var to = parseResult.GetValue(ToOption);
            var wheel = parseResult.GetValue(WheelOption);

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
                    int centerX = (int)(element.X + element.Width / 2.0);
                    int centerY = (int)(element.Y + element.Height / 2.0);

                    if (element.Width == 0 || element.Height == 0)
                    {
                        logger.LogError("{Symbol} Element has zero size — cannot scroll-wheel over it.", UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot scroll-wheel over it.", selectorStr);
                        return 1;
                    }

                    if (targetHwnd != 0)
                    {
                        Windows.Win32.PInvoke.SetForegroundWindow(
                            new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                        await Task.Delay(100, cancellationToken);

                        // The wheel event is injected at screen coordinates; verify the target actually
                        // came to the foreground so it doesn't scroll whatever window is on top instead.
                        if (!ForegroundGuard.ForegroundBelongsTo(targetHwnd))
                        {
                            logger.LogError(
                                "{Symbol} Target window is not in the foreground — refusing scroll --wheel to avoid acting on the wrong window. Focus or click the window first.",
                                UiSymbols.Error);
                            UiJsonError.Emit(json, UiJsonError.CodeForegroundNotTarget,
                                "Target window is not in the foreground — refusing scroll --wheel to avoid injecting into the wrong window. Bring it to the foreground first.");
                            return 1;
                        }
                    }

                    // --wheel is expressed in notches for ergonomics; SendInput's mouse wheel works in
                    // WHEEL_DELTA units (120 per detent), so scale up to the raw delta the OS expects.
                    mouseInput.ScrollWheel(centerX, centerY, notches * WheelDelta);
                }
                else
                {
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
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}
