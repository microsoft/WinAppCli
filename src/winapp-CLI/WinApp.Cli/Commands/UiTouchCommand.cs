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

internal class UiTouchCommand : Command, IShortDescription
{
    public string ShortDescription => "Inject synthetic touch gestures (tap, swipe, pinch, stretch, long-press)";

    private static readonly Dictionary<string, TouchGesture> Gestures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tap"] = TouchGesture.Tap,
        ["double-tap"] = TouchGesture.DoubleTap,
        ["long-press"] = TouchGesture.LongPress,
        ["swipe"] = TouchGesture.Swipe,
        ["pinch"] = TouchGesture.Pinch,
        ["stretch"] = TouchGesture.Stretch,
    };

    public static Option<string?> GestureOption { get; } = new("--gesture", "-g")
    {
        Description = "Gesture to perform: tap, double-tap, long-press, swipe, pinch, stretch (default: tap).",
        DefaultValueFactory = _ => "tap"
    };

    public static Option<string?> AtOption { get; } = new("--at")
    {
        Description = "Explicit start point as app coordinates x,y (as reported by 'ui inspect'). " +
                      "Defaults to the selector's element center."
    };

    public static Option<string?> ToPointOption { get; } = new("--to-point")
    {
        Description = "End point x,y for a swipe (app coordinates). Takes precedence over --direction."
    };

    public static Option<int> DistanceOption { get; } = new("--distance")
    {
        Description = "Distance in pixels for pinch/stretch (finger spread) or swipe.",
        DefaultValueFactory = _ => 0
    };

    public static Option<string?> DirectionOption { get; } = new("--direction")
    {
        Description = "Swipe direction: right (default), left, up, or down. Combined with --distance to compute the end point when --to-point is not given.",
        DefaultValueFactory = _ => "right"
    };

    public static Option<int> HoldOption { get; } = new("--hold-ms")
    {
        Description = "Milliseconds to hold contacts down before lifting (long-press hold time). " +
                      "Defaults to 500 ms when --gesture long-press is used and this option is not set.",
        DefaultValueFactory = _ => 0
    };

    public static Option<int> DurationOption { get; } = new("--duration-ms")
    {
        Description = "Glide time in milliseconds for moving gestures (swipe/pinch/stretch).",
        DefaultValueFactory = _ => 300
    };

    public static Option<int> FingersOption { get; } = new("--fingers")
    {
        Description = "Number of touch contacts (default: 1). Pinch/stretch always use 2.",
        DefaultValueFactory = _ => 1
    };

    public UiTouchCommand()
        : base("touch", "Inject synthetic touch input using the Windows touch-injection API. " +
               "Supports tap, double-tap, long-press, swipe, pinch and stretch gestures at an element's " +
               "center or explicit app x,y coordinates. Requires an unlocked, interactive desktop with the " +
               "target window foregroundable.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(GestureOption);
        Options.Add(AtOption);
        Options.Add(ToPointOption);
        Options.Add(DistanceOption);
        Options.Add(DirectionOption);
        Options.Add(HoldOption);
        Options.Add(DurationOption);
        Options.Add(FingersOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IPointerInput pointerInput,
        IForegroundGuard foregroundGuard,
        IAnsiConsole ansiConsole,
        ILogger<UiTouchCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var gestureStr = parseResult.GetValue(GestureOption) ?? "tap";
            var atStr = parseResult.GetValue(AtOption);
            var toStr = parseResult.GetValue(ToPointOption);
            var distance = parseResult.GetValue(DistanceOption);
            var direction = parseResult.GetValue(DirectionOption);
            var holdMs = parseResult.GetValue(HoldOption);
            var durationMs = parseResult.GetValue(DurationOption);
            var fingers = parseResult.GetValue(FingersOption);

            // All app-independent semantic validation runs BEFORE the missing-app check so that
            // malformed argument values return invalid_arguments, not missing_app (M4 root-cause fix).

            if (!Gestures.TryGetValue(gestureStr, out var gesture))
            {
                logger.LogError("{Symbol} Unknown gesture '{Gesture}'. Allowed: tap, double-tap, long-press, swipe, pinch, stretch.",
                    UiSymbols.Error, gestureStr);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"Unknown gesture '{gestureStr}'. Allowed: tap, double-tap, long-press, swipe, pinch, stretch.");
                return 1;
            }

            if (holdMs < 0 || durationMs < 0 || distance < 0 || fingers < 1)
            {
                logger.LogError("{Symbol} --hold-ms/--duration-ms/--distance must be zero or positive and --fingers >= 1.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    "--hold-ms/--duration-ms/--distance must be zero or positive and --fingers >= 1.");
                return 1;
            }

            // Validate --direction value up front.
            var validDirections = new[] { "right", "left", "up", "down" };
            if (!string.IsNullOrEmpty(direction) && !validDirections.Contains(direction.ToLowerInvariant()))
            {
                logger.LogError("{Symbol} --direction must be one of: right, left, up, down. Got '{Direction}'.", UiSymbols.Error, direction);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"--direction must be one of: right, left, up, down. Got '{direction}'.");
                return 1;
            }

            // Long-press with no explicit --hold-ms defaults to 500 ms (a real long-press).
            // An explicit --hold-ms 0 is a degenerate long-press (indistinguishable from a tap)
            // and is rejected clearly rather than silently rewritten to 500.
            if (gesture is TouchGesture.LongPress)
            {
                bool holdWasSupplied = (parseResult.GetResult(HoldOption)?.Tokens.Count ?? 0) > 0;
                if (!holdWasSupplied)
                {
                    holdMs = 500;
                }
                else if (holdMs == 0)
                {
                    logger.LogError("{Symbol} --hold-ms 0 is invalid with --gesture long-press (degenerate hold). " +
                        "Omit --hold-ms to get the 500 ms default or supply a positive value.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        "--hold-ms 0 is invalid with --gesture long-press. Omit --hold-ms to use the 500 ms default or supply a positive value.");
                    return 1;
                }
            }

            if (fingers > PointerGesturePlanner.MaxContacts)
            {
                logger.LogError("{Symbol} --fingers must be between 1 and {Max} (the touch-injection contact limit).",
                    UiSymbols.Error, PointerGesturePlanner.MaxContacts);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                    $"--fingers must be between 1 and {PointerGesturePlanner.MaxContacts} (the touch-injection contact limit).");
                return 1;
            }

            // Parse an explicit start point up front (mutually independent of selector resolution).
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

            PointerPoint? to = null;
            if (!string.IsNullOrWhiteSpace(toStr))
            {
                if (!PointerGesturePlanner.TryParsePoint(toStr, out var toPoint))
                {
                    logger.LogError("{Symbol} --to-point must be a valid x,y pair (e.g. 300,400). Got '{To}'.", UiSymbols.Error, toStr);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"--to-point must be a valid x,y pair (e.g. 300,400). Got '{toStr}'.", toStr);
                    return 1;
                }
                to = toPoint;
            }

            if (at is null && string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} Provide a target: a selector, or --at x,y.", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "Provide a target: a selector, or --at x,y.");
                return 1;
            }

            if (gesture is TouchGesture.Pinch or TouchGesture.Stretch && distance <= 0)
            {
                logger.LogError("{Symbol} {Gesture} requires --distance (finger spread in pixels).", UiSymbols.Error, gestureStr);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"{gestureStr} requires --distance (finger spread in pixels).");
                return 1;
            }

            if (gesture is TouchGesture.Swipe && to is null && distance <= 0)
            {
                logger.LogError("{Symbol} swipe requires --to-point x,y or --distance (combined with optional --direction).", UiSymbols.Error);
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "swipe requires --to-point x,y or --distance (combined with optional --direction).");
                return 1;
            }

            // Missing-app check runs after all argument validation so invalid arg values return
            // invalid_arguments rather than missing_app.
            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                long targetHwnd = session.WindowHandle;
                PointerPoint start;
                // L1: report the effective target — the --at value when explicit coordinates were given,
                // or the selector when the selector resolved the contact point.
                var targetLabel = at is not null ? atStr : selectorStr;

                if (at is not null)
                {
                    start = at.Value;
                }
                else
                {
                    // Resolve the selector's element center and re-resolve just before injection.
                    var selector = selectorService.Parse(selectorStr!);
                    var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
                    if (element is null)
                    {
                        UiErrors.ElementNotFound(logger, selectorStr!, json);
                        return 1;
                    }

                    if (element.Width == 0 || element.Height == 0)
                    {
                        logger.LogError("{Symbol} Element has zero size — cannot use its center as a touch point.", UiSymbols.Error);
                        UiJsonError.Emit(json, UiJsonError.CodeZeroSize, "Element has zero size — cannot use its center as a touch point.", selectorStr);
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
                    if (!GestureTargeting.TryReport(stable, logger, json, selectorStr!, "touch"))
                    {
                        return 1;
                    }
                    start = new PointerPoint(stable.CenterX, stable.CenterY);
                }

                if (at is not null && targetHwnd != 0)
                {
                    Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
                    await Task.Delay(100, cancellationToken);
                }

                // Refuse to inject without a resolved target window (F1): with hwnd 0 the foreground
                // guard cannot verify the injection lands on the intended window, and the OS-wide touch
                // would hit whatever is foreground. Fail closed instead.
                if (targetHwnd == 0)
                {
                    logger.LogError("{Symbol} No target window could be resolved — refusing to inject touch (it could hit the wrong window).", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                        "No target window could be resolved — refusing to inject touch. Target an app window (via --app/--window) whose element resolves to a window handle.");
                    return 1;
                }

                // Resolve the target window rect to bounds-check every coordinate before injecting.
                if (!uiAutomation.TryGetWindowRect(targetHwnd, out var windowRect))
                {
                    logger.LogError("{Symbol} Could not read the target window rectangle — refusing to inject touch.", UiSymbols.Error);
                    UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                        "Could not read the target window rectangle — refusing to inject touch.");
                    return 1;
                }

                var (contactPaths, points, effectiveFingers) =
                    PointerGesturePlanner.PlanTouch(gesture, start, to, distance, fingers, direction);

                // Every planned point (selector center, explicit --at/--to-point, and generated
                // waypoints) must fall inside the target window — reject out-of-bounds coordinates
                // and inject nothing.
                var outOfBounds = PointerGesturePlanner.FirstOutOfBounds(windowRect, points);
                if (outOfBounds is not null)
                {
                    logger.LogError(
                        "{Symbol} Point ({X}, {Y}) is outside the target window ({Left},{Top})-({Right},{Bottom}) — refusing to inject touch.",
                        UiSymbols.Error, outOfBounds.Value.X, outOfBounds.Value.Y,
                        windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                        $"Point ({outOfBounds.Value.X},{outOfBounds.Value.Y}) is outside the target window " +
                        $"({windowRect.Left},{windowRect.Top})-({windowRect.Right},{windowRect.Bottom}) — no input injected.");
                    return 1;
                }

                // Final foreground gate before the OS-wide injection (matches click/drag/hover).
                if (!foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, "touch"))
                {
                    return 1;
                }

                // M8: narrow the injection_unsupported catch to only the actual injection call so that
                // pre-injection failures (session resolution, element not found, etc.) are NOT
                // mis-classified as injection_unsupported — they surface through GenericError instead.
                try
                {
                    pointerInput.Touch(gesture, contactPaths, holdMs, durationMs);
                }
                catch (InvalidOperationException injectEx)
                {
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, injectEx.Message);
                    UiJsonError.Emit(json, UiJsonError.CodeInjectionUnsupported, injectEx.Message,
                        errorOut: parseResult.InvocationConfiguration.Error);
                    return 1;
                }

                if (json)
                {
                    var result = new UiTouchResult
                    {
                        Gesture = gestureStr.ToLowerInvariant(),
                        Target = targetLabel,
                        Points = points.Select(p => new UiPointResult { X = p.X, Y = p.Y }).ToArray(),
                        Fingers = effectiveFingers,
                        DurationMs = durationMs,
                        HoldMs = holdMs,
                        Hwnd = targetHwnd
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiTouchResult));
                }
                else
                {
                    logger.LogInformation("{Symbol} {Gesture} at ({X}, {Y}) with {Fingers} finger(s)",
                        UiSymbols.Check, gestureStr, start.X, start.Y, effectiveFingers);
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
