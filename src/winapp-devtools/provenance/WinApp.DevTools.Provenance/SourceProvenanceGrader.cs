namespace WinApp.DevTools.Provenance;

/// <summary>
/// Grades a live element's source provenance, assigning a <see cref="SourceKind"/>, a
/// <see cref="Confidence"/>, and (when imperfect) a <see cref="ReasonCode"/> — the honesty model
/// of provenance spec §4.
/// </summary>
/// <remarks>
/// <para>The prime directive is the <b>false-confident prohibition</b>: the grader never returns
/// <see cref="Confidence.Exact"/> or <see cref="Confidence.High"/> unless the element is truly
/// authored inline in the developer's own page/UserControl markup <b>and</b> a source line is
/// present. Framework/template/style/runtime origins are graded <see cref="Confidence.Low"/> or
/// <see cref="Confidence.None"/> and mapped (at best) to a template/style definition — never
/// presented as the user's page. This makes the 0%-false-confident Gate-1 invariant hold by
/// construction.</para>
/// <para>The grader is pure and deterministic (no live-tree or Windows dependency) so it runs on
/// hosted CI and is unit-tested against fixtures.</para>
/// </remarks>
public sealed class SourceProvenanceGrader : ISourceProvenanceGrader
{
    private readonly ProvenanceGradingOptions _options;

    /// <summary>Creates a grader with the given policy (defaults to <see cref="ProvenanceGradingOptions.Default"/>).</summary>
    public SourceProvenanceGrader(ProvenanceGradingOptions? options = null) =>
        _options = options ?? ProvenanceGradingOptions.Default;

    /// <inheritdoc />
    public GradedSource Grade(SourceResolutionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Ambiguity is honest by construction: multiple candidates => low, all reported.
        if (input.CandidateSpans is { Count: > 1 } candidates)
        {
            SourceSpan first = candidates[0];
            return new GradedSource
            {
                SourceKind = SourceKind.Ambiguous,
                Confidence = Confidence.Low,
                Candidates = candidates,
                Uri = first.Uri,
                Line = first.Line > 0 ? first.Line : null,
                Column = first.Column > 0 ? first.Column : null,
            };
        }

        // 2. Explicit origin hints the live daemon can supply but a bare census row cannot.
        if (!input.TargetReachable)
        {
            return Bare(SourceKind.Unreachable, Confidence.None, ReasonCode.SourceInfoMissing);
        }

        if (input.KindHint is SourceKind hint)
        {
            switch (hint)
            {
                case SourceKind.Unreachable:
                    return Bare(SourceKind.Unreachable, Confidence.None, ReasonCode.SourceInfoMissing);
                case SourceKind.RuntimeOnly:
                    return Bare(SourceKind.RuntimeOnly, Confidence.None, ReasonCode.SourceInfoMissing);
                case SourceKind.BindingGenerated:
                    // A binding-materialized element may know its template def; low at best, never exact.
                    return Targeted(SourceKind.BindingGenerated, Confidence.Low, ReasonCode.SourceInfoMissing, input);
                case SourceKind.ResourceOrigin:
                    return Targeted(SourceKind.ResourceOrigin, Confidence.Low, reason: null, input);
                default:
                    // source-backed / template-generated / style-generated / ambiguous hints fall
                    // through to the file-based derivation below, which classifies them honestly.
                    break;
            }
        }

        // 3. Derive from file provenance.
        string file = (input.File ?? string.Empty).Trim();

        // No markup provenance at all -> runtime-only.
        if (file.Length == 0)
        {
            return Bare(SourceKind.RuntimeOnly, Confidence.None, ReasonCode.SourceInfoMissing);
        }

        // Framework template/style instantiation -> mapped to the framework template/style def,
        // low confidence, never the user's page.
        if (SourceFileClassifier.IsFramework(file))
        {
            return TemplateOrStyle(file, input);
        }

        // The app's own template/style resource dictionary (generic.xaml / themeresources.xaml):
        // an authored template/style definition is still a generated origin -> low, mapped to the def.
        if (_options.TreatAuthoredTemplateDictionariesAsGenerated && SourceFileClassifier.IsTemplateOrStyleDictionary(file))
        {
            return TemplateOrStyle(file, input);
        }

        // Directly-authored page / UserControl markup.
        if (input.Line > 0)
        {
            return new GradedSource
            {
                SourceKind = SourceKind.SourceBacked,
                Confidence = Confidence.Exact,
                Uri = file,
                Line = input.Line,
                Column = input.Column > 0 ? input.Column : null,
            };
        }

        // Authored file but no line: honest 'none' (we can name the file, not the span) with the
        // reason that distinguishes a stripped-line build from genuinely missing info.
        ReasonCode why = input.Config.StripsLineInfo ? ReasonCode.ReleaseNoLineInfo : ReasonCode.SourceInfoMissing;
        return new GradedSource
        {
            SourceKind = SourceKind.SourceBacked,
            Confidence = Confidence.None,
            ReasonCode = why,
            Uri = file,
        };
    }

    private static GradedSource TemplateOrStyle(string file, SourceResolutionInput input)
    {
        SourceKind kind = SourceFileClassifier.IsThemeResource(file) ? SourceKind.StyleGenerated : SourceKind.TemplateGenerated;
        return new GradedSource
        {
            SourceKind = kind,
            Confidence = Confidence.Low,
            ReasonCode = ReasonCode.TemplateGenerated,
            Uri = file,
            Line = input.Line > 0 ? input.Line : null,
            Column = input.Column > 0 ? input.Column : null,
        };
    }

    private static GradedSource Bare(SourceKind kind, Confidence confidence, ReasonCode? reason) => new()
    {
        SourceKind = kind,
        Confidence = confidence,
        ReasonCode = reason,
    };

    private static GradedSource Targeted(SourceKind kind, Confidence confidence, ReasonCode? reason, SourceResolutionInput input)
    {
        string file = (input.File ?? string.Empty).Trim();
        return new GradedSource
        {
            SourceKind = kind,
            Confidence = confidence,
            ReasonCode = reason,
            Uri = file.Length > 0 ? file : null,
            Line = file.Length > 0 && input.Line > 0 ? input.Line : null,
            Column = file.Length > 0 && input.Column > 0 ? input.Column : null,
        };
    }
}
