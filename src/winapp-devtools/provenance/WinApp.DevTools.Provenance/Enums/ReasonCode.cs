namespace WinApp.DevTools.Provenance;

/// <summary>
/// Machine-readable reason a mapping is imperfect — surfaced as API, not free log text.
/// </summary>
/// <remarks>
/// Mirrors the normative <c>Diagnostics.ReasonCode</c> enumeration in the WDXP protocol schema
/// (<c>specs/winapp-devtools-protocol.md</c> §6.5 and Appendix B). The full set is mirrored for
/// fidelity; provenance grading emits the source-related subset
/// (<see cref="SourceInfoMissing"/>, <see cref="TemplateGenerated"/>,
/// <see cref="ReleaseNoLineInfo"/>). The protocol schema is the source of truth; see
/// <see cref="SourceKind"/> remarks for the coordination rule.
/// </remarks>
public enum ReasonCode
{
    /// <summary>Wire: <c>parse-error</c>.</summary>
    ParseError,

    /// <summary>Wire: <c>binding-failure</c>.</summary>
    BindingFailure,

    /// <summary>Wire: <c>apply-failed</c>.</summary>
    ApplyFailed,

    /// <summary>Source line-info is absent for this element. Wire: <c>source-info-missing</c>.</summary>
    SourceInfoMissing,

    /// <summary>The element was produced by a template/style, not authored inline. Wire: <c>template-generated</c>.</summary>
    TemplateGenerated,

    /// <summary>Wire: <c>unreachable-popup</c>.</summary>
    UnreachablePopup,

    /// <summary>A Release/optimized build stripped XAML line-info. Wire: <c>release-no-line-info</c>.</summary>
    ReleaseNoLineInfo,

    /// <summary>Wire: <c>unsafe-refused</c>.</summary>
    UnsafeRefused,
}
