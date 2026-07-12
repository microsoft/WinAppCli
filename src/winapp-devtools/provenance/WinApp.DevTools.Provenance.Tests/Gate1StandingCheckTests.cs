using WinApp.DevTools.Provenance.Census;

namespace WinApp.DevTools.Provenance.Tests;

/// <summary>
/// The <b>standing Gate-1 check</b>: the kill-criteria (provenance spec §5) encoded as an always-on
/// test over the committed reference corpus. If a future change to the grader or aggregator regresses
/// source resolution or — critically — produces a single false-confident answer, this test fails and
/// the pipeline stops. It is the CI-runnable half of the census that needs no desktop.
/// </summary>
[TestClass]
public sealed class Gate1StandingCheckTests
{
    private static CensusResult _census = null!;
    private static Gate1Report _gate = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        IReadOnlyList<CensusTsvFile> files = CensusTsvReader.ReadDirectory(Fixtures.CensusDir);
        _census = new CensusAggregator().Aggregate(files);
        _gate = Gate1Evaluator.Evaluate(_census);
    }

    [TestMethod]
    public void Reference_corpus_is_complete()
    {
        Assert.AreEqual(Fixtures.ExpectedTsvCount, _census.Pages.Count);
        Assert.IsTrue(_census.Configs.Count >= 3, "expect debug, release and release-nolineinfo");
    }

    [TestMethod]
    public void Gate1_verdict_is_GO()
    {
        Assert.AreEqual(Gate1Verdict.Go, _gate.Verdict, string.Join(" ", _gate.Reasons));
        Assert.AreEqual("release", _gate.EvaluatedConfigLabel);
    }

    [TestMethod]
    public void Zero_false_confident_across_the_whole_corpus()
    {
        Assert.AreEqual(0, _gate.FalseConfidentTotal);
        foreach (ConfigCensus c in _census.Configs)
        {
            Assert.AreEqual(0, c.FalseConfident, $"config '{c.Config.Label}'");
        }
    }

    [TestMethod]
    public void Release_meets_the_source_backed_floor()
    {
        ConfigCensus release = _census.ForLabel("release")!;
        Assert.IsTrue(
            release.SourceBackedResolvedPct >= Gate1Evaluator.SourceBackedFloorPct,
            $"source-backed resolved {release.SourceBackedResolvedPct}% must be >= {Gate1Evaluator.SourceBackedFloorPct}%");
    }

    [TestMethod]
    public void Release_meets_the_templated_floor()
    {
        ConfigCensus release = _census.ForLabel("release")!;
        Assert.IsTrue(
            release.TemplatedToTemplatePct >= Gate1Evaluator.TemplatedFloorPct,
            $"templated-to-template {release.TemplatedToTemplatePct}% must be >= {Gate1Evaluator.TemplatedFloorPct}%");
    }

    [TestMethod]
    public void Report_renders_without_forbidden_labels()
    {
        string md = CensusReport.ToMarkdown(_census, _gate);
        string json = CensusReport.ToJson(_census, _gate);

        Assert.IsTrue(md.Contains("Source-resolution census", StringComparison.Ordinal));
        Assert.IsFalse(md.Contains("RT1", StringComparison.Ordinal), "scrubbed reports must not carry internal probe labels");
        Assert.IsTrue(json.Contains("\"verdict\": \"Go\"", StringComparison.Ordinal));
    }
}
