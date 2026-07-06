// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// Well-known audit <em>areas</em> — the high-level, user-facing grouping of accessibility
/// concerns surfaced by <c>ui audit</c> via <c>--area</c>. Each area is implemented independently
/// by an <see cref="IUiAuditAreaEngine"/> so new areas can be added without touching existing ones.
/// <para>
/// This is the primary extension point for scaling the audit: to add coverage for a new concern,
/// add a constant here, add it to <see cref="Implemented"/>, and register a matching engine in DI.
/// </para>
/// </summary>
internal static class AuditArea
{
    /// <summary>Accessible-name coverage on interactive/focusable elements.</summary>
    public const string Names = "names";

    /// <summary>Keyboard reachability (and, in <see cref="AuditProfile.Thorough"/>, tab-order coherence).</summary>
    public const string Keyboard = "keyboard";

    /// <summary>Screen-reader affordances (static readiness proxy over the UIA tree).</summary>
    public const string ScreenReader = "screen-reader";

    /// <summary>WCAG color-contrast of visible text. Requires a captured-pixel provider.</summary>
    public const string Contrast = "contrast";

    /// <summary>Control-type / role clarity on actionable elements.</summary>
    public const string Roles = "roles";

    /// <summary>
    /// UIA event/interaction behavior. Reserved extension point (see <see cref="UiAudit.EventsAreaEngine"/>
    /// TODO). Not yet user-selectable — its engine is a registered no-op until dynamic event
    /// validation lands, so it is intentionally excluded from <see cref="Implemented"/> and
    /// <see cref="Selectable"/> and <c>--area events</c> is rejected as invalid for now.
    /// </summary>
    public const string Events = "events";

    /// <summary>Meta-selector expanding to every <see cref="Implemented"/> area.</summary>
    public const string All = "all";

    /// <summary>
    /// User-facing areas with a registered, active engine, in stable display order. Extend this
    /// (and DI) when adding a new area engine. Reserved no-op areas (e.g. <see cref="Events"/>) are
    /// intentionally omitted until they produce findings.
    /// </summary>
    public static readonly IReadOnlyList<string> Implemented =
        [Names, Keyboard, ScreenReader, Contrast, Roles];

    /// <summary>Values a user may pass to <c>--area</c> (implemented areas plus <c>all</c>).</summary>
    public static readonly IReadOnlyList<string> Selectable = [.. Implemented, All];

    /// <summary>
    /// Normalize and expand a raw <c>--area</c> selection into a de-duplicated, ordered set of
    /// concrete area names. An empty selection defaults to <see cref="All"/>. <c>all</c> anywhere
    /// expands to every <see cref="Implemented"/> area.
    /// </summary>
    /// <param name="rawAreas">User-supplied area tokens (may be empty).</param>
    /// <param name="invalid">First unrecognized token, if any.</param>
    /// <returns>The resolved ordered area set, or <c>null</c> when a token was invalid.</returns>
    public static IReadOnlyList<string>? Resolve(IReadOnlyList<string> rawAreas, out string? invalid)
    {
        invalid = null;
        if (rawAreas.Count == 0)
        {
            return Implemented;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawAreas)
        {
            var normalized = raw.Trim().ToLowerInvariant();
            if (normalized == All)
            {
                return Implemented;
            }
            if (!Selectable.Contains(normalized))
            {
                invalid = raw;
                return null;
            }
            seen.Add(normalized);
        }

        // Preserve the canonical Implemented ordering for deterministic output.
        return Implemented.Where(seen.Contains).ToArray();
    }
}
