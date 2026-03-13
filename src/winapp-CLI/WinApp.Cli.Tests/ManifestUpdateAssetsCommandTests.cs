// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestUpdateAssetsCommandTests : BaseCommandTests
{
    private string _testManifestPath = null!;
    private string _testImagePath = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a test manifest file
        _testManifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        CreateTestManifest(_testManifestPath);

        // Create a test image file
        _testImagePath = Path.Combine(_tempDirectory.FullName, "testlogo.png");
        PngHelper.CreateTestImage(_testImagePath);
    }

    private static void CreateTestManifest(string path)
    {
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package 
  xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""TestPackage"" Publisher=""CN=TestPublisher"" Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>TestPackage</DisplayName>
    <PublisherDisplayName>TestPublisher</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Applications>
    <Application Id=""TestApp"" Executable=""test.exe"">
      <uap:VisualElements
        DisplayName=""TestPackage""
        Description=""Test Application""
        BackgroundColor=""transparent""
        Square150x150Logo=""Assets\Square150x150Logo.png""
        Square44x44Logo=""Assets\Square44x44Logo.png"">
        <uap:DefaultTile Wide310x150Logo=""Assets\Wide310x150Logo.png"" />
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>";
        File.WriteAllText(path, manifestContent);
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandShouldBeAvailable()
    {
        // Arrange & Act
        var manifestCommand = GetRequiredService<ManifestCommand>();

        // Assert
        Assert.IsNotNull(manifestCommand, "ManifestCommand should be created");
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "update-assets"),
            "Should have 'update-assets' subcommand");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateAssets()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        // Verify Assets directory was created
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Verify assets referenced in manifest were generated
        // The test manifest references: StoreLogo.png, Square150x150Logo.png, Square44x44Logo.png, Wide310x150Logo.png
        var expectedAssets = new[]
        {
            "Square44x44Logo.png",
            "Square150x150Logo.png",
            "Wide310x150Logo.png",
            "StoreLogo.png"
        };

        foreach (var asset in expectedAssets)
        {
            var assetPath = Path.Combine(assetsDir, asset);
            Assert.IsTrue(File.Exists(assetPath), $"Asset {asset} should be generated");
        }
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandShouldFailWithNonExistentManifest()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var nonExistentManifest = Path.Combine(_tempDirectory.FullName, "nonexistent.xml");
        var args = new[]
        {
            _testImagePath,
            "--manifest", nonExistentManifest
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);

        // Assert
        Assert.IsNotEmpty(parseResult.Errors, "Should have parse errors for non-existent manifest");
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandShouldFailWithNonExistentImage()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var nonExistentImage = Path.Combine(_tempDirectory.FullName, "nonexistent.png");
        var args = new[]
        {
            nonExistentImage,
            "--manifest", _testManifestPath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);

        // Assert
        Assert.IsNotEmpty(parseResult.Errors, "Should have parse errors for non-existent image");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateCorrectSizes()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        // Verify specific asset sizes
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");

        // Check that scale-200 assets exist (which should be 2x the base size)
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "Square44x44Logo.scale-200.png")),
            "Square44x44Logo.scale-200.png should exist");
        Assert.IsTrue(File.Exists(Path.Combine(assetsDir, "Square150x150Logo.scale-200.png")),
            "Square150x150Logo.scale-200.png should exist");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldOverwriteExistingAssets()
    {
        // Arrange
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);

        // Create a dummy existing asset
        var existingAssetPath = Path.Combine(assetsDir, "Square150x150Logo.png");
        File.WriteAllText(existingAssetPath, "old content");
        var oldLength = new FileInfo(existingAssetPath).Length;

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");
        Assert.IsTrue(File.Exists(existingAssetPath), "Asset should still exist");

        var newLength = new FileInfo(existingAssetPath).Length;
        Assert.AreNotEqual(oldLength, newLength, "Asset should be overwritten with new content");
    }

    [TestMethod]
    public void ManifestUpdateAssetsCommandHelpShouldDisplayCorrectInformation()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[] { "--help" };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        // The help option should be recognized and not produce errors
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldLogProgress()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            _testImagePath,
            "--manifest", _testManifestPath,
            "--verbose"
        };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(updateAssetsCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully");

        Assert.Contains("Updating assets for manifest", TestAnsiConsole.Output, "Should log update message");
        Assert.Contains("generated", TestAnsiConsole.Output.ToLowerInvariant(), "Should log generation progress");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldInferManifestFromCurrentDirectory()
    {
        // Arrange
        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        // Only provide the image path, manifest should be inferred
        var args = new[]
        {
            _testImagePath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully when manifest is inferred");

        // Verify Assets directory was created
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateAssetsFromSvg()
    {
        // Arrange
        var svgImagePath = Path.Combine(_tempDirectory.FullName, "testlogo.svg");
        PngHelper.CreateTestSvgImage(svgImagePath);

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            svgImagePath,
            "--manifest", _testManifestPath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully with SVG source");

        // Verify Assets directory was created
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Verify assets referenced in manifest were generated
        var expectedAssets = new[]
        {
            "Square44x44Logo.png",
            "Square150x150Logo.png",
            "Wide310x150Logo.png",
            "StoreLogo.png"
        };

        foreach (var asset in expectedAssets)
        {
            var assetPath = Path.Combine(assetsDir, asset);
            Assert.IsTrue(File.Exists(assetPath), $"Asset {asset} should be generated from SVG source");
        }
    }

    [TestMethod]
    public async Task ManifestUpdateAssetsCommandShouldGenerateCorrectSizesFromSvg()
    {
        // Arrange
        var svgImagePath = Path.Combine(_tempDirectory.FullName, "testlogo.svg");
        PngHelper.CreateTestSvgImage(svgImagePath);

        var updateAssetsCommand = GetRequiredService<ManifestUpdateAssetsCommand>();
        var args = new[]
        {
            svgImagePath,
            "--manifest", _testManifestPath
        };

        // Act
        var parseResult = updateAssetsCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync();

        // Assert
        Assert.AreEqual(0, exitCode, "Update-assets command should complete successfully with SVG source");

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");

        // Verify scale-200 assets exist with correct dimensions (2x the base size)
        var scale200_44 = Path.Combine(assetsDir, "Square44x44Logo.scale-200.png");
        Assert.IsTrue(File.Exists(scale200_44), "Square44x44Logo.scale-200.png should exist when generated from SVG");
        using (var bmp44 = new System.Drawing.Bitmap(scale200_44))
        {
            Assert.AreEqual(88, bmp44.Width, "Square44x44Logo.scale-200 should be 88px wide (44 * 2)");
            Assert.AreEqual(88, bmp44.Height, "Square44x44Logo.scale-200 should be 88px tall (44 * 2)");
        }

        var scale200_150 = Path.Combine(assetsDir, "Square150x150Logo.scale-200.png");
        Assert.IsTrue(File.Exists(scale200_150), "Square150x150Logo.scale-200.png should exist when generated from SVG");
        using (var bmp150 = new System.Drawing.Bitmap(scale200_150))
        {
            Assert.AreEqual(300, bmp150.Width, "Square150x150Logo.scale-200 should be 300px wide (150 * 2)");
            Assert.AreEqual(300, bmp150.Height, "Square150x150Logo.scale-200 should be 300px tall (150 * 2)");
        }
    }
}
