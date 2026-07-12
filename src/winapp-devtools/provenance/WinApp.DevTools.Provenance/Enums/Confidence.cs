namespace WinApp.DevTools.Provenance;

/// <summary>
/// How sure we are of a resolved source span. The prime directive of provenance is to never
/// report a confidence we cannot back up: an uncertain mapping is <see cref="Low"/> or
/// <see cref="None"/>, never <see cref="Exact"/> or <see cref="High"/>.
/// </summary>
/// <remarks>
/// Mirrors the normative <c>Confidence</c> enumeration in the WDXP protocol schema
/// (<c>specs/winapp-devtools-protocol.md</c> Appendix B). The protocol schema is the source of
/// truth; see <see cref="SourceKind"/> remarks for the coordination rule.
/// </remarks>
public enum Confidence
{
    /// <summary>Source-backed element with intact line-info: an exact file+line. Wire: <c>exact</c>.</summary>
    Exact,

    /// <summary>Strongly indicated but not pinpoint-verified. Wire: <c>high</c>.</summary>
    High,

    /// <summary>Best-effort: a template/style definition, or an ambiguous / partial mapping. Wire: <c>low</c>.</summary>
    Low,

    /// <summary>No trustworthy span (runtime-only, or line-info stripped). Wire: <c>none</c>.</summary>
    None,
}
