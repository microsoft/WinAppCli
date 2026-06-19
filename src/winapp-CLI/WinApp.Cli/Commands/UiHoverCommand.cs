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

internal class UiHoverCommand : Command, IShortDescription
{
    public string ShortDescription => "Move the mouse to an element to trigger hover effects like tooltips";

    public static Option<int> DwellTimeOption { get; } = new("--dwell-time")
    {
        Description = "Time in milliseconds to wait after hovering for hover effects to appear (default: 800)",
        DefaultValueFactory = _ => 800
    };

    public UiHoverCommand()
        : base("hover", "Move the mouse to an element's center to trigger hover effects (tooltips, flyouts, visual states). " +
               "Uses SendInput for realistic mouse movement and waits for a configurable dwell time.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(DwellTimeOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IAnsiConsole ansiConsole,
        ILogger<UiHoverCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var dwellTime = parseResult.GetValue(DwellTimeOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "hover", json);
                return 1;
            }

            if (dwellTime < 0 || dwellTime > 10_000)
            {
                logger.LogError("{Symbol} --dwell-time must be between 0 and 10000 ms.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--dwell-time must be between 0 and 10000 ms.");
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

                int centerX = (int)(element.X + element.Width / 2.0);
                int centerY = (int)(element.Y + element.Height / 2.0);

                if (element.Width == 0 || element.Height == 0)
                {
                    logger.LogError("{Symbol} Element has zero size — cannot hover.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot hover.", selectorStr);
                    return 1;
                }

                // Bring target window to foreground
                if (session.WindowHandle != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(
                        new Windows.Win32.Foundation.HWND((nint)session.WindowHandle));
                    await Task.Delay(100, cancellationToken);
                }

                // Move mouse to element center with a small wiggle to trigger hover detection
                MouseInput.Hover(centerX, centerY);

                // Wait for dwell time to allow hover effects to appear
                await Task.Delay(dwellTime, cancellationToken);

                var elementId = element.Selector ?? element.Id ?? "";

                if (json)
                {
                    var result = new UiHoverResult
                    {
                        ElementId = elementId,
                        X = centerX,
                        Y = centerY,
                        DwellTimeMs = dwellTime,
                        Hwnd = session.WindowHandle
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiHoverResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} Hovered on {ElementId} at ({X}, {Y}) — dwelled {DwellTime}ms",
                        UiSymbols.Check, elementId, centerX, centerY, dwellTime);
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
