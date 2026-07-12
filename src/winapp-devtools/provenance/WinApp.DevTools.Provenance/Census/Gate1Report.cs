namespace WinApp.DevTools.Provenance.Census;

/// <summary>The Gate-1 decision for source mapping (provenance spec §5).</summary>
public enum Gate1Verdict
{
    /// <summary>Not enough data to decide (no non-stripping Release-family config was measured).</summary>
    Inconclusive,

    /// <summary>Proceed: Release meets the source-backed and templated floors with zero false-confident answers.</summary>
    Go,

    /// <summary>Proceed with caution: no false-confident answers, but a Release floor was missed.</summary>
    Conditional,

    /// <summary>Stop: a false-confident answer occurred, or select-to-source fails outright in Release.</summary>
    Kill,
}

/// <summary>The outcome of evaluating the Gate-1 kill-criteria against a census.</summary>
public sealed record Gate1Report
{
    /// <summary>The decision.</summary>
    public required Gate1Verdict Verdict { get; init; }

    /// <summary>Human-readable reasons behind the verdict.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    /// <summary>The config label whose Release rates were judged, when one was found.</summary>
    public string? EvaluatedConfigLabel { get; init; }

    /// <summary>Release source-backed resolution rate that was judged.</summary>
    public double SourceBackedResolvedPct { get; init; }

    /// <summary>Release templated-to-template rate that was judged.</summary>
    public double TemplatedToTemplatePct { get; init; }

    /// <summary>Total false-confident answers across all configs (must be 0 to pass).</summary>
    public int FalseConfidentTotal { get; init; }

    /// <summary>True only when the verdict is <see cref="Gate1Verdict.Go"/>.</summary>
    public bool IsPassing => Verdict == Gate1Verdict.Go;
}
