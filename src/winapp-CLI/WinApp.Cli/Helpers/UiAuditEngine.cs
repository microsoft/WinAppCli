// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Helpers.UiAudit;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Pure accessibility + contrast rule engine for <c>ui audit</c>. Operates on a flat list of
/// inspected <see cref="UiElement"/>s and emits findings. No UIA/capture dependencies, so it is
/// fully unit-testable with fabricated elements. Contrast ratios are supplied by the caller via a
/// provider delegate (computed from captured pixels) to keep pixel sampling out of the engine.
/// </summary>
internal static class UiAuditEngine
{
    public const string CheckNames = "names";
    public const string CheckKeyboard = "keyboard";
    public const string CheckRoles = "roles";
    public const string CheckScreenReader = "screen-reader";
    public const string CheckTabOrder = "tab-order";
    public const string CheckContrast = "contrast";

    public const string SeverityFail = "fail";
    public const string SeverityWarn = "warn";

    // Root-cause tags used to de-duplicate the SAME underlying defect when it is surfaced by more
    // than one area (e.g. a missing accessible name is reported by names, keyboard, and
    // screen-reader). The orchestrator collapses cross-area findings that share (Selector, RootCause).
    public const string RootCauseMissingName = "missing-name";
    public const string RootCauseNotFocusable = "not-focusable";
    public const string RootCauseUnclearRole = "unclear-role";

    public static readonly IReadOnlyList<string> AllChecks =
        [CheckNames, CheckKeyboard, CheckRoles, CheckScreenReader, CheckTabOrder, CheckContrast];

    /// <summary>Options controlling which rules run and their thresholds.</summary>
    internal sealed class Options
    {
        public required HashSet<string> Checks { get; init; }
        public string Profile { get; init; } = AuditProfile.Basic;

        /// <summary>WCAG contrast threshold for normal-size text.</summary>
        public double NormalContrast { get; init; } = 4.5;

        /// <summary>WCAG contrast threshold for large text (>= ~24px, or ~18.66px bold).</summary>
        public double LargeContrast { get; init; } = 3.0;

        /// <summary>Informational: "AA" or "AAA".</summary>
        public string WcagLevel { get; init; } = "AA";
    }

