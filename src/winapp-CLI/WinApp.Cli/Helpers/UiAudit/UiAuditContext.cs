// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// Immutable inputs shared by every <see cref="IUiAuditAreaEngine"/> for one audit run. Keeping
/// this as a single context object lets area engines stay independent and lets the orchestrator
/// add new inputs (e.g. a time budget) without changing engine signatures.
/// </summary>
internal sealed class UiAuditContext
{
    /// <summary>Flat, walk-ordered element list produced by the inspect walk.</summary>
    public required IReadOnlyList<UiElement> Elements { get; init; }

    /// <summary>Selected profile (see <see cref="AuditProfile"/>). Controls per-area rule depth.</summary>
    public required string Profile { get; init; }

    /// <summary>WCAG contrast threshold for normal-size text.</summary>
    public double NormalContrast { get; init; } = 4.5;

    /// <summary>WCAG contrast threshold for large text.</summary>
    public double LargeContrast { get; init; } = 3.0;

    /// <summary>Informational WCAG level ("AA"/"AAA") echoed into messages.</summary>
    public string WcagLevel { get; init; } = "AA";

    /// <summary>
    /// Measured contrast ratio provider (from captured pixels), or <c>null</c> when capture was
    /// unavailable. Only areas that set <see cref="IUiAuditAreaEngine.RequiresContrastCapture"/>
    /// consult it.
    /// </summary>
    public Func<UiElement, double?>? ContrastProvider { get; init; }

    // TODO(time-budget): Carry an optional per-run/per-area time budget here so dynamic areas
    // (screen-reader, events) can bound how long they interact with the live UI. Plumbed as a
    // nullable so today's synchronous engines can ignore it.
    public TimeSpan? TimeBudget { get; init; }
}
