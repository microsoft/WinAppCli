using WinApp.DevTools.Provenance.Census;

namespace WinApp.DevTools.Provenance.Tests;

/// <summary>Tests for <see cref="Gate1Evaluator"/> — the §5 kill-criteria decision logic.</summary>
[TestClass]
public sealed class Gate1EvaluatorTests
{
    [TestMethod]
    public void Meeting_both_floors_with_no_false_confident_is_GO()
    {
        CensusResult census = TestData.Result(
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 10,
                templatedResolved: 5, generatedTotal: 10, falseConfident: 0));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Go, report.Verdict);
        Assert.IsTrue(report.IsPassing);
        Assert.AreEqual("release", report.EvaluatedConfigLabel);
    }

    [TestMethod]
    public void Source_backed_below_floor_is_CONDITIONAL()
    {
        CensusResult census = TestData.Result(
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 5,   // 50% < 70%
                templatedResolved: 5, generatedTotal: 10, falseConfident: 0));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Conditional, report.Verdict);
        Assert.IsFalse(report.IsPassing);
    }

    [TestMethod]
    public void Templated_below_floor_is_CONDITIONAL()
    {
        CensusResult census = TestData.Result(
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 10,
                templatedResolved: 3, generatedTotal: 10, falseConfident: 0));  // 30% < 40%

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Conditional, report.Verdict);
    }

    [TestMethod]
    public void Any_false_confident_is_an_automatic_KILL()
    {
        CensusResult census = TestData.Result(
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 10,
                templatedResolved: 9, generatedTotal: 10, falseConfident: 1));  // otherwise perfect

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Kill, report.Verdict);
        Assert.AreEqual(1, report.FalseConfidentTotal);
    }

    [TestMethod]
    public void False_confident_in_any_config_kills_even_if_release_is_clean()
    {
        CensusResult census = TestData.Result(
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 10,
                templatedResolved: 9, generatedTotal: 10, falseConfident: 0),
            TestData.Config("release-nolineinfo", releaseFamily: true, strips: true,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 0,
                templatedResolved: 9, generatedTotal: 10, falseConfident: 2));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Kill, report.Verdict);
        Assert.AreEqual(2, report.FalseConfidentTotal);
    }

    [TestMethod]
    public void Zero_source_backed_resolution_in_release_is_a_KILL()
    {
        CensusResult census = TestData.Result(
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 0,   // select-to-source broken
                templatedResolved: 9, generatedTotal: 10, falseConfident: 0));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Kill, report.Verdict);
    }

    [TestMethod]
    public void No_release_family_config_is_INCONCLUSIVE()
    {
        CensusResult census = TestData.Result(
            TestData.Config("debug", releaseFamily: false, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 10,
                templatedResolved: 9, generatedTotal: 10, falseConfident: 0));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Inconclusive, report.Verdict);
    }

    [TestMethod]
    public void The_stripped_probe_is_never_the_arbiter_release_is_preferred()
    {
        // Only difference: release passes, release-nolineinfo would fail. The evaluator must judge release.
        CensusResult census = TestData.Result(
            TestData.Config("release-nolineinfo", releaseFamily: true, strips: true,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 0,
                templatedResolved: 5, generatedTotal: 10, falseConfident: 0),
            TestData.Config("release", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 10,
                templatedResolved: 5, generatedTotal: 10, falseConfident: 0));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Go, report.Verdict);
        Assert.AreEqual("release", report.EvaluatedConfigLabel);
    }

    [TestMethod]
    public void Packaged_is_judged_when_no_plain_release_is_present()
    {
        CensusResult census = TestData.Result(
            TestData.Config("packaged", releaseFamily: true, strips: false,
                total: 100, sourceBackedTotal: 10, sourceBackedResolved: 8,
                templatedResolved: 5, generatedTotal: 10, falseConfident: 0));

        Gate1Report report = Gate1Evaluator.Evaluate(census);

        Assert.AreEqual(Gate1Verdict.Go, report.Verdict);
        Assert.AreEqual("packaged", report.EvaluatedConfigLabel);
    }
}
