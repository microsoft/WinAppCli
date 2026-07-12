namespace WinApp.DevTools.Provenance.Census;

/// <summary>
/// The source-resolution census aggregated for a single build config: counts by
/// <see cref="SourceKind"/> plus the derived Gate-1 rates. Percentages are computed from the raw
/// counts so callers can compare against the Gate-1 thresholds without rounding drift.
/// </summary>
public sealed record ConfigCensus
{
    /// <summary>The build config these numbers describe.</summary>
    public required CensusConfig Config { get; init; }

    /// <summary>Total elements observed across all pages for this config.</summary>
    public required int Total { get; init; }

    /// <summary>Element count for every <see cref="SourceKind"/> (grader-assigned).</summary>
    public required IReadOnlyDictionary<SourceKind, int> CountsByKind { get; init; }

    /// <summary>Elements graded <see cref="SourceKind.SourceBacked"/> (the select-to-source population).</summary>
    public required int SourceBackedTotal { get; init; }

    /// <summary>Source-backed elements that resolved to an exact file+line span.</summary>
    public required int SourceBackedResolved { get; init; }

    /// <summary>Generated elements (template/style/binding) that mapped to a template/style definition.</summary>
    public required int TemplatedResolved { get; init; }

    /// <summary>All generated elements (templated + runtime-only) — the templated-to-template denominator.</summary>
    public required int GeneratedTotal { get; init; }

    /// <summary>
    /// Elements the audit found confidently graded (<c>exact</c>/<c>high</c>) yet not pointing at the
    /// app's own authored markup — i.e. a false-confident answer. MUST stay 0 (Gate-1 kill-criteria).
    /// </summary>
    public required int FalseConfident { get; init; }

    /// <summary>Percent of source-backed elements resolved to an exact span (Gate-1 floor: ≥70% in Release).</summary>
    public double SourceBackedResolvedPct => Percent(SourceBackedResolved, SourceBackedTotal);

    /// <summary>Percent of generated elements mapped to a template/style def (Gate-1 floor: ≥40% in Release).</summary>
    public double TemplatedToTemplatePct => Percent(TemplatedResolved, GeneratedTotal);

    /// <summary>Percent of all elements that were false-confident (Gate-1: must be exactly 0%).</summary>
    public double FalseConfidentPct => Percent(FalseConfident, Total);

    /// <summary>Element count for a given kind (0 when absent).</summary>
    public int Count(SourceKind kind) => CountsByKind.TryGetValue(kind, out int v) ? v : 0;

    private static double Percent(int numerator, int denominator) =>
        denominator == 0 ? 0d : (double)numerator / denominator * 100d;
}

/// <summary>Whether one fixture page produced any elements under one config (per-page coverage).</summary>
/// <param name="Config">The build config.</param>
/// <param name="Page">The fixture page label.</param>
/// <param name="FileName">The source TSV file name.</param>
/// <param name="Elements">Number of elements read.</param>
/// <param name="Ok">True when at least one element was collected.</param>
public sealed record PageCoverage(CensusConfig Config, string Page, string FileName, int Elements, bool Ok);

/// <summary>The full aggregated census across every config and page.</summary>
public sealed record CensusResult
{
    /// <summary>Per-config aggregates, in the order configs were first seen.</summary>
    public required IReadOnlyList<ConfigCensus> Configs { get; init; }

    /// <summary>Per-page-per-config coverage rows (for the raw coverage table).</summary>
    public required IReadOnlyList<PageCoverage> Pages { get; init; }

    /// <summary>Distinct fixture page labels observed.</summary>
    public required IReadOnlyList<string> PageLabels { get; init; }

    /// <summary>When the aggregation was produced.</summary>
    public DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The aggregate for a config label, or <c>null</c> when that config was not measured.</summary>
    public ConfigCensus? ForLabel(string label) =>
        Configs.FirstOrDefault(c => string.Equals(c.Config.Label, label, StringComparison.OrdinalIgnoreCase));
}
