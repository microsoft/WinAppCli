// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// Base for area engines that are implemented by delegating to the low-level, pure
/// <see cref="UiAuditEngine"/> with a profile-dependent subset of rule checks. This keeps all of
/// today's rule logic in one place while presenting it through the modular area/profile surface.
/// </summary>
internal abstract class CheckBackedAreaEngine : IUiAuditAreaEngine
{
    public abstract string Area { get; }

    public virtual bool RequiresContrastCapture => false;

    /// <summary>
    /// Low-level <see cref="UiAuditEngine"/> checks this area contributes for the given profile.
    /// Return an empty set to make this a reserved (no-op) extension point.
    /// </summary>
    protected abstract IReadOnlyList<string> ResolveChecks(string profile);

    public UiAuditResult Evaluate(UiAuditContext context)
    {
        var checks = ResolveChecks(context.Profile);
        if (checks.Count == 0)
        {
            return EmptyResult;
        }

        var options = new UiAuditEngine.Options
        {
            Checks = new HashSet<string>(checks, StringComparer.OrdinalIgnoreCase),
            Profile = context.Profile,
            NormalContrast = context.NormalContrast,
            LargeContrast = context.LargeContrast,
            WcagLevel = context.WcagLevel,
        };

        return UiAuditEngine.Run(context.Elements, options, context.ContrastProvider);
    }

    protected static UiAuditResult EmptyResult => new()
    {
        Summary = new UiAuditSummary(),
        Issues = [],
    };
}

/// <summary>Accessible-name coverage on interactive/focusable elements.</summary>
internal sealed class NamesAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Names;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckNames];
}

/// <summary>
/// Keyboard reachability. <see cref="AuditProfile.Basic"/> checks focusability; the more expensive
/// tab-order coherence heuristic is added at <see cref="AuditProfile.Thorough"/>.
/// </summary>
internal sealed class KeyboardAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Keyboard;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => profile == AuditProfile.Thorough
            ? [UiAuditEngine.CheckKeyboard, UiAuditEngine.CheckTabOrder]
            : [UiAuditEngine.CheckKeyboard];
}

/// <summary>Control-type / role clarity on actionable elements.</summary>
internal sealed class RolesAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Roles;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckRoles];
}

/// <summary>WCAG color-contrast of visible text (requires captured pixels).</summary>
internal sealed class ContrastAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Contrast;

    public override bool RequiresContrastCapture => true;

    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckContrast];
}

/// <summary>
/// Screen-reader affordances. Reserved extension point: a future engine will drive a screen-reader
/// bridge / UIA text patterns to validate announced content. Returns no findings today.
/// </summary>
internal sealed class ScreenReaderAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.ScreenReader;

    // Static readiness proxy: checks what a screen reader would be able to perceive from the
    // current UIA tree (name, role clarity, focus reachability). Dynamic event/live-region
    // validation stays in the reserved `events` area for a later pass.
    protected override IReadOnlyList<string> ResolveChecks(string profile)
        => [UiAuditEngine.CheckScreenReader];
}

/// <summary>
/// UIA event / interaction behavior. Reserved extension point: a future engine will subscribe to
/// UIA events (focus, invoke, property-changed) while exercising the UI. Returns no findings today.
/// </summary>
internal sealed class EventsAreaEngine : CheckBackedAreaEngine
{
    public override string Area => AuditArea.Events;

    // TODO(events): Subscribe to UIA automation events and assert that interactions raise the
    // expected notifications; needs a time budget (see UiAuditContext.TimeBudget) to bound waits.
    protected override IReadOnlyList<string> ResolveChecks(string profile) => [];
}
