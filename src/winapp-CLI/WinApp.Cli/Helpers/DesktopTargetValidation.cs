// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Post-wait target validation for cooperative desktop turns (issue #764).
/// </summary>
/// <remarks>
/// A <c>DesktopExclusive</c> command may sit in the queue for an unbounded time while another workflow
/// drives the desktop. In that gap the target window can close, move, or exit and have its HWND reused
/// by an unrelated process. Spec §10.5 therefore requires the HWND, PID, element and bounds actually
/// acted upon to be resolved or revalidated <em>inside</em> the desktop section — never carried across
/// the wait. These helpers are the shared check that the window a command is about to act on is still
/// the one it resolved.
/// </remarks>
internal static class DesktopTargetValidation
{
    /// <summary>
    /// Confirms the window a command is about to act on still exists and still belongs to the process
    /// the command resolved. Emits the standard stale-target error and returns <see langword="false"/>
    /// when it does not.
    /// </summary>
    /// <param name="expectedProcessId">
    /// The PID resolved for this target. A mismatch means the original window closed and Windows reused
    /// its handle for a different process — acting on it would drive the wrong application.
    /// </param>
    /// <param name="action">Verb used in the message, e.g. "click", "invoke".</param>
    public static bool TryConfirmTargetWindow(
        ISystemUiQuery systemQuery,
        long hwnd,
        int expectedProcessId,
        ILogger logger,
        bool json,
        string action,
        TextWriter? errorOut = null)
    {
        if (hwnd == 0)
        {
            // A bare-coordinate target has no window to confirm; the foreground guard is the gate there.
            return true;
        }

        var actualProcessId = systemQuery.GetProcessIdForWindow(hwnd);
        if (actualProcessId == 0)
        {
            logger.LogError(
                "{Symbol} The target window closed while this command was waiting for the desktop — refusing to {Action}.",
                UiSymbols.Error, action);
            UiJsonError.Emit(json, UiJsonError.CodeStaleElement,
                $"The target window no longer exists — refusing to {action}. Re-resolve the target and retry.",
                errorOut: errorOut,
                recoveryHint: "Another workflow may have closed the window while this command waited for the desktop. Re-run the discovery step (ui list-windows / ui search) and retry.");
            return false;
        }

        if (expectedProcessId > 0 && actualProcessId != (uint)expectedProcessId)
        {
            logger.LogError(
                "{Symbol} The target window handle now belongs to a different process — refusing to {Action}.",
                UiSymbols.Error, action);
            UiJsonError.Emit(json, UiJsonError.CodeStaleElement,
                $"The target window handle now belongs to a different process — refusing to {action}. Re-resolve the target and retry.",
                errorOut: errorOut,
                recoveryHint: "The original window exited while this command waited for the desktop and Windows reused its handle. Re-run the discovery step and retry.");
            return false;
        }

        return true;
    }
}
