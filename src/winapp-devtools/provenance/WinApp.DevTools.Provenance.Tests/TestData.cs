using WinApp.DevTools.Provenance.Census;

namespace WinApp.DevTools.Provenance.Tests;

/// <summary>Builders for synthetic census aggregates used by the Gate-1 tests.</summary>
internal static class TestData
{
    public static ConfigCensus Config(
        string label,
        bool releaseFamily,
        bool strips,
        int total,
        int sourceBackedTotal,
        int sourceBackedResolved,
        int templatedResolved,
        int generatedTotal,
        int falseConfident,
        IReadOnlyDictionary<SourceKind, int>? counts = null) => new()
        {
            Config = new CensusConfig(label, strips, releaseFamily),
            Total = total,
            CountsByKind = counts ?? new Dictionary<SourceKind, int>(),
            SourceBackedTotal = sourceBackedTotal,
            SourceBackedResolved = sourceBackedResolved,
            TemplatedResolved = templatedResolved,
            GeneratedTotal = generatedTotal,
            FalseConfident = falseConfident,
        };

    public static CensusResult Result(params ConfigCensus[] configs) => new()
    {
        Configs = configs,
        Pages = [],
        PageLabels = [],
    };
}

/// <summary>A grader stub that returns a fixed grade, used to prove the aggregator's audit is independent.</summary>
internal sealed class StubGrader(GradedSource fixedGrade) : ISourceProvenanceGrader
{
    public GradedSource Grade(SourceResolutionInput input) => fixedGrade;
}
