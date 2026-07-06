// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// Coordinates the per-area <see cref="IUiAuditAreaEngine"/>s: runs each selected area independently
/// and merges their findings into a single <see cref="UiAuditResult"/> (summing summaries, preserving
/// area order). Engines are supplied via DI, so registering a new engine automatically extends the
/// set of runnable areas without changing this class.
/// </summary>
internal sealed class UiAuditOrchestrator
{
    private readonly Dictionary<string, IUiAuditAreaEngine> _engines;

    public UiAuditOrchestrator(IEnumerable<IUiAuditAreaEngine> engines)
    {
        _engines = engines.ToDictionary(e => e.Area, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Areas that have a registered engine.</summary>
    public IReadOnlyCollection<string> AvailableAreas => _engines.Keys;

    /// <summary>Whether any of <paramref name="areas"/> needs a contrast pixel capture.</summary>
    public bool AnyRequiresContrastCapture(IEnumerable<string> areas)
        => areas.Any(a => _engines.TryGetValue(a, out var e) && e.RequiresContrastCapture);

    /// <summary>
    /// Run the given <paramref name="areas"/> in order and merge their findings. Unknown area
    /// names are skipped (the command validates selections up front).
    /// </summary>
    public UiAuditResult Run(IReadOnlyList<string> areas, UiAuditContext context)
    {
        var issues = new List<UiAuditIssue>();
        var pass = 0;

        foreach (var area in areas)
        {
            if (!_engines.TryGetValue(area, out var engine))
            {
                continue;
            }

            var result = engine.Evaluate(context);
            issues.AddRange(result.Issues);
            pass += result.Summary.Pass;
        }

        // Cross-area de-duplication: when several areas surface the SAME underlying defect for the
        // same element (e.g. a missing accessible name reported by names + keyboard + screen-reader),
        // keep only the first — preserving area order so the most canonical finding wins — instead of
        // triple-counting it and inflating the fail total / CI exit semantics. Findings without a
        // shared root cause (or without a selector to correlate on) are always preserved, so
        // single-area runs stay fully meaningful.
        var deduped = Deduplicate(issues);

        var warn = deduped.Count(i => i.Severity == UiAuditEngine.SeverityWarn);
        var fail = deduped.Count(i => i.Severity == UiAuditEngine.SeverityFail);

        return new UiAuditResult
        {
            Summary = new UiAuditSummary { Pass = pass, Warn = warn, Fail = fail },
            Issues = deduped.ToArray(),
        };
    }

    /// <summary>
    /// Collapse findings that share the same (Selector, RootCause) — the same defect reported by
    /// more than one area — keeping the first occurrence. Findings with no RootCause or no Selector
    /// are always kept.
    /// </summary>
    private static List<UiAuditIssue> Deduplicate(List<UiAuditIssue> issues)
    {
        var seen = new HashSet<(string Selector, string RootCause)>();
        var deduped = new List<UiAuditIssue>(issues.Count);
        foreach (var issue in issues)
        {
            if (!string.IsNullOrEmpty(issue.RootCause) && !string.IsNullOrEmpty(issue.Selector))
            {
                if (!seen.Add((issue.Selector!, issue.RootCause!)))
                {
                    continue;
                }
            }
            deduped.Add(issue);
        }
        return deduped;
    }
}
