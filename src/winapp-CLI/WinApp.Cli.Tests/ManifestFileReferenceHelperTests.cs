// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="ManifestFileReferenceHelper"/>, the pure static helper that
/// walks an AppxManifest and extracts relative file-path references (used by the bundle
/// path to gather payload files). Covers the real extraction workflow plus the
/// <c>IsLikelyFilePath</c> accept/reject heuristics that keep versions, GUIDs, URIs and
/// dotted class names out of the payload set.
/// </summary>
[TestClass]
public class ManifestFileReferenceHelperTests
{
    #region ExtractAllFileReferencesFromManifest

    [TestMethod]
    public void ExtractAllFileReferences_RealManifest_FindsAttributeAndElementPaths()
    {
        // A representative manifest mixing file-path attributes, element-text paths,
        // and plenty of non-path values that must NOT be treated as references.
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Identity Name="MyApp" Version="1.0.0.0" Publisher="CN=Test" />
              <Applications>
                <Application Id="App" Executable="app/my-app.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements Square150x150Logo="Assets/Logo.png" Square44x44Logo="Assets\Small.png" />
                </Application>
              </Applications>
              <Extensions>
                <Extension Category="windows.activatableClass.inProcessServer">
                  <InProcessServer>
                    <Path>Runtime.dll</Path>
                  </InProcessServer>
                </Extension>
              </Extensions>
            </Package>
            """;

        var refs = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest(manifest);

        // File-path values are collected and normalized to backslashes.
        Assert.IsTrue(refs.Contains(@"app\my-app.exe"), "Executable path should be extracted");
        Assert.IsTrue(refs.Contains(@"Assets\Logo.png"), "Forward-slash asset path should be normalized and extracted");
        Assert.IsTrue(refs.Contains(@"Assets\Small.png"), "Backslash asset path should be extracted");
        Assert.IsTrue(refs.Contains("Runtime.dll"), "Element-text DLL path should be extracted");
        Assert.AreEqual(4, refs.Count, "Only the four real file paths should be extracted");

        // Non-path values must be rejected.
        Assert.IsFalse(refs.Contains("1.0.0.0"), "Version strings are not file paths");
        Assert.IsFalse(refs.Contains("Windows.FullTrustApplication"), "Dotted class names are not file paths");
    }

    [TestMethod]
    public void ExtractAllFileReferences_DeduplicatesCaseInsensitively()
    {
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications>
                <Application Id="A" Executable="Assets/Logo.png" />
                <Application Id="B" Executable="assets\logo.png" />
              </Applications>
            </Package>
            """;

        var refs = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest(manifest);

        Assert.AreEqual(1, refs.Count, "The same path in different case/separators should dedupe to one entry");
    }