    // ControlTypes conventionally considered interactive (mirrors UiInspectCommand's allowlist).
    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "CheckBox", "ComboBox", "Edit", "TextBox", "Hyperlink",
        "ListItem", "MenuItem", "RadioButton", "Tab", "TabItem", "SplitButton",
        "TreeItem", "DataItem", "Slider"
    };

    // ControlTypes that carry visible text worth contrast-checking.
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "Document", "Hyperlink"
    };

    // Non-client / chrome ControlTypes that are structurally part of the window frame or scroll
    // machinery rather than app content. Suppressed from name/keyboard/screen-reader rules.
    private static readonly HashSet<string> ChromeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScrollBar", "Thumb", "TitleBar"
    };

    // System-provided accessible names for scrollbar increment/decrement parts. These are
    // framework-generated, unambiguous, and effectively never real user-facing control names, so
    // flagging them is noise. Only unambiguous part names are listed — caption buttons and the
    // ambiguous bare part names (e.g. "Close", "Page Down") are intentionally left out and covered
    // structurally by ChromeTypes (ScrollBar/Thumb) + TitleBar ancestry instead. This is only a
    // fallback for parts exposed as generic Buttons.
    private static readonly HashSet<string> ChromePartNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Vertical Small Increase", "Vertical Small Decrease",
        "Vertical Large Increase", "Vertical Large Decrease",
        "Horizontal Small Increase", "Horizontal Small Decrease",
        "Horizontal Large Increase", "Horizontal Large Decrease",
    };

    private const string TitleBarType = "TitleBar";

    /// <summary>Large-text px threshold (WCAG large text ≈ 18pt ≈ 24px).</summary>
    private const double LargeTextHeightPx = 24.0;

    public static bool IsInteractive(UiElement el)
        => el.IsInvokable || InteractiveTypes.Contains(el.Type);

    /// <summary>
    /// True when the element is non-client window chrome (title-bar / caption buttons) or scroll-bar
    /// part machinery, which the accessibility rules should not flag: these are framework-owned
    /// affordances, keyboard-reachable via the scrollbar/window as a whole, not app content. Detected
    /// primarily by ControlType (ScrollBar/Thumb/TitleBar) and TitleBar ancestry, with a fallback on
    /// system-generated part names.
    /// </summary>
    public static bool IsNonClientChrome(UiElement el)
    {
        if (ChromeTypes.Contains(el.Type))
        {
            return true;
        }

        if (el.AncestorPath is { } path)
        {
            foreach (var ancestor in path)
            {
                if (string.Equals(ancestor, TitleBarType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(el.Name) && ChromePartNames.Contains(el.Name.Trim()))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Run the enabled rules over <paramref name="elements"/>. <paramref name="contrastProvider"/>
    /// returns the measured contrast ratio for a text element, or <c>null</c> when it could not be
    /// measured (in which case the contrast rule is skipped for that element).
    /// </summary>
    public static UiAuditResult Run(
        IReadOnlyList<UiElement> elements,
        Options options,
        Func<UiElement, double?>? contrastProvider = null)
    {
        var issues = new List<UiAuditIssue>();
        var pass = 0;

        foreach (var el in elements)
        {
            // Skip window separator sentinels emitted by the inspect walk.
            if (el.Type == "---")
            {
                continue;
            }

            var interactive = IsInteractive(el);
            var visible = !el.IsOffscreen;

            // Non-client window chrome (title bar / caption buttons) and scrollbar part machinery
            // are framework-owned affordances, not app content. Suppress them from the
            // name/keyboard/screen-reader rules to avoid false positives.
            var chrome = IsNonClientChrome(el);

            // names: interactive/focusable elements must have a non-empty accessible name.
            if (options.Checks.Contains(CheckNames) && visible && !chrome && (interactive || el.IsKeyboardFocusable))
            {
                if (string.IsNullOrWhiteSpace(el.Name))
                {
                    issues.Add(Issue(CheckNames, SeverityFail, el,
                        $"{Describe(el)} is interactive but has no accessible name (Name is empty). Set an accessible label (e.g. AutomationProperties.Name / aria-label).",
                        RootCauseMissingName));
                }
                else
                {
                    pass++;
                }

                if (!string.IsNullOrWhiteSpace(el.Name))
                {
                    if (!string.IsNullOrWhiteSpace(el.AutomationId)
                        && string.Equals(el.Name, el.AutomationId, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Issue(CheckNames, SeverityWarn, el,
                            $"{Describe(el)} uses its AutomationId as its accessible name. Provide a user-facing label instead of a control identifier."));
                    }
                    else if (!string.IsNullOrWhiteSpace(el.ClassName)
                        && string.Equals(el.Name, el.ClassName, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Issue(CheckNames, SeverityWarn, el,
                            $"{Describe(el)} uses its class name as its accessible name. Provide a user-facing label instead of an implementation detail."));
                    }
                    else if (options.Profile == AuditProfile.Thorough
                        && string.Equals(el.Name, el.Type, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(Issue(CheckNames, SeverityWarn, el,
                            $"{Describe(el)} uses its control type as its accessible name. Users need a meaningful name, not just the role."));
                    }
                }
            }

            // keyboard: interactive, enabled, visible elements should be keyboard-focusable.
            if (options.Checks.Contains(CheckKeyboard) && !chrome && interactive && el.IsEnabled && visible)
            {
                if (!el.IsKeyboardFocusable)
                {
                    issues.Add(Issue(CheckKeyboard, SeverityWarn, el,
                        $"{Describe(el)} is interactive but not keyboard-focusable. Keyboard-only users cannot reach it.",
                        RootCauseNotFocusable));
                }
                else
                {
                    pass++;
                }
            }

            if (options.Checks.Contains(CheckKeyboard) && !chrome)
            {
                if (el.IsKeyboardFocusable && visible && string.IsNullOrWhiteSpace(el.Name))
                {
                    issues.Add(Issue(CheckKeyboard, SeverityFail, el,
                        $"{Describe(el)} is keyboard-focusable but has no accessible name. Keyboard and assistive-tech users will land on an unnamed stop.",
                        RootCauseMissingName));
                }

                if (el.IsKeyboardFocusable && !el.IsEnabled && visible)
                {
                    issues.Add(Issue(CheckKeyboard, SeverityWarn, el,
                        $"{Describe(el)} is disabled but still keyboard-focusable. Disabled controls should usually be skipped by tab navigation."));
                }

                if (el.IsKeyboardFocusable && el.IsOffscreen)
                {
                    issues.Add(Issue(CheckKeyboard, SeverityWarn, el,
                        $"{Describe(el)} can receive keyboard focus while offscreen. Users may lose track of focus location."));
                }
            }

            // roles: actionable elements should expose a sensible ControlType (not Custom/Unknown).
            if (options.Checks.Contains(CheckRoles) && el.IsInvokable && visible)
            {
                if (HasUnknownRole(el.Type))
                {
                    issues.Add(Issue(CheckRoles, SeverityWarn, el,
                        $"{Describe(el)} is actionable but exposes an unclear ControlType ('{el.Type}'). Assistive tech may misannounce it — assign a proper control type/role.",
                        RootCauseUnclearRole));
                }
                else
                {
                    pass++;
                }

                if (el.Type.Equals("Pane", StringComparison.OrdinalIgnoreCase)
                    || el.Type.Equals("Group", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Issue(CheckRoles, SeverityWarn, el,
                        $"{Describe(el)} is actionable but exposes a container role ('{el.Type}'). Screen readers may not announce it as an actionable control."));
                }
                else if (options.Profile == AuditProfile.Thorough
                    && (el.Type.Equals("Text", StringComparison.OrdinalIgnoreCase)
                        || el.Type.Equals("Image", StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(Issue(CheckRoles, SeverityWarn, el,
                        $"{Describe(el)} is actionable but exposes a presentational role ('{el.Type}'). Consider a clearer actionable control type."));
                }
            }

            if (options.Checks.Contains(CheckScreenReader) && !chrome)
            {
                if ((interactive || el.IsKeyboardFocusable) && visible && string.IsNullOrWhiteSpace(el.Name))
                {
                    issues.Add(Issue(CheckScreenReader, SeverityFail, el,
                        $"{Describe(el)} is reachable by assistive technology but has no accessible name. A screen reader would announce little or nothing useful.",
                        RootCauseMissingName));
                }

                if (interactive && el.IsEnabled && visible && !el.IsKeyboardFocusable)
                {
                    issues.Add(Issue(CheckScreenReader, SeverityWarn, el,
                        $"{Describe(el)} is actionable but not keyboard-focusable. Screen-reader users navigating by focus may not be able to reach it.",
                        RootCauseNotFocusable));
                }

                if (interactive && visible && HasUnknownRole(el.Type))
                {
                    issues.Add(Issue(CheckScreenReader, SeverityWarn, el,
                        $"{Describe(el)} exposes an unclear role ('{el.Type}'). Screen readers may announce it ambiguously.",
                        RootCauseUnclearRole));
                }

                if (options.Profile == AuditProfile.Thorough
                    && el.IsKeyboardFocusable
                    && !string.IsNullOrWhiteSpace(el.Name)
                    && string.Equals(el.Name, el.Type, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Issue(CheckScreenReader, SeverityWarn, el,
                        $"{Describe(el)} has a low-value accessible name that duplicates its role. Screen-reader users need a descriptive label."));
                }
            }

            // contrast: text elements must meet the WCAG ratio for their size.
            if (options.Checks.Contains(CheckContrast) && visible && contrastProvider is not null && IsTextElement(el))
            {
                var ratio = contrastProvider(el);
                if (ratio is { } r)
                {
                    var isLarge = el.Height >= LargeTextHeightPx;
                    var threshold = isLarge ? options.LargeContrast : options.NormalContrast;
                    if (r + 0.05 < threshold) // small epsilon so exact-threshold passes
                    {
                        issues.Add(Issue(CheckContrast, SeverityFail, el,
                            $"{Describe(el)} contrast ratio {r:0.00}:1 is below the WCAG {options.WcagLevel} threshold {threshold:0.0}:1 for {(isLarge ? "large" : "normal")} text."));
                    }
                    else
                    {
                        pass++;
                    }
                }
            }
        }

        // tab-order: coherence heuristic over the focusable elements in walk order.
        if (options.Checks.Contains(CheckTabOrder))
        {
            EvaluateTabOrder(elements, issues, ref pass);
        }

        var warn = issues.Count(i => i.Severity == SeverityWarn);
        var fail = issues.Count(i => i.Severity == SeverityFail);

        return new UiAuditResult
        {
            Summary = new UiAuditSummary { Pass = pass, Warn = warn, Fail = fail },
            Issues = issues.ToArray(),
        };
    }

    /// <summary>
    /// Simple tab-order coherence heuristic: focusable, visible elements are expected to progress
    /// roughly top-to-bottom, left-to-right in walk order. Report large backward jumps (an element
    /// whose top is well above the previous focusable element's top) as anomalies.
    /// </summary>
    private static void EvaluateTabOrder(IReadOnlyList<UiElement> elements, List<UiAuditIssue> issues, ref int pass)
    {
        const double backwardTolerancePx = 24.0;

        UiElement? prev = null;
        foreach (var el in elements)
        {
            if (el.Type == "---" || el.IsOffscreen || !el.IsKeyboardFocusable)
            {
                continue;
            }

            if (prev is not null)
            {
                var movedToNewRow = el.Y > prev.Y + backwardTolerancePx;
                var sameRow = Math.Abs(el.Y - prev.Y) <= backwardTolerancePx;
                var backwardOnRow = sameRow && el.X + backwardTolerancePx < prev.X;
                var jumpedUp = el.Y + backwardTolerancePx < prev.Y;

                if (!movedToNewRow && (backwardOnRow || jumpedUp))
                {
                    issues.Add(Issue(CheckTabOrder, SeverityWarn, el,
                        $"{Describe(el)} appears out of tab order — it sits above/left of the previous focusable element, so keyboard focus may jump unexpectedly."));
                }
                else
                {
                    pass++;
                }
            }

            prev = el;
        }
    }

    private static bool IsTextElement(UiElement el)
        => TextTypes.Contains(el.Type) && !string.IsNullOrWhiteSpace(el.Name) && el.Width > 0 && el.Height > 0;

    private static bool HasUnknownRole(string type)
        => string.IsNullOrWhiteSpace(type)
        || type.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Custom", StringComparison.OrdinalIgnoreCase);

    private static UiAuditIssue Issue(string ruleId, string severity, UiElement el, string message, string? rootCause = null)
        => new()
        {
            RuleId = ruleId,
            Severity = severity,
            Selector = el.Selector ?? el.Id,
            Name = el.Name,
            Message = message,
            RootCause = rootCause,
        };

    private static string Describe(UiElement el)
    {
        var label = !string.IsNullOrWhiteSpace(el.Name) ? $" \"{el.Name}\""
            : !string.IsNullOrWhiteSpace(el.AutomationId) ? $" #{el.AutomationId}"
            : "";
        return $"{el.Type}{label}";
    }
}
