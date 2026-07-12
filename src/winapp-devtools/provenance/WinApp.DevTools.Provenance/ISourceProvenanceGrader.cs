namespace WinApp.DevTools.Provenance;

/// <summary>
/// Grades a live element's source provenance into a <see cref="GradedSource"/> — the honesty model
/// of provenance spec §4. Exposed as an interface so clients (the census aggregator, the future
/// <c>Source.resolve</c> command) can depend on the seam and it can be substituted in tests.
/// </summary>
public interface ISourceProvenanceGrader
{
    /// <summary>Grades a single element's provenance.</summary>
    GradedSource Grade(SourceResolutionInput input);
}