    [TestMethod]
    public void ExtractAllFileReferences_IgnoresUrisVersionsAndNamespaces()
    {
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="App" Version="10.0.19041.0" />
              <Properties>
                <DisplayName>ms-resource:AppName</DisplayName>
                <Website>https://example.com/index.html</Website>
              </Properties>
            </Package>
            """;

        var refs = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest(manifest);

        Assert.AreEqual(0, refs.Count, "URIs, ms-resource references and version strings should not be extracted");
    }

    [TestMethod]
    public void ExtractAllFileReferences_MalformedXml_ReturnsEmpty()
    {
        var refs = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest("<Package><not-closed>");

        Assert.AreEqual(0, refs.Count, "Unparseable manifest content should yield an empty set, not throw");
    }

    [TestMethod]
    public void ExtractAllFileReferences_SkipsTextOfNonLeafElements()
    {
        // The text content check only applies to leaf elements; a parent element's
        // concatenated inner text must not be misread as a file path.
        var manifest = """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Parent>
                <Child>readme.md</Child>
              </Parent>
            </Package>
            """;

        var refs = ManifestFileReferenceHelper.ExtractAllFileReferencesFromManifest(manifest);

        Assert.AreEqual(1, refs.Count);
        Assert.IsTrue(refs.Contains("readme.md"), "Only the leaf element's text should be extracted");
    }

    #endregion

    #region IsLikelyFilePath - accepted

    [TestMethod]
    [DataRow("app.exe", DisplayName = "bare executable")]
    [DataRow("Assets\\Logo.png", DisplayName = "relative image path")]
    [DataRow("sub/dir/module.dll", DisplayName = "forward-slash nested dll")]
    [DataRow("styles.css", DisplayName = "web asset")]
    [DataRow("data.json", DisplayName = "config data")]
    [DataRow("Resources.pri", DisplayName = "pri resource index")]
    [DataRow("font.ttf", DisplayName = "font file")]
    public void IsLikelyFilePath_AcceptsRelativePathsWithKnownExtensions(string value)
    {
        Assert.IsTrue(ManifestFileReferenceHelper.IsLikelyFilePath(value), $"'{value}' should be treated as a file path");
    }

    #endregion

    #region IsLikelyFilePath - rejected

    [TestMethod]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "whitespace")]
    [DataRow("NoExtensionHere", DisplayName = "no dot / no extension")]
    [DataRow("http://example.com/logo.png", DisplayName = "http uri")]
    [DataRow("https://example.com/logo.png", DisplayName = "https uri")]
    [DataRow("ms-appx:///Assets/Logo.png", DisplayName = "ms-appx uri")]
    [DataRow("ms-resource:AppName", DisplayName = "ms-resource uri")]
    [DataRow("..\\parent\\evil.exe", DisplayName = "path traversal")]
    [DataRow("1.0.0.0", DisplayName = "version string")]
    [DataRow("10.0.19041.0", DisplayName = "os build version")]
    [DataRow("Windows.FullTrustApplication", DisplayName = "dotted class name / unknown extension")]
    [DataRow("MyCompany.MyApp", DisplayName = "namespace-like identifier")]
    [DataRow("archive.tar", DisplayName = "extension not in allow list")]
    [DataRow("assets.bundle/icon", DisplayName = "dotted directory but final segment has no extension")]
    [DataRow("my.folder\\readme", DisplayName = "dotted directory, extensionless leaf")]
    public void IsLikelyFilePath_RejectsNonPaths(string value)
    {
        Assert.IsFalse(ManifestFileReferenceHelper.IsLikelyFilePath(value), $"'{value}' should NOT be treated as a file path");
    }

    [TestMethod]
    public void IsLikelyFilePath_RejectsRootedPath()
    {
        Assert.IsFalse(ManifestFileReferenceHelper.IsLikelyFilePath(@"C:\Windows\app.exe"), "Absolute paths should be rejected");
    }

    [TestMethod]
    public void IsLikelyFilePath_RejectsUncPath()
    {
        Assert.IsFalse(ManifestFileReferenceHelper.IsLikelyFilePath(@"\\server\share\app.exe"), "UNC paths should be rejected");
    }

    [TestMethod]
    public void IsLikelyFilePath_RejectsGuid()
    {
        Assert.IsFalse(
            ManifestFileReferenceHelper.IsLikelyFilePath("12345678-1234-1234-1234-123456789abc"),
            "GUID values should be rejected");
    }

    #endregion

    #region NormalizePathSeparators

    [TestMethod]
    public void NormalizePathSeparators_ConvertsForwardSlashes()
    {
        Assert.AreEqual(@"Assets\Sub\Logo.png", ManifestFileReferenceHelper.NormalizePathSeparators("Assets/Sub/Logo.png"));
    }

    [TestMethod]
    public void NormalizePathSeparators_LeavesBackslashPathUnchanged()
    {
        Assert.AreEqual(@"Assets\Logo.png", ManifestFileReferenceHelper.NormalizePathSeparators(@"Assets\Logo.png"));
    }

    [TestMethod]
    public void NormalizePathSeparators_NoSeparators_ReturnsSameValue()
    {
        Assert.AreEqual("Logo.png", ManifestFileReferenceHelper.NormalizePathSeparators("Logo.png"));
    }

    #endregion
}
