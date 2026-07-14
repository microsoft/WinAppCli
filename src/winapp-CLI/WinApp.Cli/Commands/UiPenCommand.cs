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

internal class UiPenCommand : Command, IShortDescription
{
    public string ShortDescription => "Inject synthetic pen/stylus input (taps and ink strokes with pressure and tilt)";

    public static Option<string?> AtOption { get; } = new("--at")
    {
        Description = "Pen contact point as app coordinates x,y (as reported by 'ui inspect'). " +
                      "Defaults to the selector's element center. Ignored when --path is given."
    };

    public static Option<string?> PathOption { get; } = new("--path")
    {
        Description = "Ink stroke path as a whitespace-separated list of x,y pairs, e.g. \"10,10 20,30 40,50\"."
    };

    public static Option<float> PressureOption { get; } = new("--pressure")
    {
        Description = "Pen pressure from 0.0 to 1.0 (default: 0.5).",
        DefaultValueFactory = _ => 0.5f
    };

    public static Option<int> TiltXOption { get; } = new("--tilt-x")
    {
        Description = "Pen tilt along the x-axis in degrees (-90 to 90, default: 0).",
        DefaultValueFactory = _ => 0
    };

    public static Option<int> TiltYOption { get; } = new("--tilt-y")
    {
        Description = "Pen tilt along the y-axis in degrees (-90 to 90, default: 0).",
        DefaultValueFactory = _ => 0
    };

    public static Option<bool> EraserOption { get; } = new("--eraser")
    {
        Description = "Use the eraser end of the pen instead of the tip."
    };

    public static Option<int> DurationOption { get; } = new("--duration-ms")
    {
        Description = "Total glide time in milliseconds distributed across the stroke path segments (default: ~10 ms per segment).",
        DefaultValueFactory = _ => 0
    };

