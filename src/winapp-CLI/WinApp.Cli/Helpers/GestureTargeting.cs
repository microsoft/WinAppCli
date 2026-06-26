// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

/// <summary>Outcome of resolving a stable on-screen target for a coordinate gesture.</summary>
internal enum TargetStatus
{
    /// <summary>The element settled; <see cref="StableTarget.Element"/> holds its current bounds.</summary>
    Ok,

    /// <summary>The element could no longer be resolved — it was removed or re-templated mid-gesture.</summary>
    NotFound,

    /// <summary>The element resolved but now has zero width or height.</summary>
    ZeroSize,

    /// <summary>The element kept moving/resizing and never settled within the budget.</summary>
    Moving,
}

/// <summary>A resolved gesture target plus its current center.</summary>
internal readonly record struct StableTarget(TargetStatus Status, UiElement Element, int CenterX, int CenterY);

/// <summary>
/// Re-resolves a coordinate gesture's target element immediately before injection so click / drag /
/// scroll --wheel hit where the element actually is *now*, not a rectangle that went stale while the
/// window was being foregrounded. If the element is animating (a moving/resizing target), the gesture
/// would otherwise land on empty space yet still report success — so we confirm the bounds have
/// stopped changing and surface a <see cref="TargetStatus.Moving"/> result instead of a false ✅.
/// </summary>
internal static class GestureTargeting
{
    /// <summary>Per-axis tolerance (px) within which two reads count as "the same position".</summary>
    private const int StabilityTolerancePx = 2;

    /// <summary>Default extra reads after the initial resolve before giving up as "still moving".</summary>
    public const int DefaultMaxReads = 3;

    /// <summary>Default pause between stability reads.</summary>
    public const int DefaultReadDelayMs = 40;

    /// <summary>
    /// Re-reads <paramref name="selector"/> until two consecutive bounding rects agree within
    /// <see cref="StabilityTolerancePx"/> (target settled) or the read budget is exhausted (still
    /// moving). <paramref name="initial"/> is the bounds the caller already resolved before
    /// foregrounding and seeds the comparison, so a static element settles after a single confirming
    /// read.
    /// </summary>
    public static async Task<StableTarget> ResolveStableAsync(
        IUiAutomationService uiAutomation,
        UiSessionInfo session,
        SelectorExpression selector,
        UiElement initial,
        int maxReads,
        int readDelayMs,
        Func<int, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        delay ??= (ms, ct) => Task.Delay(ms, ct);

        var previous = initial;
        for (int read = 0; read < maxReads; read++)
        {
            await delay(readDelayMs, cancellationToken);

            var current = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
            if (current is null)
            {
                return new StableTarget(TargetStatus.NotFound, initial, 0, 0);
            }

            if (current.Width == 0 || current.Height == 0)
            {
                return new StableTarget(TargetStatus.ZeroSize, current, 0, 0);
            }

            if (Settled(previous, current))
            {
                return Ok(current);
            }

            previous = current;
        }

        // Never settled within the budget — the element is still animating. Report the latest known
        // bounds so the caller can log where it last was, but flag the gesture as not delivered.
        return new StableTarget(TargetStatus.Moving, previous, Center(previous).X, Center(previous).Y);
    }

    private static StableTarget Ok(UiElement element)
    {
        var (cx, cy) = Center(element);
        return new StableTarget(TargetStatus.Ok, element, cx, cy);
    }

    /// <summary>
    /// Performs a single confirming read of <paramref name="selector"/> and checks it still occupies the
    /// bounds the caller already settled on (<paramref name="expected"/>). Used immediately before a
    /// button-down — after the cursor-settle delay — to close the residual race where a continuously
    /// animating target drifts during the settle window <see cref="ResolveStableAsync"/> couldn't see,
    /// so a reported success no longer hides a silent miss. Returns the element's current bounds on
    /// success, or a <see cref="TargetStatus.Moving"/> / <see cref="TargetStatus.NotFound"/> /
    /// <see cref="TargetStatus.ZeroSize"/> result (feed to <see cref="TryReport"/>) when it shifted,
    /// vanished, or collapsed in that final window.
    /// </summary>
    public static async Task<StableTarget> ConfirmStillAsync(
        IUiAutomationService uiAutomation,
        UiSessionInfo session,
        SelectorExpression selector,
        UiElement expected,
        CancellationToken cancellationToken)
    {
        var current = await uiAutomation.FindSingleElementAsync(session, selector, cancellationToken);
        if (current is null)
        {
            return new StableTarget(TargetStatus.NotFound, expected, 0, 0);
        }

        if (current.Width == 0 || current.Height == 0)
        {
            return new StableTarget(TargetStatus.ZeroSize, current, 0, 0);
        }

        if (!Settled(expected, current))
        {
            var (mx, my) = Center(current);
            return new StableTarget(TargetStatus.Moving, current, mx, my);
        }

        return Ok(current);
    }

    private static (int X, int Y) Center(UiElement element)
        => ((int)(element.X + element.Width / 2.0), (int)(element.Y + element.Height / 2.0));

    private static bool Settled(UiElement a, UiElement b)
        => Math.Abs(a.X - b.X) <= StabilityTolerancePx
        && Math.Abs(a.Y - b.Y) <= StabilityTolerancePx
        && Math.Abs(a.Width - b.Width) <= StabilityTolerancePx
        && Math.Abs(a.Height - b.Height) <= StabilityTolerancePx;

    /// <summary>
    /// Returns <see langword="true"/> when the target settled (proceed with the gesture). Otherwise logs
    /// and emits the matching JSON error (<c>target_moved</c> for a vanished/animating element,
    /// <c>zero_size_element</c> for a collapsed one) and returns <see langword="false"/> so the caller
    /// aborts with a non-zero exit — converting the old "silent miss reported as success" into a clear,
    /// machine-readable signal.
    /// </summary>
    public static bool TryReport(StableTarget target, ILogger logger, bool json, string selector, string action)
    {
        switch (target.Status)
        {
            case TargetStatus.Ok:
                return true;

            case TargetStatus.ZeroSize:
                logger.LogError("{Symbol} Element collapsed to zero size before the {Action} — gesture not sent.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeZeroSize,
                    $"Element collapsed to zero size before the {action} — gesture not sent.", selector);
                return false;

            case TargetStatus.NotFound:
                logger.LogError(
                    "{Symbol} Element could not be re-resolved just before the {Action} — it moved or was removed. Gesture not sent (it would have hit empty space). Retry, or use a UIA-pattern verb (invoke/set-value/scroll --direction) for changing UI.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeTargetMoved,
                    $"Element could not be re-resolved just before the {action} — it moved or was removed; gesture not sent.", selector);
                return false;

            case TargetStatus.Moving:
            default:
                logger.LogError(
                    "{Symbol} Element is still moving/resizing — {Action} not sent to avoid hitting empty space (the target never settled). Retry once the UI is static, or use a UIA-pattern verb (invoke/set-value/scroll --direction) which doesn't depend on screen coordinates.",
                    UiSymbols.Error, action);
                UiJsonError.Emit(json, UiJsonError.CodeTargetMoved,
                    $"Element is still moving/resizing — {action} not sent because the target never settled. Retry once static, or use a UIA-pattern verb.", selector);
                return false;
        }
    }
}
