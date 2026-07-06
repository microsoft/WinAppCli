// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers.UiAudit;

/// <summary>
/// Audit <em>levels</em> select how deeply each area probes and which WCAG contrast thresholds
/// apply: <see cref="Basic"/> runs the fast, essential rules with WCAG AA contrast; <see cref="Thorough"/>
/// adds heuristic / deeper rules and applies WCAG AAA contrast. Exposed via <c>--level</c>, this lets a
/// single <c>--area</c> selection scale from a quick CI gate to a comprehensive sweep without new commands.
/// </summary>
internal static class AuditProfile
{
    /// <summary>Fast, high-signal essential rules (WCAG AA contrast). The default level.</summary>
    public const string Basic = "basic";

    /// <summary>Superset of <see cref="Basic"/> adding heuristic / deeper rules and WCAG AAA contrast.</summary>
    public const string Thorough = "thorough";

    /// <summary>Accepted <c>--level</c> values.</summary>
    public static readonly IReadOnlyList<string> All = [Basic, Thorough];

    /// <summary>Normalize a raw level token; returns <c>null</c> when unrecognized.</summary>
    public static string? Normalize(string? raw)
    {
        var normalized = (raw ?? Basic).Trim().ToLowerInvariant();
        return All.Contains(normalized) ? normalized : null;
    }
}
