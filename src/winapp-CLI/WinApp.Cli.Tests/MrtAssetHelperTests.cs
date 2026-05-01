// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class MrtAssetHelperTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), $"MrtTest_{Guid.NewGuid():N}"));
        _tempDir.Create();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_tempDir.Exists)
        {
            _tempDir.Delete(recursive: true);
        }
    }

    #region IsSingleQualifierToken

    [TestMethod]
    [DataRow("scale-100", DisplayName = "scale-100")]
    [DataRow("scale-200", DisplayName = "scale-200")]
    [DataRow("scale-400", DisplayName = "scale-400")]
    [DataRow("theme-dark", DisplayName = "theme-dark")]
    [DataRow("theme-light", DisplayName = "theme-light")]
    [DataRow("contrast-standard", DisplayName = "contrast-standard")]
    [DataRow("contrast-high", DisplayName = "contrast-high")]
    [DataRow("targetsize-16", DisplayName = "targetsize-16")]
    [DataRow("targetsize-256", DisplayName = "targetsize-256")]
    [DataRow("altform-unplated", DisplayName = "altform-unplated")]
    [DataRow("altform-lightunplated", DisplayName = "altform-lightunplated")]
    [DataRow("dxfeaturelevel-9", DisplayName = "dxfeaturelevel-9")]
    [DataRow("dxfeaturelevel-11", DisplayName = "dxfeaturelevel-11")]
    [DataRow("device-family-desktop", DisplayName = "device-family-desktop")]
    [DataRow("device-family-xbox", DisplayName = "device-family-xbox")]
    [DataRow("homeregion-US", DisplayName = "homeregion-US")]
    [DataRow("homeregion-JP", DisplayName = "homeregion-JP")]
    [DataRow("configuration-debug", DisplayName = "configuration-debug")]
    [DataRow("configuration-retail", DisplayName = "configuration-retail")]
    [DataRow("en-US", DisplayName = "en-US (language)")]
    [DataRow("fr", DisplayName = "fr (language)")]
    [DataRow("zh-Hans", DisplayName = "zh-Hans (language)")]
    [DataRow("pt-BR", DisplayName = "pt-BR (language)")]
    [DataRow("ltr", DisplayName = "ltr (layout direction)")]
    [DataRow("rtl", DisplayName = "rtl (layout direction)")]
    public void IsSingleQualifierToken_ReturnsTrue_ForValidTokens(string token)
    {
        Assert.IsTrue(MrtAssetHelper.IsSingleQualifierToken(token));
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty string")]
    [DataRow("Logo", DisplayName = "plain name")]
    [DataRow("foo-barbaz1234", DisplayName = "arbitrary hyphenated (subtag too long)")]
    [DataRow("scale-abc", DisplayName = "scale with non-numeric")]
    [DataRow("theme-blue", DisplayName = "invalid theme")]
    [DataRow("contrast-low", DisplayName = "invalid contrast")]
    [DataRow("device-family-phone", DisplayName = "invalid device family")]
    public void IsSingleQualifierToken_ReturnsFalse_ForInvalidTokens(string token)
    {
        Assert.IsFalse(MrtAssetHelper.IsSingleQualifierToken(token));
    }

    [TestMethod]
    public void IsSingleQualifierToken_ReturnsFalse_ForNull()
    {
        Assert.IsFalse(MrtAssetHelper.IsSingleQualifierToken(null!));
    }

    #endregion

    #region IsQualifierToken (compound)

    [TestMethod]
    [DataRow("scale-200", DisplayName = "single qualifier")]
    [DataRow("scale-200_theme-dark", DisplayName = "compound: scale + theme")]
    [DataRow("targetsize-24_altform-unplated", DisplayName = "compound: targetsize + altform")]
    [DataRow("en-US", DisplayName = "language alone")]
    public void IsQualifierToken_ReturnsTrue_ForValidTokens(string token)
    {
        Assert.IsTrue(MrtAssetHelper.IsQualifierToken(token));
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty string")]
    [DataRow("Logo", DisplayName = "plain name")]
    [DataRow("scale-200_Logo", DisplayName = "qualifier + non-qualifier")]
    [DataRow("Logo_scale-200", DisplayName = "non-qualifier + qualifier")]
    public void IsQualifierToken_ReturnsFalse_ForInvalidTokens(string token)
    {
        Assert.IsFalse(MrtAssetHelper.IsQualifierToken(token));
    }

    #endregion

    #region IsMrtVariantName

    [TestMethod]
    public void IsMrtVariantName_ExactMatch_ReturnsTrue()
    {
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo"));
    }

    [TestMethod]
    public void IsMrtVariantName_CaseInsensitive()
    {
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "logo"));
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("logo", "Logo"));
    }

    [TestMethod]
    public void IsMrtVariantName_WithSingleQualifier()
    {
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.scale-200"));
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.targetsize-48"));
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.theme-dark"));
    }

    [TestMethod]
    public void IsMrtVariantName_WithMultipleQualifiers()
    {
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.scale-200.theme-dark"));
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.targetsize-48.altform-unplated"));
    }

    [TestMethod]
    public void IsMrtVariantName_WithCompoundQualifier()
    {
        Assert.IsTrue(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.targetsize-24_altform-unplated"));
    }

    [TestMethod]
    public void IsMrtVariantName_ReturnsFalse_ForNonQualifierSuffix()
    {
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("Logo", "Logo.99invalid"));
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("Logo", "LogoExtra"));
    }

    [TestMethod]
    public void IsMrtVariantName_ReturnsFalse_ForDifferentBaseName()
    {
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("Logo", "Icon.scale-200"));
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("Logo", "Banner"));
    }

    [TestMethod]
    public void IsMrtVariantName_ReturnsFalse_ForEmptyInputs()
    {
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("", "Logo"));
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("Logo", ""));
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("", ""));
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName(null!, "Logo"));
        Assert.IsFalse(MrtAssetHelper.IsMrtVariantName("Logo", null!));
    }

    #endregion

    #region GetMrtVariantBaseName

    [TestMethod]
    public void GetMrtVariantBaseName_UnqualifiedName_ReturnsSame()
    {
        Assert.AreEqual("Logo", MrtAssetHelper.GetMrtVariantBaseName("Logo"));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_SingleQualifier_ReturnsBase()
    {
        Assert.AreEqual("Logo", MrtAssetHelper.GetMrtVariantBaseName("Logo.scale-100"));
        Assert.AreEqual("Logo", MrtAssetHelper.GetMrtVariantBaseName("Logo.targetsize-48"));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_MultipleQualifiers_ReturnsBase()
    {
        Assert.AreEqual("Logo", MrtAssetHelper.GetMrtVariantBaseName("Logo.scale-200.theme-dark"));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_CompoundQualifier_ReturnsBase()
    {
        Assert.AreEqual("Logo", MrtAssetHelper.GetMrtVariantBaseName("Logo.targetsize-24_altform-unplated"));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_DottedBaseName_PreservesNonQualifierDots()
    {
        // "Assets.Logo.scale-200" → base should be "Assets.Logo"
        Assert.AreEqual("Assets.Logo", MrtAssetHelper.GetMrtVariantBaseName("Assets.Logo.scale-200"));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_NoQualifiers_ReturnsOriginal()
    {
        Assert.AreEqual("SomeFile.backup", MrtAssetHelper.GetMrtVariantBaseName("SomeFile.backup"));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_ThrowsForNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => MrtAssetHelper.GetMrtVariantBaseName(null!));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_ThrowsForEmpty()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MrtAssetHelper.GetMrtVariantBaseName(""));
    }

    [TestMethod]
    public void GetMrtVariantBaseName_ThrowsForWhitespace()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MrtAssetHelper.GetMrtVariantBaseName("   "));
    }

    #endregion

    #region ExpandManifestReferencedFiles

    [TestMethod]
    public void ExpandManifestReferencedFiles_FindsMrtVariants()
    {
        // Create files: Logo.png, Logo.scale-100.png, Logo.scale-200.png
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.scale-100.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.scale-200.png"), [0]);

        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Logo.png"],
            taskContext: null);

        Assert.AreEqual(3, result.Count);
        Assert.IsTrue(result.Any(f => f.RelativePath == "Logo.png"));
        Assert.IsTrue(result.Any(f => f.RelativePath == "Logo.scale-100.png"));
        Assert.IsTrue(result.Any(f => f.RelativePath == "Logo.scale-200.png"));
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_FallsBackToExactFile_WhenNoVariants()
    {
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Icon.png"), [0]);

        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Icon.png"],
            taskContext: null);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Icon.png", result[0].RelativePath);
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_MissingDirectory_ReturnsEmpty()
    {
        // Reference a file in a non-existent subdirectory
        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["nonexistent\\Logo.png"],
            taskContext: null);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_MissingFile_NoVariants_ReturnsEmpty()
    {
        // No files exist at all
        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Missing.png"],
            taskContext: null);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_ExcludesNonVariantFiles()
    {
        // Logo.png, Logo.backup.png (not a qualifier), Logo.scale-200.png
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.backup.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.scale-200.png"), [0]);

        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Logo.png"],
            taskContext: null);

        // Should include Logo.png and Logo.scale-200.png, but NOT Logo.backup.png
        Assert.AreEqual(2, result.Count);
        Assert.IsFalse(result.Any(f => f.RelativePath.Contains("backup")));
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_HandlesSubdirectories()
    {
        var assetsDir = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "Assets"));
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "Logo.png"), [0]);
        File.WriteAllBytes(Path.Combine(assetsDir.FullName, "Logo.scale-200.png"), [0]);

        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Assets\\Logo.png"],
            taskContext: null);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.All(f => f.RelativePath.StartsWith("Assets\\", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_WithIncludeFilter()
    {
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.scale-100.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.scale-200.png"), [0]);

        // Only include files that end with scale-200
        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Logo.png"],
            taskContext: null,
            includeFile: f => f.Name.Contains("scale-200"));

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("Logo.scale-200.png", result[0].RelativePath);
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_EmptyReferencedFiles_ReturnsEmpty()
    {
        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            [],
            taskContext: null);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_DeduplicatesAcrossReferences()
    {
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.png"), [0]);
        File.WriteAllBytes(Path.Combine(_tempDir.FullName, "Logo.scale-200.png"), [0]);

        // Reference the same logical file twice
        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            ["Logo.png", "Logo.png"],
            taskContext: null);

        // Dictionary-based dedup should prevent duplicates
        Assert.AreEqual(2, result.Count); // Logo.png + Logo.scale-200.png
    }

    #endregion

    #region ExpandManifestReferencedFiles — unplated variants

    [TestMethod]
    public void ExpandManifestReferencedFiles_IncludesUnplatedVariants()
    {
        // Arrange — create asset directory with base icon + unplated variants
        var assetsDir = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "Assets"));
        var assetFiles = new[]
        {
            "Square44x44Logo.png",
            "Square44x44Logo.targetsize-16.png",
            "Square44x44Logo.targetsize-16_altform-unplated.png",
            "Square44x44Logo.targetsize-24.png",
            "Square44x44Logo.targetsize-24_altform-unplated.png",
            "Square44x44Logo.targetsize-48.png",
            "Square44x44Logo.targetsize-48_altform-unplated.png",
            "Square44x44Logo.scale-200.png",
        };
        foreach (var file in assetFiles)
        {
            File.WriteAllBytes(Path.Combine(assetsDir.FullName, file), [0]);
        }

        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            [@"Assets\Square44x44Logo.png"],
            taskContext: null);

        // Assert — all variants including unplated must be discovered for PRI generation
        Assert.AreEqual(assetFiles.Length, result.Count, "Should expand to all MRT variants in the directory");
        Assert.IsTrue(result.Any(f => f.RelativePath.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase)),
            "Should discover altform-unplated variants for taskbar icon resolution");
    }

    [TestMethod]
    public void ExpandManifestReferencedFiles_IncludesLightUnplatedVariants()
    {
        var assetsDir = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "Assets"));
        var assetFiles = new[]
        {
            "AppList.png",
            "AppList.targetsize-32.png",
            "AppList.targetsize-32_altform-unplated.png",
            "AppList.targetsize-32_altform-lightunplated.png",
        };
        foreach (var file in assetFiles)
        {
            File.WriteAllBytes(Path.Combine(assetsDir.FullName, file), [0]);
        }

        var result = MrtAssetHelper.ExpandManifestReferencedFiles(
            _tempDir,
            [@"Assets\AppList.png"],
            taskContext: null);

        Assert.AreEqual(assetFiles.Length, result.Count);
        Assert.IsTrue(result.Any(f => f.RelativePath.Contains("altform-unplated", StringComparison.OrdinalIgnoreCase)),
            "Should discover altform-unplated variants");
        Assert.IsTrue(result.Any(f => f.RelativePath.Contains("altform-lightunplated", StringComparison.OrdinalIgnoreCase)),
            "Should discover altform-lightunplated variants");
    }

    #endregion

    #region PriIncludedExtensions

    [TestMethod]
    [DataRow(".png")]
    [DataRow(".jpg")]
    [DataRow(".jpeg")]
    [DataRow(".gif")]
    [DataRow(".bmp")]
    [DataRow(".ico")]
    [DataRow(".svg")]
    public void PriIncludedExtensions_ContainsExpected(string ext)
    {
        Assert.IsTrue(MrtAssetHelper.PriIncludedExtensions.Contains(ext));
    }

    [TestMethod]
    [DataRow(".exe")]
    [DataRow(".dll")]
    [DataRow(".xml")]
    [DataRow(".txt")]
    public void PriIncludedExtensions_ExcludesNonImage(string ext)
    {
        Assert.IsFalse(MrtAssetHelper.PriIncludedExtensions.Contains(ext));
    }

    #endregion
}
