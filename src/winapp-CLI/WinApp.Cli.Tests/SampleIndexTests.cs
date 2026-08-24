// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services.Controls;

namespace WinApp.Cli.Tests;

/// <summary>
/// Phase 0 harness for the shared sample-index contract
/// (<c>docs/winui-sample-index.schema.json</c>) behind
/// <see href="https://github.com/microsoft/winappCli/issues/703">#703</see>.
///
/// <para>These tests exist to answer one question before we ask WinUI-Gallery or
/// CommunityToolkit to publish anything: <b>is the schema sufficient?</b> If a scenario
/// can't survive a round trip through the index, the contract is missing a field — and the
/// cheapest possible time to learn that is now, not after an upstream maintainer has
/// reviewed and merged it.</para>
///
/// <para>Hermetic: no network. The Reactor-shaped fixture mirrors the real published
/// <c>reactor-search-index.json</c>, including the top-level and control-level fields it
/// carries that this contract does not model.</para>
/// </summary>
[TestClass]
public class SampleIndexTests
{
    private static readonly string[] ButtonKeywords = ["click", "press me", "command bar"];
    private static readonly string[] ButtonRelated = ["HyperlinkButton", "ToggleButton"];
    private static readonly string[] ButtonXmlns = ["xmlns:controls=\"using:CommunityToolkit.WinUI.Controls\""];

    /// <summary>
    /// A control whose scenarios populate every field the contract models, including the
    /// two that only XAML sources use (<c>xaml</c>, <c>xmlnsImports</c>) and the two
    /// descriptions that Gallery keeps separately (<c>description</c>/<c>details</c>).
    /// </summary>
    private static Scenario[] FullyPopulatedScenarios() =>
    [
        new Scenario
        {
            Id = "button-1",
            ControlId = "button",
            ControlName = "Button",
            HeaderText = "A simple button",
            Xaml = "<Button Content=\"Click me\" />",
            CSharp = "var button = new Button();",
            Source = "gallery",
            NuGetPackage = "CommunityToolkit.WinUI.Controls.Primitives",
            XmlnsImports = ButtonXmlns,
            Description = "A long-form description of Button, the kind ControlInfoData.json carries.",
            ControlDescription = "A control that responds to user input.",
            RelatedControls = ButtonRelated,
            ApiNamespace = "Microsoft.UI.Xaml.Controls",
            Docs =
            [
                new DocLink { Title = "Button class", Uri = "https://learn.microsoft.com/button" },
                new DocLink { Title = "Buttons guidance", Uri = "https://learn.microsoft.com/buttons" },
            ],
        },
        new Scenario
        {
            Id = "button-2",
            ControlId = "button",
            ControlName = "Button",
            HeaderText = "XAML-only sample",
            Xaml = "<Button Content=\"No code-behind\" />",
            CSharp = null,
            Source = "gallery",
            NuGetPackage = "CommunityToolkit.WinUI.Controls.Primitives",
            XmlnsImports = ButtonXmlns,
            Description = "A long-form description of Button, the kind ControlInfoData.json carries.",
            ControlDescription = "A control that responds to user input.",
            RelatedControls = ButtonRelated,
            ApiNamespace = "Microsoft.UI.Xaml.Controls",
            Docs =
            [
                new DocLink { Title = "Button class", Uri = "https://learn.microsoft.com/button" },
                new DocLink { Title = "Buttons guidance", Uri = "https://learn.microsoft.com/buttons" },
            ],
        },
    ];

    /// <summary>
    /// The heart of Phase 0: every field of every scenario survives
    /// <see cref="SampleIndexWriter.Write"/> → <see cref="SampleIndexParser.Parse"/>
    /// unchanged. A failure here means the published schema cannot carry our corpus.
    /// </summary>
    [TestMethod]
    public void RoundTrip_PreservesEveryScenarioField()
    {
        var original = FullyPopulatedScenarios();
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["button"] = ButtonKeywords };

        var json = SampleIndexWriter.Write(original, tags, "gallery");
        var (roundTripped, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(original.Length, roundTripped.Length, "every scenario must survive the round trip");

        for (int i = 0; i < original.Length; i++)
        {
            var (before, after) = (original[i], roundTripped[i]);
            Assert.AreEqual(before.Id, after.Id, "Id");
            Assert.AreEqual(before.ControlId, after.ControlId, "ControlId");
            Assert.AreEqual(before.ControlName, after.ControlName, "ControlName");
            Assert.AreEqual(before.HeaderText, after.HeaderText, "HeaderText");
            Assert.AreEqual(before.Xaml, after.Xaml, "Xaml");
            Assert.AreEqual(before.CSharp, after.CSharp, "CSharp");
            Assert.AreEqual(before.Source, after.Source, "Source");
            Assert.AreEqual(before.NuGetPackage, after.NuGetPackage, "NuGetPackage");
            Assert.AreEqual(before.Description, after.Description, "Description (long-form)");
            Assert.AreEqual(before.ControlDescription, after.ControlDescription, "ControlDescription (one-line)");
            Assert.AreEqual(before.ApiNamespace, after.ApiNamespace, "ApiNamespace");
            CollectionAssert.AreEqual(before.RelatedControls, after.RelatedControls, "RelatedControls");
            CollectionAssert.AreEqual(before.XmlnsImports, after.XmlnsImports, "XmlnsImports");

            Assert.AreEqual(before.Docs.Length, after.Docs.Length, "Docs length");
            for (int d = 0; d < before.Docs.Length; d++)
            {
                Assert.AreEqual(before.Docs[d].Title, after.Docs[d].Title, "Docs.Title");
                Assert.AreEqual(before.Docs[d].Uri, after.Docs[d].Uri, "Docs.Uri");
            }
        }
    }

