namespace WinApp.DevTools.Provenance.Census;

/// <summary>
/// Grades every raw census row and aggregates the results by build config and <see cref="SourceKind"/>,
/// producing the per-config rates the Gate-1 evaluator judges. The false-confident tally is an
/// <em>independent audit</em>: it re-checks each confident grade against the row's own file rather
/// than trusting the grade, so a future grader regression that mislabels a framework/runtime element
/// as confident is caught here and trips Gate-1.
/// </summary>
public sealed class CensusAggregator
{
    private readonly ISourceProvenanceGrader _grader;

    /// <summary>Creates an aggregator using the given grader (defaults to <see cref="SourceProvenanceGrader"/>).</summary>
    public CensusAggregator(ISourceProvenanceGrader? grader = null) =>
        _grader = grader ?? new SourceProvenanceGrader();

    /// <summary>Aggregates a set of parsed census TSVs into a <see cref="CensusResult"/>.</summary>
    public CensusResult Aggregate(IReadOnlyList<CensusTsvFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<ConfigCensus> configs = [];
        List<PageCoverage> pages = [];
        List<string> configOrder = [];
        Dictionary<string, List<CensusTsvFile>> byConfig = new(StringComparer.OrdinalIgnoreCase);
        List<string> pageOrder = [];
        HashSet<string> seenPages = new(StringComparer.OrdinalIgnoreCase);

        foreach (CensusTsvFile file in files)
        {
            string label = file.Config.Label;
            if (!byConfig.TryGetValue(label, out List<CensusTsvFile>? list))
            {
                list = [];
                byConfig[label] = list;
                configOrder.Add(label);
            }

            list.Add(file);
            pages.Add(new PageCoverage(file.Config, file.Page, file.FileName, file.Elements.Count, file.Elements.Count > 0));
            if (file.Page.Length > 0 && seenPages.Add(file.Page))
            {
                pageOrder.Add(file.Page);
            }
        }

        foreach (string label in configOrder)
        {
            configs.Add(AggregateConfig(byConfig[label]));
        }

        return new CensusResult
        {
            Configs = configs,
            Pages = pages,
            PageLabels = pageOrder,
        };
    }

    private ConfigCensus AggregateConfig(List<CensusTsvFile> configFiles)
    {
        CensusConfig config = configFiles[0].Config;
        Dictionary<SourceKind, int> counts = [];
        int total = 0, sourceBackedTotal = 0, sourceBackedResolved = 0;
        int templatedResolved = 0, generatedTotal = 0, falseConfident = 0;

        foreach (CensusTsvFile file in configFiles)
        {
            foreach (TapElement element in file.Elements)
            {
                SourceResolutionInput input = SourceResolutionInput.FromTap(element, config);
                GradedSource graded = _grader.Grade(input);

                total++;
                counts[graded.SourceKind] = counts.GetValueOrDefault(graded.SourceKind) + 1;

                if (graded.SourceKind == SourceKind.SourceBacked)
                {
                    sourceBackedTotal++;
                    if (graded.Confidence == Confidence.Exact)
                    {
                        sourceBackedResolved++;
                    }
                }

                if (IsGenerated(graded.SourceKind))
                {
                    generatedTotal++;
                    if (graded.HasTarget)
                    {
                        templatedResolved++;
                    }
                }

                if (IsFalseConfident(element, graded))
                {
                    falseConfident++;
                }
            }
        }

        return new ConfigCensus
        {
            Config = config,
            Total = total,
            CountsByKind = counts,
            SourceBackedTotal = sourceBackedTotal,
            SourceBackedResolved = sourceBackedResolved,
            TemplatedResolved = templatedResolved,
            GeneratedTotal = generatedTotal,
            FalseConfident = falseConfident,
        };
    }

    /// <summary>Template/style/binding/runtime origins — the "not directly authored" generated population.</summary>
    private static bool IsGenerated(SourceKind kind) => kind is
        SourceKind.TemplateGenerated or
        SourceKind.StyleGenerated or
        SourceKind.BindingGenerated or
        SourceKind.RuntimeOnly;

    /// <summary>
    /// Independent false-confident audit: a grade is false-confident when it claims
    /// <c>exact</c>/<c>high</c> confidence yet the underlying row does not point at the app's own
    /// authored markup. This does not trust the grader's own <see cref="SourceKind"/>.
    /// </summary>
    public static bool IsFalseConfident(TapElement element, GradedSource graded)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(graded);
        return graded.IsConfident && !SourceFileClassifier.IsAuthoredMarkup(element.File);
    }
}