    public UiPenCommand()
        : base("pen", "Inject synthetic pen/stylus input using the Windows synthetic-pointer API. " +
               "Taps or draws ink strokes with configurable pressure, tilt and eraser mode, at an element's " +
               "center or explicit app x,y coordinates. Requires an unlocked, interactive desktop with the " +
               "target window foregroundable (Windows 10 1809+).")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(AtOption);
        Options.Add(PathOption);
        Options.Add(PressureOption);
        Options.Add(TiltXOption);
        Options.Add(TiltYOption);
        Options.Add(EraserOption);
        Options.Add(DurationOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IPointerInput pointerInput,
        IForegroundGuard foregroundGuard,
        IAnsiConsole ansiConsole,
        ILogger<UiPenCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var atStr = parseResult.GetValue(AtOption);
            var pathStr = parseResult.GetValue(PathOption);
            var pressure = parseResult.GetValue(PressureOption);
            var tiltX = parseResult.GetValue(TiltXOption);
            var tiltY = parseResult.GetValue(TiltYOption);
            var eraser = parseResult.GetValue(EraserOption);
            var durationMs = parseResult.GetValue(DurationOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (pressure < 0f || pressure > 1f)
            {
                logger.LogError("{Symbol} --pressure must be between 0.0 and 1.0.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--pressure must be between 0.0 and 1.0.");
                return 1;
            }

            if (durationMs < 0)
            {
                logger.LogError("{Symbol} --duration-ms must be zero or positive.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--duration-ms must be zero or positive.");
                return 1;
            }

            if (tiltX < -90 || tiltX > 90 || tiltY < -90 || tiltY > 90)
            {
                logger.LogError("{Symbol} --tilt-x and --tilt-y must be between -90 and 90 degrees.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--tilt-x and --tilt-y must be between -90 and 90 degrees.");
                return 1;
            }

            List<PointerPoint>? path = null;
            if (!string.IsNullOrWhiteSpace(pathStr))
            {
                if (!PointerGesturePlanner.TryParsePath(pathStr, out path))
                {
                    logger.LogError("{Symbol} --path must be whitespace-separated x,y pairs (e.g. \"10,10 20,30\"). Got '{Path}'.", UiSymbols.Error, pathStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--path must be whitespace-separated x,y pairs (e.g. \"10,10 20,30\"). Got '{pathStr}'.", pathStr);
                    return 1;
                }
            }

            PointerPoint? at = null;
            if (!string.IsNullOrWhiteSpace(atStr))
            {
                if (!PointerGesturePlanner.TryParsePoint(atStr, out var atPoint))
                {
                    logger.LogError("{Symbol} --at must be a valid x,y pair (e.g. 100,200). Got '{At}'.", UiSymbols.Error, atStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--at must be a valid x,y pair (e.g. 100,200). Got '{atStr}'.", atStr);
                    return 1;
                }
                at = atPoint;
            }

            if (path is null && at is null && string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} Provide a target: a selector, --at x,y, or --path.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Provide a target: a selector, --at x,y, or --path.");
                return 1;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                long targetHwnd = session.WindowHandle;
                var targetLabel = pathStr ?? selectorStr ?? atStr;

                // Build the ink path: explicit --path wins; else --at; else the selector's center.
                if (path is null)
                {
                    PointerPoint contact;
                    if (at is not null)
                    {
                        contact = at.Value;
                    }
                    else
                    {
                        var selector = selectorService.Parse(selectorStr!);
                        var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
                        if (element is null)
                        {
                            UiErrors.ElementNotFound(logger, selectorStr!, json);
                            return 1;
                        }

                        if (element.Width == 0 || element.Height == 0)
                        {
                            logger.LogError("{Symbol} Element has zero size — cannot use its center as a pen point.", UiSymbols.Error);
                            UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot use its center as a pen point.", selectorStr);
                            return 1;
                        }

                        targetHwnd = element.WindowHandle ?? session.WindowHandle;

                        if (targetHwnd != 0)
                        {
                            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                            await Task.Delay(100, cancellationToken);
                        }

                        var stable = await GestureTargeting.ResolveStableAsync(
                            uiAutomation, session, selector, element,
                            GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
                        if (!GestureTargeting.TryReport(stable, logger, json, selectorStr!, "pen"))
                        {
                            return 1;
                        }
                        contact = new PointerPoint(stable.CenterX, stable.CenterY);
                    }

                    path = [contact];
                }

                if (targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                // Refuse to inject without a resolved target window (F1): with hwnd 0 the foreground
                // guard cannot verify the injection lands on the intended window, and the OS-wide pen
                // input would hit whatever is foreground. Fail closed instead.
                if (targetHwnd == 0)
                {
                    logger.LogError("{Symbol} No target window could be resolved — refusing to inject pen input (it could hit the wrong window).", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                        "No target window could be resolved — refusing to inject pen input. Target an app window (via --app/--window) whose element resolves to a window handle.");
                    return 1;
                }

                // Resolve the target window rect to bounds-check every ink point before injecting.
                if (!uiAutomation.TryGetWindowRect(targetHwnd, out var windowRect))
                {
                    logger.LogError("{Symbol} Could not read the target window rectangle — refusing to inject pen input.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                        "Could not read the target window rectangle — refusing to inject pen input.");
                    return 1;
                }

                // Every ink point (selector center, explicit --at, or --path waypoint) must fall inside
                // the target window — reject out-of-bounds coordinates and inject nothing.
                var outOfBounds = PointerGesturePlanner.FirstOutOfBounds(windowRect, path);
                if (outOfBounds is not null)
                {
                    logger.LogError(
                        "{Symbol} Point ({X}, {Y}) is outside the target window ({Left},{Top})-({Right},{Bottom}) — refusing to inject pen input.",
                        UiSymbols.Error, outOfBounds.Value.X, outOfBounds.Value.Y,
                        windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        $"Point ({outOfBounds.Value.X},{outOfBounds.Value.Y}) is outside the target window " +
                        $"({windowRect.Left},{windowRect.Top})-({windowRect.Right},{windowRect.Bottom}) — no input injected.");
                    return 1;
                }

                // Final foreground gate before the OS-wide injection (matches click/drag/hover).
                if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "pen"))
                {
                    return 1;
                }

                pointerInput.Pen(path, pressure, tiltX, tiltY, eraser, durationMs);

                var action = eraser ? "erase" : (path.Count > 1 ? "draw" : "tap");

                if (json)
                {
                    var result = new UiPenResult
                    {
                        Action = action,
                        Target = targetLabel,
                        Points = path.Select(p => new UiPointResult { X = p.X, Y = p.Y }).ToArray(),
                        Pressure = pressure,
                        TiltX = tiltX,
                        TiltY = tiltY,
                        Eraser = eraser,
                        DurationMs = durationMs,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiPenResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} pen {Action} with {Count} point(s), pressure {Pressure:0.00}",
                        UiSymbols.Check, action, path.Count, pressure);
                }

                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (InvalidOperationException injectEx)
            {
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, injectEx.Message);
                UiJsonError.Emit(json, UiJsonError.CodeInjectionUnsupported, injectEx.Message);
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