    /// <summary>Curated keywords are carried by the index, so a source that publishes one
    /// also retires our hand-maintained tag files.</summary>
    [TestMethod]
    public void RoundTrip_PreservesCuratedTags()
    {
        var tags = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["button"] = ButtonKeywords };

        var json = SampleIndexWriter.Write(FullyPopulatedScenarios(), tags, "gallery");
        var (_, roundTripped) = SampleIndexParser.Parse(json, "gallery");

        CollectionAssert.AreEqual(ButtonKeywords, roundTripped["button"]);
    }

    /// <summary>
    /// Content that came back through the index must still pass the corpus boundary guard.
    /// This is the miniature of the Phase 3 assertion that the malformed-XAML drop count
    /// goes to zero: an index round trip must not itself corrupt markup or code.
    /// </summary>
    [TestMethod]
    public void RoundTrip_OutputSurvivesTheScenarioSanitizer()
    {
        var json = SampleIndexWriter.Write(FullyPopulatedScenarios(), source: "gallery");
        var (scenarios, _) = SampleIndexParser.Parse(json, "gallery");

        ScenarioSanitizer.SanitizeAll(scenarios);

        Assert.IsTrue(scenarios.All(s => s.Xaml is not null), "well-formed XAML must not be dropped");
        Assert.IsNotNull(scenarios[0].CSharp, "brace-balanced C# must not be dropped");
    }

    /// <summary>Regenerating against unchanged data must produce an identical file, or the
    /// drift check upstream would report noise instead of change.</summary>
    [TestMethod]
    public void Write_IsDeterministicAndOrdersControlsById()
    {
        Scenario[] unordered =
        [
            new Scenario { Id = "zebra-1", ControlId = "zebra", ControlName = "Zebra", CSharp = "new Zebra();", Source = "gallery" },
            new Scenario { Id = "alpha-1", ControlId = "alpha", ControlName = "Alpha", CSharp = "new Alpha();", Source = "gallery" },
        ];

        var first = SampleIndexWriter.Write(unordered, source: "gallery");
        var second = SampleIndexWriter.Write(unordered, source: "gallery");

        Assert.AreEqual(first, second, "the same input must produce a byte-identical index");
        Assert.IsTrue(
            first.IndexOf("alpha", StringComparison.Ordinal) < first.IndexOf("zebra", StringComparison.Ordinal),
            "controls must be written in id order regardless of input order");
    }

    /// <summary>No timestamp by default — see <see cref="SampleIndexWriter.Write"/>.</summary>
    [TestMethod]
    public void Write_OmitsGeneratedTimestampUnlessRequested()
    {
        var withoutStamp = SampleIndexWriter.Write(FullyPopulatedScenarios(), source: "gallery");
        StringAssert.DoesNotMatch(withoutStamp, new System.Text.RegularExpressions.Regex("generatedAtUtc"));

        var withStamp = SampleIndexWriter.Write(
            FullyPopulatedScenarios(), source: "gallery", generatedAtUtc: DateTimeOffset.UnixEpoch);
        StringAssert.Contains(withStamp, "generatedAtUtc");
    }

    /// <summary>XAML-only samples are the common case for Gallery and Toolkit, and the shape
    /// Reactor's parser never had to handle.</summary>
    [TestMethod]
    public void Parse_KeepsXamlOnlySamples_AndSkipsEmptyOnes()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            {
              "id": "button",
              "samples": [
                { "header": "xaml only", "xaml": "<Button />" },
                { "header": "nothing at all" },
                { "header": "blank strings", "xaml": "  ", "code": "  " },
                { "header": "code only", "code": "new Button();" }
              ]
            }
          ]
        }
        """;

        var (scenarios, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(2, scenarios.Length, "samples with neither xaml nor code are skipped");
        Assert.AreEqual("button-1", scenarios[0].Id, "kept ids stay contiguous from 1");
        Assert.AreEqual("<Button />", scenarios[0].Xaml);
        Assert.IsNull(scenarios[0].CSharp, "a xaml-only sample must not gain empty C#");
        Assert.AreEqual("button-2", scenarios[1].Id);
        Assert.IsNull(scenarios[1].Xaml);
    }

    /// <summary>A control-level <c>usings</c> list must not turn a xaml-only sample into a
    /// scenario whose C# is nothing but using directives.</summary>
    [TestMethod]
    public void Parse_DoesNotFoldUsingsIntoASampleWithNoCode()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            {
              "id": "flex",
              "usings": ["Microsoft.UI.Reactor.Flex"],
              "samples": [ { "header": "xaml only", "xaml": "<Flex />" } ]
            }
          ]
        }
        """;

        var (scenarios, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(1, scenarios.Length);
        Assert.IsNull(scenarios[0].CSharp, "usings alone are not a sample");
    }

    /// <summary>
    /// The version gate is the whole point of publishing <c>schemaVersion</c>: a future
    /// document must be refused rather than read with today's field meanings. An absent
    /// version is accepted so the contract stays compatible with indexes that predate it.
    /// </summary>
    [TestMethod]
    public void Parse_RefusesAnUnknownSchemaVersion()
    {
        const string future = """
        { "schemaVersion": 2, "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;
        const string notANumber = """
        { "schemaVersion": "1", "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;
        const string absent = """
        { "controls": [ { "id": "button", "samples": [ { "code": "new Button();" } ] } ] }
        """;

        Assert.AreEqual(0, SampleIndexParser.Parse(future, "gallery").scenarios.Length, "a newer contract is refused");
        Assert.AreEqual(0, SampleIndexParser.Parse(notANumber, "gallery").scenarios.Length, "a non-numeric version is refused");
        Assert.AreEqual(1, SampleIndexParser.Parse(absent, "gallery").scenarios.Length, "an absent version is read as v1");
    }

    /// <summary>The reader runs on untrusted network content, so wrongly-typed array entries
    /// are skipped rather than thrown on.</summary>
    [TestMethod]
    public void Parse_SkipsNonObjectEntriesInsteadOfThrowing()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "controls": [
            "not a control",
            42,
            { "id": "button", "samples": [ "not a sample", { "code": "new Button();" } ] }
          ]
        }
        """;

        var (scenarios, _) = SampleIndexParser.Parse(json, "gallery");

        Assert.AreEqual(1, scenarios.Length);
        Assert.AreEqual("button-1", scenarios[0].Id);
    }

    /// <summary>A source can't label its samples as another source's, because
    /// <c>Source</c> is stamped by the caller and never read from the document.</summary>
    [TestMethod]
    public void Parse_IgnoresTheDocumentsOwnSourceField()
    {
        const string json = """
        { "schemaVersion": 1, "source": "gallery", "controls": [ { "id": "x", "samples": [ { "code": "new X();" } ] } ] }
        """;

        var (scenarios, _) = SampleIndexParser.Parse(json, "reactor");

        Assert.AreEqual("reactor", scenarios[0].Source);
    }

    /// <summary>
    /// Unmodelled fields must not break a reader — the real Reactor index already carries
    /// <c>generatedFrom</c>, <c>category</c> and <c>galleryRoute</c>, and upstream repos will
    /// add their own. This is why the schema sets <c>additionalProperties: true</c>.
    /// </summary>
    [TestMethod]
    public void Parse_ToleratesFieldsTheContractDoesNotModel()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "source": "reactor",
          "generatedFrom": "some/path.csproj",
          "controls": [
            {
              "id": "flex",
              "name": "Flex",
              "category": "Layout",
              "galleryRoute": "/flex",
              "samples": [ { "header": "Basic flex", "language": "csharp", "code": "new Flex()" } ]
            }
          ]
        }
        """;

        var (scenarios, _) = SampleIndexParser.Parse(json, "reactor");

        Assert.AreEqual(1, scenarios.Length);
        Assert.AreEqual("Flex", scenarios[0].ControlName);
    }

    /// <summary>
    /// The published schema, the reader and the writer must describe the same contract.
    /// Without this, a field added to <see cref="SampleIndexSchema"/> would silently not be
    /// in the document we hand upstream — or worse, we'd ask upstream for a field nothing
    /// reads.
    /// </summary>
    [TestMethod]
    public void Schema_DeclaresExactlyTheFieldsTheReaderAndWriterUse()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "winui-sample-index.schema.json");
        Assert.IsTrue(File.Exists(schemaPath), $"schema not found at {schemaPath}");

        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = schema.RootElement;
        var defs = root.GetProperty("$defs");

        AssertPropertiesMatch(SampleIndexSchema.DocumentProperties, root, "document");
        AssertPropertiesMatch(SampleIndexSchema.ControlProperties, defs.GetProperty("control"), "control");
        AssertPropertiesMatch(SampleIndexSchema.DocLinkProperties, defs.GetProperty("docLink"), "docLink");
        AssertPropertiesMatch(SampleIndexSchema.SampleProperties, defs.GetProperty("sample"), "sample");

        Assert.AreEqual(
            SampleIndexSchema.Version,
            root.GetProperty("properties").GetProperty(SampleIndexSchema.SchemaVersion).GetProperty("const").GetInt32(),
            "the schema's pinned schemaVersion must match the version the reader accepts");
    }

    private static void AssertPropertiesMatch(string[] expected, JsonElement schemaNode, string level)
    {
        var declared = schemaNode.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        CollectionAssert.AreEquivalent(
            expected,
            declared,
            $"the {level} properties in docs/winui-sample-index.schema.json and SampleIndexSchema have drifted apart");
    }
}
