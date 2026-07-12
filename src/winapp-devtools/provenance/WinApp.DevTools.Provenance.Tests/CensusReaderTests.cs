using WinApp.DevTools.Provenance.Census;

namespace WinApp.DevTools.Provenance.Tests;

/// <summary>Tests for <see cref="CensusTsvReader"/> — config/page name splitting and row parsing.</summary>
[TestClass]
public sealed class CensusReaderTests
{
    [TestMethod]
    [DataRow("release-nolineinfo-SmokePage", "release-nolineinfo", "SmokePage")]
    [DataRow("release-SmokePage", "release", "SmokePage")]
    [DataRow("debug-UcHost", "debug", "UcHost")]
    [DataRow("packaged-Items", "packaged", "Items")]
    [DataRow("trimmed-XBindFn", "trimmed", "XBindFn")]
    public void SplitConfigAndPage_uses_longest_known_prefix(string stem, string expectedConfig, string expectedPage)
    {
        (CensusConfig config, string page) = CensusTsvReader.SplitConfigAndPage(stem);

        Assert.AreEqual(expectedConfig, config.Label);
        Assert.AreEqual(expectedPage, page);
    }

    [TestMethod]
    public void SplitConfigAndPage_release_nolineinfo_is_not_mistaken_for_release()
    {
        (CensusConfig config, _) = CensusTsvReader.SplitConfigAndPage("release-nolineinfo-Items");

        Assert.AreEqual("release-nolineinfo", config.Label);
        Assert.IsTrue(config.StripsLineInfo, "the stripped probe must keep its strips-line-info flag");
    }

    [TestMethod]
    public void SplitConfigAndPage_unknown_prefix_yields_neutral_config()
    {
        (CensusConfig config, string page) = CensusTsvReader.SplitConfigAndPage("custom-MyPage");

        Assert.AreEqual("custom", config.Label);
        Assert.AreEqual("MyPage", page);
        Assert.IsFalse(config.IsReleaseFamily);
    }

    [TestMethod]
    public void ReadDirectory_reads_the_reference_corpus()
    {
        IReadOnlyList<CensusTsvFile> files = CensusTsvReader.ReadDirectory(Fixtures.CensusDir);

        Assert.AreEqual(Fixtures.ExpectedTsvCount, files.Count);
        Assert.IsTrue(files.All(f => f.Elements.Count > 0), "every corpus TSV should have rows");
    }

    [TestMethod]
    public void ReadFile_parses_rows_and_skips_the_header()
    {
        string path = Path.Combine(Fixtures.CensusDir, "debug-SmokePage.tsv");

        CensusTsvFile file = CensusTsvReader.ReadFile(path);

        Assert.AreEqual("debug", file.Config.Label);
        Assert.AreEqual("SmokePage", file.Page);
        Assert.AreEqual(100, file.Elements.Count, "debug-SmokePage has 100 element rows (header excluded)");
        Assert.IsTrue(file.Elements.All(e => e.Type.Length > 0));
    }
}
