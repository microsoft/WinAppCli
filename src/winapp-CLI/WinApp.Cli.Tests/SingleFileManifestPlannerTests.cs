// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="SingleFileManifestPlanner"/> — the pure mapping from a .NET file-based app's
/// evaluated <c>#:property</c> values onto appxmanifest metadata.
/// </summary>
[TestClass]
public class SingleFileManifestPlannerTests
{
    private static FileInfo SingleFile(string name = "counter.cs") =>
        new(Path.Combine(Path.GetTempPath(), name));

    private static Dictionary<string, string> Props(params (string Name, string Value)[] values)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }
        return dictionary;
    }

    #region Defaults

    [TestMethod]
    public void Plan_NoProperties_DerivesEverythingFromTheFileStem()
    {
        var info = SingleFileManifestPlanner.Plan(SingleFile(), Props(), defaultPublisher: "CN=tester");

        Assert.AreEqual("counter", info.PackageName);
        Assert.AreEqual("counter", info.DisplayName);
        Assert.AreEqual("CN=tester", info.PublisherDN);
        Assert.AreEqual("1.0.0.0", info.Version);
        Assert.AreEqual("counter", info.Description, "description falls back to the display name");
    }

    [TestMethod]
    public void Plan_UndeclaredPropertiesEvaluateToEmpty_AreTreatedAsAbsent()
    {
        // MSBuild returns "" for a property no #:property declared, so the planner must treat an empty
        // string exactly like a missing key rather than emitting Name="" into the manifest.
        var props = Props(
            (SingleFileManifestPlanner.PackageNameProperty, ""),
            (SingleFileManifestPlanner.DisplayNameProperty, "   "),
            (SingleFileManifestPlanner.PublisherProperty, ""),
            (SingleFileManifestPlanner.DescriptionProperty, ""),
            (SingleFileManifestPlanner.VersionProperty, ""),
            ("Version", ""));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props, defaultPublisher: "CN=tester");

        Assert.AreEqual("counter", info.PackageName);
        Assert.AreEqual("counter", info.DisplayName);
        Assert.AreEqual("CN=tester", info.PublisherDN);
        Assert.AreEqual("1.0.0.0", info.Version);
    }

    [TestMethod]
    public void Plan_StemWithInvalidIdentityCharacters_IsSanitizedForIdentityButNotForDisplay()
    {
        // Identity/@Name must match [-.A-Za-z0-9]+, but the display name is free text, so the RAW stem is
        // the better default there.
        var info = SingleFileManifestPlanner.Plan(SingleFile("my tool.cs"), Props(), defaultPublisher: "CN=tester");

        Assert.AreEqual("mytool", info.PackageName);
        Assert.AreEqual("my tool", info.DisplayName);
    }

    #endregion

    #region Declared values

    [TestMethod]
    public void Plan_DeclaredProperties_AreUsedVerbatim()
    {
        var props = Props(
            (SingleFileManifestPlanner.PackageNameProperty, "com.contoso.counter"),
            (SingleFileManifestPlanner.DisplayNameProperty, "Contoso Counter"),
            (SingleFileManifestPlanner.PublisherProperty, "CN=Contoso Ltd"),
            (SingleFileManifestPlanner.VersionProperty, "4.5.6.7"),
            (SingleFileManifestPlanner.DescriptionProperty, "Counts things"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("com.contoso.counter", info.PackageName, "dots are valid in an Identity name");
        Assert.AreEqual("Contoso Counter", info.DisplayName);
        Assert.AreEqual("CN=Contoso Ltd", info.PublisherDN);
        Assert.AreEqual("4.5.6.7", info.Version);
        Assert.AreEqual("Counts things", info.Description);
    }

    [TestMethod]
    public void Plan_BarePublisherName_IsWrappedAsCommonName()
    {
        // Matches what `manifest generate --publisher-name` already does; diverging would be surprising.
        var props = Props((SingleFileManifestPlanner.PublisherProperty, "Contoso Ltd"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("CN=Contoso Ltd", info.PublisherDN);
    }

    [TestMethod]
    public void Plan_DeclaredDisplayName_BecomesTheDescriptionFallback()
    {
        var props = Props((SingleFileManifestPlanner.DisplayNameProperty, "Contoso Counter"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("Contoso Counter", info.Description);
    }

    #endregion

    #region Version normalization

    [TestMethod]
    [DataRow("1.0.0", "1.0.0.0", DisplayName = "MSBuild's three-part default is padded")]
    [DataRow("2.1", "2.1.0.0", DisplayName = "two components are padded")]
    [DataRow("7", "7.0.0.0", DisplayName = "one component is padded")]
    [DataRow("1.2.3.4", "1.2.3.4", DisplayName = "an already-valid four-part version is unchanged")]
    [DataRow("1.2.3-preview.4", "1.2.3.0", DisplayName = "a semver pre-release suffix is cut, then padded")]
    [DataRow("2.0.0-rc.1+build.55", "2.0.0.0", DisplayName = "pre-release and build metadata are cut")]
    [DataRow("65535.65535.65535.65535", "65535.65535.65535.65535", DisplayName = "the upper bound is accepted")]
    public void Plan_Version_IsNormalizedToFourComponents(string declared, string expected)
    {
        var info = SingleFileManifestPlanner.Plan(SingleFile(), Props(("Version", declared)));

        Assert.AreEqual(expected, info.Version);
    }

    [TestMethod]
    public void Plan_ReadsMSBuildVersion_NotVersionPrefix()
    {
        // Setting $(Version) explicitly leaves $(VersionPrefix) EMPTY, so a planner that consulted
        // VersionPrefix first would silently discard the user's version and emit the 1.0.0.0 default.
        var props = Props(("Version", "3.4.5"), ("VersionPrefix", ""));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("3.4.5.0", info.Version);
    }

    [TestMethod]
    public void Plan_WinAppVersion_TakesPrecedenceOverMSBuildVersion()
    {
        var props = Props(
            (SingleFileManifestPlanner.VersionProperty, "9.9.9.9"),
            ("Version", "1.2.3"));

        var info = SingleFileManifestPlanner.Plan(SingleFile(), props);

        Assert.AreEqual("9.9.9.9", info.Version);
    }

    [TestMethod]
    [DataRow("70000.1.2.3", DisplayName = "a component above 65535 is rejected")]
    [DataRow("1.2.3.4.5", DisplayName = "five components are rejected, not truncated")]
    [DataRow("not-a-version", DisplayName = "unparseable text is rejected")]
    [DataRow("1.2.3oops", DisplayName = "a trailing non-numeric suffix is rejected, not silently dropped")]
    [DataRow("v1.2.3", DisplayName = "a leading non-numeric prefix is rejected")]
    [DataRow("1.2.3 (build 7)", DisplayName = "decorated build metadata is rejected")]
    public void Plan_UnusableVersion_ThrowsInsteadOfSilentlyChangingIt(string declared)
    {
        var exception = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props((SingleFileManifestPlanner.VersionProperty, declared))));

        StringAssert.Contains(exception.Message, "counter.cs", "the message should name the offending file");
        StringAssert.Contains(exception.Message, "65535", "the message should state the valid range");
    }

    [TestMethod]
    public void Plan_UnusableVersion_NamesThePropertyThatDeclaredIt()
    {
        // The two sources have different fixes, so the error must say which one was actually read.
        var winApp = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props((SingleFileManifestPlanner.VersionProperty, "70000.0"))));
        StringAssert.Contains(winApp.Message, "WinAppVersion='70000.0'");

        var msBuild = Assert.ThrowsExactly<ProjectRunException>(() =>
            SingleFileManifestPlanner.Plan(SingleFile(), Props(("Version", "70000.0"))));
        StringAssert.Contains(msBuild.Message, "Version='70000.0'");
    }

    #endregion
}
