namespace WinApp.DevTools.Provenance;

/// <summary>
/// Maps the mirrored honesty enums to/from their normative kebab-case wire strings, matching the
/// WDXP protocol schema values exactly. Keeping this mapping in one place means serialization
/// stays in lockstep with the contract even while these enums are a local mirror (see
/// <see cref="SourceKind"/> remarks).
/// </summary>
public static class ProvenanceWire
{
    /// <summary>Wire string for a <see cref="SourceKind"/>.</summary>
    public static string ToWire(this SourceKind value) => value switch
    {
        SourceKind.SourceBacked => "source-backed",
        SourceKind.TemplateGenerated => "template-generated",
        SourceKind.StyleGenerated => "style-generated",
        SourceKind.BindingGenerated => "binding-generated",
        SourceKind.RuntimeOnly => "runtime-only",
        SourceKind.ResourceOrigin => "resource-origin",
        SourceKind.Ambiguous => "ambiguous",
        SourceKind.Unreachable => "unreachable",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown SourceKind"),
    };

    /// <summary>Wire string for a <see cref="Confidence"/>.</summary>
    public static string ToWire(this Confidence value) => value switch
    {
        Confidence.Exact => "exact",
        Confidence.High => "high",
        Confidence.Low => "low",
        Confidence.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown Confidence"),
    };

    /// <summary>Wire string for a <see cref="ReasonCode"/>.</summary>
    public static string ToWire(this ReasonCode value) => value switch
    {
        ReasonCode.ParseError => "parse-error",
        ReasonCode.BindingFailure => "binding-failure",
        ReasonCode.ApplyFailed => "apply-failed",
        ReasonCode.SourceInfoMissing => "source-info-missing",
        ReasonCode.TemplateGenerated => "template-generated",
        ReasonCode.UnreachablePopup => "unreachable-popup",
        ReasonCode.ReleaseNoLineInfo => "release-no-line-info",
        ReasonCode.UnsafeRefused => "unsafe-refused",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown ReasonCode"),
    };

    /// <summary>Wire string for an optional <see cref="ReasonCode"/> (<c>null</c> when absent).</summary>
    public static string? ToWire(this ReasonCode? value) => value?.ToWire();
}
