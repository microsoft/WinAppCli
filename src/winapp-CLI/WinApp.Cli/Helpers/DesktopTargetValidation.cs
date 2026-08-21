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

        if (expectedProcessId > 0
            && actualProcessId != (uint)expectedProcessId
            && !IsOwnedByExpectedProcess(systemQuery, hwnd, expectedProcessId))
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

    /// <summary>
    /// Maximum owner links to follow. Owner chains are short in practice (dialog → owner window); the
    /// cap bounds the walk against a cycle produced by a torn or hostile window tree.
    /// </summary>
    private const int MaxOwnerChainDepth = 8;

    /// <summary>
    /// Whether <paramref name="hwnd"/> is a live window <em>owned</em> by a window belonging to
    /// <paramref name="expectedProcessId"/>, following the <c>GW_OWNER</c> chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plain PID equality check is too strict: <c>UiAutomationService.GetAllAppWindows</c> deliberately
    /// discovers cross-process windows that the target app <em>owns</em> — common-item file pickers and
    /// system dialogs run in another process yet are genuinely part of the app's UI, and elements found
    /// on them are tagged with that foreign HWND. Rejecting those made every such target unreachable
    /// after a queue wait.
    /// </para>
    /// <para>
    /// This mirrors the exact association the discovery side uses (<c>GW_OWNER</c> reaching one of the
    /// session's windows), so nothing is admitted here that discovery would not have surfaced. The
    /// recycled-handle protection is preserved: a reused HWND belonging to an unrelated process has no
    /// owner chain reaching the expected PID, and an owner link that leads to a dead or reused window
    /// fails the liveness check on that link — <see cref="ISystemUiQuery.GetProcessIdForWindow"/>
    /// returns 0 for a destroyed window and the true current PID for a recycled one.
    /// </para>
    /// </remarks>
    private static bool IsOwnedByExpectedProcess(ISystemUiQuery systemQuery, long hwnd, int expectedProcessId)
    {
        var current = hwnd;
        var seen = new HashSet<long>();

        for (var depth = 0; depth < MaxOwnerChainDepth; depth++)
        {
            if (!seen.Add(current))
            {
                // A cycle cannot reach the expected process by any further step.
                return false;
            }

            var owner = (long)systemQuery.GetWindowOwner(current);
            if (owner == 0)
            {
                // Top of the chain: an unowned window that is not in the expected process is either a
                // recycled handle or an unrelated window. Either way it must not be acted upon.
                return false;
            }

            // The owner must still be alive AND still belong to the expected process. Checking the PID
            // of the owner (rather than merely that one exists) is what keeps a recycled owner handle
            // from laundering an unrelated window into the expected session.
            if (systemQuery.GetProcessIdForWindow(owner) == (uint)expectedProcessId)
            {
                return true;
            }

            current = owner;
        }

        return false;
    }
}
