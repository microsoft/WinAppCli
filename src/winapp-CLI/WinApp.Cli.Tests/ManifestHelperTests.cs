// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestHelperTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ManifestHelper_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void FindManifest_PackageAppxmanifestPresent_ReturnsExistingFile()
    {
        var expected = Path.Combine(_tempDir, "Package.appxmanifest");
        File.WriteAllText(expected, "<manifest/>");

        var result = ManifestHelper.FindManifest(_tempDir);

        Assert.IsTrue(result.Exists);
        Assert.AreEqual(expected, result.FullName);
    }

    [TestMethod]
    public void FindManifest_OnlyLowercaseXmlPresent_ReturnsIt()
    {
        var expected = Path.Combine(_tempDir, "appxmanifest.xml");
        File.WriteAllText(expected, "<manifest/>");

        var result = ManifestHelper.FindManifest(_tempDir);

        Assert.IsTrue(result.Exists);
        Assert.AreEqual(expected, result.FullName);
    }

    [TestMethod]
    public void FindManifest_BothPresent_PrefersPackageAppxmanifest()
    {
        var preferred = Path.Combine(_tempDir, "Package.appxmanifest");
        File.WriteAllText(preferred, "<manifest/>");
        File.WriteAllText(Path.Combine(_tempDir, "appxmanifest.xml"), "<manifest/>");

        var result = ManifestHelper.FindManifest(_tempDir);

        Assert.AreEqual(preferred, result.FullName, "Package.appxmanifest must take precedence.");
    }

    [TestMethod]
    public void FindManifest_NeitherPresent_ReturnsNonExistentPrimaryName()
    {
        var result = ManifestHelper.FindManifest(_tempDir);

        Assert.IsFalse(result.Exists, "A missing manifest must be reported via FileInfo.Exists == false.");
        Assert.AreEqual(Path.Combine(_tempDir, "Package.appxmanifest"), result.FullName);
    }
}
