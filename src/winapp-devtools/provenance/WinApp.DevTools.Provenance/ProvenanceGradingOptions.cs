namespace WinApp.DevTools.Provenance;

/// <summary>
/// Policy knobs for <see cref="SourceProvenanceGrader"/>. Defaults encode the provenance-spec
/// baseline; these exist so the open design questions can be tuned without touching the honesty
/// core.
/// </summary>
public sealed record ProvenanceGradingOptions
{
    /// <summary>
    /// When <c>true</c> (baseline for open question <b>Q-TEMPLATE-TARGET</b>), an element resolved
    /// to the app's own template/style resource dictionary (generic.xaml / themeresources.xaml) is
    /// graded as a generated origin (template/style-generated, <see cref="Confidence.Low"/>) mapped
    /// to that definition, rather than as directly authored source. This keeps templated parts from
    /// being presented as the user's page even when the template lives in the app's markup.
    /// </summary>
    public bool TreatAuthoredTemplateDictionariesAsGenerated { get; init; } = true;

    /// <summary>The default options.</summary>
    public static ProvenanceGradingOptions Default { get; } = new();
}
