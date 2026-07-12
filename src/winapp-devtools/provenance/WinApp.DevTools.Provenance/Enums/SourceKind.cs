namespace WinApp.DevTools.Provenance;

/// <summary>
/// What <em>kind</em> of origin a live element has.
/// </summary>
/// <remarks>
/// This mirrors the normative <c>Source.SourceKind</c> enumeration defined by the WDXP
/// protocol schema (see <c>specs/winapp-devtools-protocol.md</c> §6.3 and Appendix B).
/// The protocol schema (<c>wdxp.v0.json</c>, owned by the protocol workstream) is the single
/// source of truth; when its generator emits C# types, this local mirror should be replaced by
/// them. Do <b>not</b> add, remove, or rename members here — request any change from the
/// protocol workstream as a <c>[schema-change] Source</c> PR. Wire (kebab-case) strings are
/// produced by <see cref="ProvenanceWire"/>.
/// </remarks>
public enum SourceKind
{
    /// <summary>Written directly in the developer's own markup (a page / UserControl). Wire: <c>source-backed</c>.</summary>
    SourceBacked,

    /// <summary>Instantiated from a control template (generic.xaml). Wire: <c>template-generated</c>.</summary>
    TemplateGenerated,

    /// <summary>Produced by a style / theme resource dictionary. Wire: <c>style-generated</c>.</summary>
    StyleGenerated,

    /// <summary>Materialized by a binding (e.g. an ItemTemplate instance). Wire: <c>binding-generated</c>.</summary>
    BindingGenerated,

    /// <summary>Created in code with no markup provenance. Wire: <c>runtime-only</c>.</summary>
    RuntimeOnly,

    /// <summary>Originates from a resource lookup. Wire: <c>resource-origin</c>.</summary>
    ResourceOrigin,

    /// <summary>Multiple candidate source spans; reported with all candidates, never a coin-flip. Wire: <c>ambiguous</c>.</summary>
    Ambiguous,

    /// <summary>The element is not reachable / the target was lost. Wire: <c>unreachable</c>.</summary>
    Unreachable,
}
