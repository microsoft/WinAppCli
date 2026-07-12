namespace WinApp.DevTools.Provenance.Tests;

/// <summary>
/// Unit tests for <see cref="SourceProvenanceGrader"/> — the honesty model of provenance spec §4.
/// Each test pins one row of the grading decision table; the "false-confident prohibition" tests
/// pin the prime directive that nothing but genuinely authored markup can earn confidence.
/// </summary>
[TestClass]
public sealed class GraderFixtureTests
{
    private static readonly SourceProvenanceGrader Grader = new();

    private static SourceResolutionInput Input(
        string? file = null,
        int line = 0,
        int column = 0,
        string? name = null,
        CensusConfig? config = null,
        SourceKind? kindHint = null,
        bool reachable = true,
        IReadOnlyList<SourceSpan>? candidates = null) => new()
        {
            File = file,
            Line = line,
            Column = column,
            Name = name,
            Config = config ?? CensusConfig.Debug,
            KindHint = kindHint,
            TargetReachable = reachable,
            CandidateSpans = candidates,
        };

    // ---- source-backed (the only kind allowed confidence) ----

    [TestMethod]
    public void SourceBacked_named_with_line_is_exact()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///SmokePage.xaml", line: 44, column: 58, name: "RootPanel"));

        Assert.AreEqual(SourceKind.SourceBacked, g.SourceKind);
        Assert.AreEqual(Confidence.Exact, g.Confidence);
        Assert.IsNull(g.ReasonCode);
        Assert.AreEqual("ms-appx:///SmokePage.xaml", g.Uri);
        Assert.AreEqual(44, g.Line);
        Assert.AreEqual(58, g.Column);
        Assert.IsTrue(g.IsConfident);
    }

    [TestMethod]
    public void SourceBacked_unnamed_with_line_is_exact()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///ItemsPage.xaml", line: 44, column: 58));

        Assert.AreEqual(SourceKind.SourceBacked, g.SourceKind);
        Assert.AreEqual(Confidence.Exact, g.Confidence);
        Assert.AreEqual(44, g.Line);
    }

    [TestMethod]
    public void SourceBacked_stripped_line_in_release_is_none_with_release_reason()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///SmokePage.xaml", line: 0, config: CensusConfig.ReleaseNoLineInfo));

        Assert.AreEqual(SourceKind.SourceBacked, g.SourceKind);
        Assert.AreEqual(Confidence.None, g.Confidence);
        Assert.AreEqual(ReasonCode.ReleaseNoLineInfo, g.ReasonCode);
        Assert.AreEqual("ms-appx:///SmokePage.xaml", g.Uri, "we can still name the file, just not the line");
        Assert.IsNull(g.Line);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    public void SourceBacked_missing_line_in_debug_is_none_with_missing_reason()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///SmokePage.xaml", line: 0, config: CensusConfig.Debug));

        Assert.AreEqual(SourceKind.SourceBacked, g.SourceKind);
        Assert.AreEqual(Confidence.None, g.Confidence);
        Assert.AreEqual(ReasonCode.SourceInfoMissing, g.ReasonCode);
        Assert.IsFalse(g.IsConfident);
    }

    // ---- generated (template / style) ----

    [TestMethod]
    public void Framework_control_template_maps_to_template_never_page()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///Microsoft.UI.Xaml/Themes/generic.xaml", line: 12));

        Assert.AreEqual(SourceKind.TemplateGenerated, g.SourceKind);
        Assert.AreEqual(Confidence.Low, g.Confidence);
        Assert.AreEqual(ReasonCode.TemplateGenerated, g.ReasonCode);
        Assert.AreEqual("ms-appx:///Microsoft.UI.Xaml/Themes/generic.xaml", g.Uri);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    public void Framework_theme_style_is_style_generated_low()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml", line: 0));

        Assert.AreEqual(SourceKind.StyleGenerated, g.SourceKind);
        Assert.AreEqual(Confidence.Low, g.Confidence);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    public void App_authored_generic_dictionary_is_template_generated_low()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///Themes/generic.xaml", line: 30));

        Assert.AreEqual(SourceKind.TemplateGenerated, g.SourceKind);
        Assert.AreEqual(Confidence.Low, g.Confidence);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    public void App_authored_dictionaries_can_be_treated_as_authored_when_policy_disabled()
    {
        SourceProvenanceGrader lenient = new(new ProvenanceGradingOptions
        {
            TreatAuthoredTemplateDictionariesAsGenerated = false,
        });

        GradedSource g = lenient.Grade(Input(file: "ms-appx:///Themes/generic.xaml", line: 30));

        Assert.AreEqual(SourceKind.SourceBacked, g.SourceKind, "with the policy off, an app dictionary is treated as authored markup");
        Assert.AreEqual(Confidence.Exact, g.Confidence);
    }

    // ---- runtime-only ----

    [TestMethod]
    public void Empty_file_is_runtime_only_none()
    {
        GradedSource g = Grader.Grade(Input(file: "", line: 0));

        Assert.AreEqual(SourceKind.RuntimeOnly, g.SourceKind);
        Assert.AreEqual(Confidence.None, g.Confidence);
        Assert.AreEqual(ReasonCode.SourceInfoMissing, g.ReasonCode);
        Assert.IsFalse(g.HasTarget);
        Assert.IsFalse(g.IsConfident);
    }

    // ---- ambiguity ----

    [TestMethod]
    public void Multiple_candidates_are_ambiguous_low_and_report_all()
    {
        List<SourceSpan> candidates =
        [
            new("ms-appx:///A.xaml", 10, 4),
            new("ms-appx:///B.xaml", 20, 8),
        ];

        GradedSource g = Grader.Grade(Input(candidates: candidates));

        Assert.AreEqual(SourceKind.Ambiguous, g.SourceKind);
        Assert.AreEqual(Confidence.Low, g.Confidence);
        Assert.IsNotNull(g.Candidates);
        Assert.AreEqual(2, g.Candidates!.Count);
        Assert.IsFalse(g.IsConfident);
    }

    // ---- enrichment hints (future live path) ----

    [TestMethod]
    public void Binding_hint_is_binding_generated_low()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///ListPage.xaml", line: 5, kindHint: SourceKind.BindingGenerated));

        Assert.AreEqual(SourceKind.BindingGenerated, g.SourceKind);
        Assert.AreEqual(Confidence.Low, g.Confidence);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    public void Resource_hint_is_resource_origin_low()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///Resources.xaml", line: 3, kindHint: SourceKind.ResourceOrigin));

        Assert.AreEqual(SourceKind.ResourceOrigin, g.SourceKind);
        Assert.AreEqual(Confidence.Low, g.Confidence);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    public void Unreachable_target_is_none()
    {
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///Popup.xaml", line: 9, reachable: false));

        Assert.AreEqual(SourceKind.Unreachable, g.SourceKind);
        Assert.AreEqual(Confidence.None, g.Confidence);
        Assert.IsFalse(g.IsConfident);
    }

    // ---- the false-confident prohibition (prime directive) ----

    [TestMethod]
    public void FalseConfident_prohibition_framework_with_stray_line_is_never_confident()
    {
        // A framework element that (wrongly) carries an app-looking line must never be trusted.
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///Microsoft.UI.Xaml/Themes/generic.xaml", line: 42, column: 7));

        Assert.AreNotEqual(Confidence.Exact, g.Confidence);
        Assert.AreNotEqual(Confidence.High, g.Confidence);
        Assert.IsFalse(g.IsConfident);
        Assert.AreNotEqual(SourceKind.SourceBacked, g.SourceKind);
    }

    [TestMethod]
    public void FalseConfident_prohibition_runtime_hint_with_stray_line_is_never_confident()
    {
        // Even with a file+line present, an origin the daemon knows is runtime must not be exact.
        GradedSource g = Grader.Grade(Input(file: "ms-appx:///SmokePage.xaml", line: 44, kindHint: SourceKind.RuntimeOnly));

        Assert.AreEqual(SourceKind.RuntimeOnly, g.SourceKind);
        Assert.AreEqual(Confidence.None, g.Confidence);
        Assert.IsFalse(g.IsConfident);
    }

    [TestMethod]
    [DataRow("ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml", 5)]
    [DataRow("ms-appx:///Microsoft.UI.Xaml/Themes/generic.xaml", 99)]
    [DataRow("ms-appx:///Themes/generic.xaml", 12)]
    [DataRow("", 77)]
    public void No_generated_or_runtime_origin_is_ever_confident(string file, int line)
    {
        GradedSource g = Grader.Grade(Input(file: file, line: line));

        Assert.IsFalse(g.IsConfident, $"'{file}' line {line} must not be graded confident");
    }
}
