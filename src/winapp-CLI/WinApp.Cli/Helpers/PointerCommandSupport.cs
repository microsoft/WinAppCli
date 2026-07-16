// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

internal static class PointerCommandSupport
{
    public readonly record struct ResolvedPoint(bool Ok, PointerPoint Point, long TargetHwnd, string? TargetLabel);

    public static async Task<ResolvedPoint> ResolvePointAsync(
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        UiSessionInfo session,
        string? selectorStr,
        PointerPoint? explicitPoint,
        string? explicitLabel,
        string action,
        string pointKind,
        ILogger logger,
        bool json,
        CancellationToken cancellationToken)
    {
        if (explicitPoint is not null)
        {
            return new ResolvedPoint(true, explicitPoint.Value, session.WindowHandle, explicitLabel);
        }

        var selector = selectorService.Parse(selectorStr!);
        var element = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
        if (element is null)
        {
            UiErrors.ElementNotFound(logger, selectorStr!, json);
            return default;
        }

        if (element.Width == 0 || element.Height == 0)
        {
            logger.LogError("{Symbol} Element has zero size — cannot use its center as a {PointKind}.",
                UiSymbols.Error, pointKind);
            UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                $"Element has zero size — cannot use its center as a {pointKind}.", selectorStr);
            return default;
        }

        long targetHwnd = element.WindowHandle ?? session.WindowHandle;

        if (targetHwnd != 0)
        {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
            await Task.Delay(100, cancellationToken);
        }

        var stable = await GestureTargeting.ResolveStableAsync(
            uiAutomation, session, selector, element,
            GestureTargeting.DefaultMaxReads, GestureTargeting.DefaultReadDelayMs, null, cancellationToken);
        if (!GestureTargeting.TryReport(stable, logger, json, selectorStr!, action))
        {
            return default;
        }

        return new ResolvedPoint(true, new PointerPoint(stable.CenterX, stable.CenterY), targetHwnd, selectorStr);
    }

    public static async Task SetForegroundAsync(long targetHwnd, CancellationToken cancellationToken)
    {
        if (targetHwnd != 0)
        {
            Windows.Win32.PInvoke.SetForegroundWindow(new Windows.Win32.Foundation.HWND((nint)targetHwnd));
            await Task.Delay(100, cancellationToken);
        }
    }

    public static bool TryPrepareInjection(
        IUiAutomationService uiAutomation,
        IForegroundGuard foregroundGuard,
        long targetHwnd,
        IEnumerable<PointerPoint> points,
        string action,
        string inputNoun,
        ILogger logger,
        bool json)
    {
        if (targetHwnd == 0)
        {
            logger.LogError("{Symbol} No target window could be resolved — refusing to inject {InputNoun} (it could hit the wrong window).",
                UiSymbols.Error, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                $"No target window could be resolved — refusing to inject {inputNoun}. Target an app window (via --app/--window) whose element resolves to a window handle.");
            return false;
        }

        if (!uiAutomation.TryGetWindowRect(targetHwnd, out var windowRect))
        {
            logger.LogError("{Symbol} Could not read the target window rectangle — refusing to inject {InputNoun}.",
                UiSymbols.Error, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeNoTarget,
                $"Could not read the target window rectangle — refusing to inject {inputNoun}.");
            return false;
        }

        var outOfBounds = PointerGesturePlanner.FirstOutOfBounds(windowRect, points);
        if (outOfBounds is not null)
        {
            logger.LogError(
                "{Symbol} Point ({X}, {Y}) is outside the target window ({Left},{Top})-({Right},{Bottom}) — refusing to inject {InputNoun}.",
                UiSymbols.Error, outOfBounds.Value.X, outOfBounds.Value.Y,
                windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom, inputNoun);
            UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments,
                $"Point ({outOfBounds.Value.X},{outOfBounds.Value.Y}) is outside the target window " +
                $"({windowRect.Left},{windowRect.Top})-({windowRect.Right},{windowRect.Bottom}) — no input injected.");
            return false;
        }

        return foregroundGuard.TryEnsureForeground(targetHwnd, logger, json, action);
    }

    public static bool TryInject(Action inject, ILogger logger, bool json, TextWriter? errorOut)
    {
        try
        {
            inject();
            return true;
        }
        catch (InvalidOperationException injectEx)
        {
            logger.LogError("{Symbol} {Message}", UiSymbols.Error, injectEx.Message);
            UiJsonError.Emit(json, UiJsonError.CodeInjectionUnsupported, injectEx.Message, errorOut: errorOut);
            return false;
        }
    }

    public static string? RemoteInjectionWarning(IForegroundGuard foregroundGuard, string inputKind)
        => ForegroundGuard.RemoteInjectionWarning(foregroundGuard.IsRemoteSession(), inputKind);
}
