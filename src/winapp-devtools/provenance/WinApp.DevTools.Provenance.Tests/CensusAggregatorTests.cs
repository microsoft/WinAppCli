using WinApp.DevTools.Provenance.Census;

namespace WinApp.DevTools.Provenance.Tests;

/// <summary>
/// Tests for <see cref="CensusAggregator"/>: per-<see cref="SourceKind"/> counting over the real
/// reference corpus, and the independent false-confident audit (which must catch a mislabelled
/// confident grade even when the grader itself is wrong).
/// </summary>
[TestClass]
public sealed class CensusAggregatorTests
{
    private static CensusResult AggregateCorpus()
    {
        IReadOnlyList<CensusTsvFile> files = CensusTsvReader.ReadDirectory(Fixtures.CensusDir);
        return new CensusAggregator().Aggregate(files);
    }

    [TestMethod]
    public void Release_counts_by_source_kind_match_the_baseline()
    {
        ConfigCensus release = AggregateCorpus().ForLabel("release")!;

        Assert.AreEqual(580, release.Total);
        Assert.AreEqual(160, release.Count(SourceKind.SourceBacked));
        Assert.AreEqual(29, release.Count(SourceKind.TemplateGenerated));
        Assert.AreEqual(216, release.Count(SourceKind.StyleGenerated));
        Assert.AreEqual(175, release.Count(SourceKind.RuntimeOnly));
    }

    [TestMethod]
    public void Release_source_backed_fully_resolves_and_templated_is_58pct()
    {
        ConfigCensus release = AggregateCorpus().ForLabel("release")!;

        Assert.AreEqual(160, release.SourceBackedTotal);
        Assert.AreEqual(160, release.SourceBackedResolved);
        Assert.AreEqual(100d, release.SourceBackedResolvedPct, 0.01);

        Assert.AreEqual(245, release.TemplatedResolved);
        Assert.AreEqual(420, release.GeneratedTotal);
        Assert.AreEqual(58.3d, release.TemplatedToTemplatePct, 0.05);

        Assert.AreEqual(0, release.FalseConfident);
    }

    [TestMethod]
    public void ReleaseNoLineInfo_drops_source_backed_resolution_to_zero()
    {
        ConfigCensus stripped = AggregateCorpus().ForLabel("release-nolineinfo")!;

        Assert.AreEqual(160, stripped.SourceBackedTotal);
        Assert.AreEqual(0, stripped.SourceBackedResolved);
        Assert.AreEqual(0d, stripped.SourceBackedResolvedPct, 0.01);
        Assert.AreEqual(0, stripped.FalseConfident, "no line still means no false confidence");
    }

    [TestMethod]
    public void Every_config_has_zero_false_confident()
    {
        foreach (ConfigCensus c in AggregateCorpus().Configs)
        {
            Assert.AreEqual(0, c.FalseConfident, $"config '{c.Config.Label}' must have 0 false-confident");
        }
    }

    [TestMethod]
    public void Audit_catches_a_confident_grade_on_a_framework_row()
    {
        // A broken grader that stamps everything Exact must still be caught by the aggregator's
        // independent audit, because the underlying rows are framework/runtime, not authored markup.
        GradedSource brokenGrade = new()
        {
            SourceKind = SourceKind.SourceBacked,
            Confidence = Confidence.Exact,
            Uri = "ms-appx:///SmokePage.xaml",
            Line = 1,
        };

        CensusAggregator aggregator = new(new StubGrader(brokenGrade));
        IReadOnlyList<CensusTsvFile> files = CensusTsvReader.ReadDirectory(Fixtures.CensusDir);

        CensusResult result = aggregator.Aggregate(files);
        ConfigCensus release = result.ForLabel("release")!;

        // 245 framework + 175 runtime-only rows are not authored markup -> all false-confident.
        Assert.AreEqual(420, release.FalseConfident);
        Assert.IsTrue(release.FalseConfidentPct > 0);
    }

    [TestMethod]
    public void IsFalseConfident_predicate_is_independent_of_the_grade_kind()
    {
        GradedSource confident = new() { SourceKind = SourceKind.SourceBacked, Confidence = Confidence.Exact };
        GradedSource low = new() { SourceKind = SourceKind.TemplateGenerated, Confidence = Confidence.Low };

        // Confident on authored markup: fine.
        Assert.IsFalse(CensusAggregator.IsFalseConfident(Row("ms-appx:///Page.xaml"), confident));
        // Confident on a framework row: false-confident.
        Assert.IsTrue(CensusAggregator.IsFalseConfident(Row("ms-appx:///Microsoft.UI.Xaml/Themes/generic.xaml"), confident));
        // Confident on an empty (runtime) row: false-confident.
        Assert.IsTrue(CensusAggregator.IsFalseConfident(Row(""), confident));
        // Not confident: never false-confident regardless of origin.
        Assert.IsFalse(CensusAggregator.IsFalseConfident(Row(""), low));
    }

    private static TapElement Row(string file) => new(1, "T", "", file, 0, 0);
}
