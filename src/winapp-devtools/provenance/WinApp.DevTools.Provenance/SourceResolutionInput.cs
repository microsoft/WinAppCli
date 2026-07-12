namespace WinApp.DevTools.Provenance;

/// <summary>
/// Normalized input to <see cref="SourceProvenanceGrader"/> for a single live element. Populated
/// from a <see cref="TapElement"/> for the census path; the optional enrichment signals model
/// what a live daemon can additionally supply on the future per-element <c>Source.resolve</c> path
/// (a bare census TSV leaves them unset).
/// </summary>
public sealed record SourceResolutionInput
{
    /// <summary>The element's <c>x:Name</c>, if any.</summary>
    public string? Name { get; init; }

    /// <summary>Runtime type name, if known.</summary>
    public string? TypeName { get; init; }

    /// <summary>Resolved source file URI, or empty/null when unresolved.</summary>
    public string? File { get; init; }

    /// <summary>Resolved 1-based line, or <c>0</c> when unknown.</summary>
    public int Line { get; init; }

    /// <summary>Resolved 1-based column, or <c>0</c> when unknown.</summary>
    public int Column { get; init; }

    /// <summary>The build config context (drives stripped-line-info reason codes).</summary>
    public CensusConfig Config { get; init; } = CensusConfig.Debug;

    /// <summary>
    /// Enrichment: multiple candidate spans. When more than one is present the element is graded
    /// <see cref="SourceKind.Ambiguous"/> and all candidates are reported — never a coin-flip.
    /// </summary>
    public IReadOnlyList<SourceSpan>? CandidateSpans { get; init; }

    /// <summary>
    /// Enrichment: an explicit origin hint from the live daemon for a kind a bare TSV cannot
    /// express (e.g. <see cref="SourceKind.BindingGenerated"/>, <see cref="SourceKind.ResourceOrigin"/>,
    /// <see cref="SourceKind.RuntimeOnly"/>, <see cref="SourceKind.Unreachable"/>).
    /// </summary>
    public SourceKind? KindHint { get; init; }

    /// <summary>Enrichment: whether the element is still reachable in the live tree.</summary>
    public bool TargetReachable { get; init; } = true;

    /// <summary>Builds an input from a raw census row under the given config.</summary>
    public static SourceResolutionInput FromTap(TapElement element, CensusConfig config) => new()
    {
        Name = element.Name,
        TypeName = element.Type,
        File = element.File,
        Line = element.Line,
        Column = element.Column,
        Config = config,
    };
}
