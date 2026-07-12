namespace WinApp.DevTools.Provenance;

/// <summary>
/// The graded result of resolving a live element to source: the honest answer to
/// "where did this come from, and how sure are we?". This is the shape the <c>Source.resolve</c>
/// command returns and the input the confidence badge (and W5 persist gating) consumes.
/// </summary>
public sealed record GradedSource
{
    /// <summary>The classified origin kind.</summary>
    public required SourceKind SourceKind { get; init; }

    /// <summary>How much to trust the span. Never <see cref="Confidence.Exact"/>/<see cref="Confidence.High"/> unless truly source-backed with a line.</summary>
    public required Confidence Confidence { get; init; }

    /// <summary>Machine-readable reason the mapping is imperfect, when applicable.</summary>
    public ReasonCode? ReasonCode { get; init; }

    /// <summary>Best target document URI (the page, or a template/style definition), when one exists.</summary>
    public string? Uri { get; init; }

    /// <summary>Target 1-based line, when resolved.</summary>
    public int? Line { get; init; }

    /// <summary>Target 1-based column, when resolved.</summary>
    public int? Column { get; init; }

    /// <summary>All candidate spans when <see cref="SourceKind"/> is <see cref="SourceKind.Ambiguous"/>.</summary>
    public IReadOnlyList<SourceSpan>? Candidates { get; init; }

    /// <summary>True when a target document URI was resolved (even without a precise line).</summary>
    public bool HasTarget => !string.IsNullOrEmpty(Uri);

    /// <summary>True when the grade asserts a trustworthy, pinpoint span (<c>exact</c>/<c>high</c>).</summary>
    public bool IsConfident => Confidence is Confidence.Exact or Confidence.High;
}
