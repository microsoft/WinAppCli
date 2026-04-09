// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class PackageCommandTests : BaseCommandTests
{
    private IMsixService _msixService = null!;
    private IWorkspaceSetupService _workspaceSetupService = null!;
    private ICertificateService _certificateService = null!;

    /// <summary>
    /// Standard test manifest content for use across multiple tests
    /// </summary>
    private const string StandardTestManifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap rescap"">
  <Identity Name=""TestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package for integration testing</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.exe"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
</Package>";

    [TestInitialize]
    public void Setup()
    {
        _msixService = GetRequiredService<IMsixService>();
        _workspaceSetupService = GetRequiredService<IWorkspaceSetupService>();
        _certificateService = GetRequiredService<ICertificateService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Clean up test certificates from the certificate store
        // This prevents test certificates from accumulating in the CurrentUser\My store
        // and potentially interfering with other tests or system operations.
        // The cleanup logic matches the pattern used in SignCommandTests.cs
        var testCertificatePublishers = new[]
        {
            "CN=TestPublisher",
            "CN=WrongPublisher",
            "CN=ExternalTestPublisher",
            "CN=DifferentPublisher",
            "CN=TestCertificatePublisher",
            "CN=PasswordTestPublisher",
            "CN=CommonValidationPublisher",
            "CN=CertificatePublisher"
        };

        foreach (var publisher in testCertificatePublishers)
        {
            CleanupInvalidTestCertificatesFromStore(publisher);
        }
    }

    /// <summary>
    /// Extracts and reads the AppxManifest.xml content from a created MSIX package
    /// </summary>
    private async Task<string> ExtractManifestContentFromPackageAsync(FileInfo msixPath, string extractSubDir)
    {
        var extractDir = Path.Combine(_tempDirectory.FullName, extractSubDir);
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(msixPath.FullName, extractDir);

        var extractedManifestPath = Path.Combine(extractDir, "AppxManifest.xml");
        Assert.IsTrue(File.Exists(extractedManifestPath), "Extracted manifest should exist");

        return await File.ReadAllTextAsync(extractedManifestPath, TestContext.CancellationToken);
    }

    /// <summary>
    /// Creates a minimal test package structure with manifest and basic files
    /// </summary>
    private static void CreateTestPackageStructure(DirectoryInfo packageDir)
    {
        packageDir.Create();

        // Use the shared standard test manifest content
        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        // Create Assets directory and a fake logo
        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");

        // Create a fake executable
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");
    }

    /// <summary>
    /// Creates external test manifest content with different identity for external manifest tests
    /// </summary>
    private static string CreateExternalTestManifest()
    {
        return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap rescap"">
  <Identity Name=""ExternalTestPackage""
            Publisher=""CN=ExternalTestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>External Test Package</DisplayName>
    <PublisherDisplayName>External Test Publisher</PublisherDisplayName>
    <Description>Test package with external manifest</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""ExternalTestApp"" Executable=""TestApp.exe"" EntryPoint=""ExternalTestApp.App"">
      <uap:VisualElements DisplayName=""External Test App"" Description=""Test application with external manifest""
                          BackgroundColor=""#333333"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
</Package>";
    }

    private static string CreateExternalTestManifestWithScaledVisualLogos()
    {
        return @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap rescap"">
  <Identity Name=""ExternalTestPackage""
            Publisher=""CN=ExternalTestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>External Test Package</DisplayName>
    <PublisherDisplayName>External Test Publisher</PublisherDisplayName>
    <Description>Test package with external manifest</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""ExternalTestApp"" Executable=""TestApp.exe"" EntryPoint=""ExternalTestApp.App"">
      <uap:VisualElements DisplayName=""External Test App"" Description=""Test application with external manifest""
                          BackgroundColor=""#333333"" Square150x150Logo=""Assets\Logo.scale-200.png"" Square44x44Logo=""Assets\Logo.scale-200.png"" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
</Package>";
    }

    private static string CreateExternalTestManifestWithScaledVisualLogosInImagesFolder()
    {
        return CreateExternalTestManifestWithScaledVisualLogos()
            .Replace("Assets\\StoreLogo.png", "Images\\StoreLogo.png", StringComparison.Ordinal)
            .Replace("Assets\\Logo.scale-200.png", "Images\\Logo.scale-200.png", StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes test certificates from the CurrentUser\My certificate store
    /// This ensures test certificates don't accumulate and interfere with other tests
    /// </summary>
    /// <param name="subjectName">Certificate subject name to clean up (e.g., "CN=TestPublisher")</param>
    private static void CleanupInvalidTestCertificatesFromStore(string subjectName)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName.Replace("CN=", ""), false);

            foreach (X509Certificate2 cert in certificates)
            {
                // Remove all test certificates - we don't need datetime logic
                store.Remove(cert);
            }
        }
        catch
        {
            // Ignore cleanup errors - not critical for test functionality
            // The certificate store cleanup is a best-effort operation
        }
    }

    [TestMethod]
    public async Task PackageCommand_ToolDiscovery_FindsCommonBuildTools()
    {
        // This test verifies that common build tools can be discovered after installation
        var commonTools = new[] { "makeappx.exe", "makepri.exe", "mt.exe", "signtool.exe" };
        var foundTools = new List<string>();
        var missingTools = new List<string>();

        // Ensure BuildTools are installed
        var buildToolsPath = await _buildToolsService.EnsureBuildToolsAsync(TestTaskContext, cancellationToken: TestContext.CancellationToken);
        if (buildToolsPath == null)
        {
            Assert.Fail("Cannot run test - BuildTools installation failed.");
            return;
        }

        // Check each common tool
        foreach (var tool in commonTools)
        {
            var toolPath = _buildToolsService.GetBuildToolPath(tool);
            if (toolPath != null)
            {
                foundTools.Add(tool);
                Console.WriteLine($"Found {tool} at: {toolPath}");
            }
            else
            {
                missingTools.Add(tool);
                Console.WriteLine($"Missing: {tool}");
            }
        }

        // Assert - We should find at least some of the common tools
        Assert.IsNotEmpty(foundTools, $"Should find at least some common build tools. Found: [{string.Join(", ", foundTools)}], Missing: [{string.Join(", ", missingTools)}]");

        // Specifically check for makeappx since it's commonly used
        Assert.Contains("makeappx.exe", foundTools, "makeappx.exe should be available in BuildTools");
    }

    [TestMethod]
    [DataRow(null, @"TestPackage_1.0.0.0.msix", DisplayName = "Null output path defaults to current directory with package name")]
    [DataRow("", @"TestPackage_1.0.0.0.msix", DisplayName = "Empty output path defaults to current directory with package name")]
    [DataRow("CustomPackage.msix", @"CustomPackage.msix", DisplayName = "Full filename with .msix extension uses as-is")]
    [DataRow("output", @"output\TestPackage_1.0.0.0.msix", DisplayName = "Directory path without .msix extension combines with package name")]
    [DataRow(@"C:\temp\output", @"C:\temp\output\TestPackage_1.0.0.0.msix", DisplayName = "Absolute directory path combines with package name")]
    [DataRow(@"C:\temp\AbsolutePackage.msix", @"C:\temp\AbsolutePackage.msix", DisplayName = "Absolute .msix file path uses as-is")]
    public async Task CreateMsixPackageAsync_OutputPathHandling_WorksCorrectly(string? outputPath, string expectedRelativePath)
    {
        // Cleanup
        if (File.Exists(expectedRelativePath))
        {
            File.Delete(expectedRelativePath);
        }

        // Arrange
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "TestPackage"));
        CreateTestPackageStructure(packageDir);

        // Create a minimal winapp.yaml to satisfy config requirements
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        var currentDir = GetRequiredService<ICurrentDirectoryProvider>().GetCurrentDirectory();

        // Convert expected relative path to absolute path based on current directory
        string expectedMsixPath;
        if (Path.IsPathRooted(expectedRelativePath))
        {
            // Already absolute - use as-is
            expectedMsixPath = expectedRelativePath;
        }
        else
        {
            // Relative - make absolute based on current directory
            expectedMsixPath = Path.GetFullPath(Path.Combine(currentDir, expectedRelativePath));
        }

        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: string.IsNullOrEmpty(outputPath) ? null : new FileInfo(Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(currentDir, outputPath)),
            TestTaskContext,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // If we get here without exception, verify the path is correct
        Assert.AreEqual(expectedMsixPath, result.MsixPath.FullName,
            $"Output path calculation incorrect. Input: '{outputPath}', Expected: '{expectedMsixPath}', Actual: '{result.MsixPath}'");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_InvalidInputFolder_ThrowsDirectoryNotFoundException()
    {
        // Arrange - Use non-existent directory
        var nonExistentDir = Path.Combine(_tempDirectory.FullName, "NonExistentPackage");

        // Act & Assert
        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: new DirectoryInfo(nonExistentDir),
                outputPath: _tempDirectory,
                TestTaskContext,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                cancellationToken: CancellationToken.None
            );
        }, "Expected DirectoryNotFoundException when input folder does not exist.");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_MissingManifest_ThrowsFileNotFoundException()
    {
        // Arrange - Create directory without manifest
        var packageDir = Path.Combine(_tempDirectory.FullName, "TestPackageNoManifest");
        Directory.CreateDirectory(packageDir);

        // Create a fake executable but no manifest
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        // Act & Assert
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(
            async () => await _msixService.CreateMsixPackageAsync(
                inputFolder: new DirectoryInfo(packageDir),
                outputPath: _tempDirectory,
                TestTaskContext,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                cancellationToken: CancellationToken.None
            ), "Expected FileNotFoundException when manifest file is missing.");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestWithAssets_CopiesManifestAndAssets()
    {
        // Arrange - Create input folder without manifest
        var packageDir = Path.Combine(_tempDirectory.FullName, "InputPackage");
        Directory.CreateDirectory(packageDir);

        // Create the executable in the input folder
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        // Create external manifest directory with manifest and assets
        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifest");
        Directory.CreateDirectory(externalManifestDir);

        // Create assets directory in external location
        var externalAssetsDir = Path.Combine(externalManifestDir, "Assets");
        Directory.CreateDirectory(externalAssetsDir);

        // Create asset files
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "Logo.png"), "external logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "StoreLogo.png"), "external store logo content", TestContext.CancellationToken);

        // Create external manifest that references the assets
        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest(), TestContext.CancellationToken);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: new DirectoryInfo(packageDir),
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "ExternalTestPackage",
            skipPri: true,
            autoSign: false,
            manifestPath: new FileInfo(externalManifestPath),
            cancellationToken: CancellationToken.None
        );

        // If successful, verify the package was created correctly
        Assert.IsNotNull(result, "Result should not be null");
        Assert.Contains("ExternalTestPackage", result.MsixPath.FullName, "Package name should reflect external manifest");

        // Verify that assets were accessible during processing
        // The external manifest and assets should still exist
        Assert.IsTrue(File.Exists(externalManifestPath), "External manifest should still exist");
        Assert.IsTrue(File.Exists(Path.Combine(externalAssetsDir, "Logo.png")), "External Logo.png should still exist");
        Assert.IsTrue(File.Exists(Path.Combine(externalAssetsDir, "StoreLogo.png")), "External StoreLogo.png should still exist");

        // Verify the input folder was not polluted with a manifest copy
        Assert.IsFalse(File.Exists(Path.Combine(packageDir, "AppxManifest.xml")), "Input folder should not contain AppxManifest.xml after packaging");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestWithAssets_IncludesExternalAndMrtAssetsInMsix()
    {
        // Arrange
        var packageDir = Path.Combine(_tempDirectory.FullName, "InputPackage_WithExternalAndMrtAssets");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifest_WithExternalAndMrtAssets");
        Directory.CreateDirectory(externalManifestDir);

        var externalAssetsDir = Path.Combine(externalManifestDir, "Assets");
        Directory.CreateDirectory(externalAssetsDir);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "Logo.png"), "external logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "Logo.scale-200.png"), "external mrt logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "StoreLogo.png"), "external store logo content", TestContext.CancellationToken);

        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest(), TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: new DirectoryInfo(packageDir),
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "ExternalAndMrtAssetsIncludedPackage",
            skipPri: true,
            autoSign: false,
            manifestPath: new FileInfo(externalManifestPath),
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Assets/Logo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/StoreLogo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Assets/StoreLogo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/Logo.scale-200.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include MRT variant Assets/Logo.scale-200.png");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestWithScaledVisualLogos_IncludesAllThreeAssetsInMsix()
    {
        // Arrange
        var packageDir = Path.Combine(_tempDirectory.FullName, "InputPackage_WithScaledVisualLogos");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifest_WithScaledVisualLogos");
        Directory.CreateDirectory(externalManifestDir);

        var externalAssetsDir = Path.Combine(externalManifestDir, "Assets");
        Directory.CreateDirectory(externalAssetsDir);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "Logo.png"), "external logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "Logo.scale-200.png"), "external mrt logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "StoreLogo.png"), "external store logo content", TestContext.CancellationToken);

        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifestWithScaledVisualLogos(), TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: new DirectoryInfo(packageDir),
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "ExternalScaledVisualLogosAssetsIncludedPackage",
            skipPri: true,
            autoSign: false,
            manifestPath: new FileInfo(externalManifestPath),
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Assets/Logo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/StoreLogo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Assets/StoreLogo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/Logo.scale-200.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include MRT variant Assets/Logo.scale-200.png");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestWithImagesFolder_IncludesAllThreeAssetsInMsix()
    {
        // Arrange
        var packageDir = Path.Combine(_tempDirectory.FullName, "InputPackage_WithImagesFolder");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifest_WithImagesFolder");
        Directory.CreateDirectory(externalManifestDir);

        var externalImagesDir = Path.Combine(externalManifestDir, "Images");
        Directory.CreateDirectory(externalImagesDir);
        await File.WriteAllTextAsync(Path.Combine(externalImagesDir, "Logo.png"), "external logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalImagesDir, "Logo.scale-200.png"), "external mrt logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalImagesDir, "StoreLogo.png"), "external store logo content", TestContext.CancellationToken);

        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifestWithScaledVisualLogosInImagesFolder(), TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: new DirectoryInfo(packageDir),
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "ExternalImagesFolderAssetsIncludedPackage",
            skipPri: false,
            autoSign: false,
            manifestPath: new FileInfo(externalManifestPath),
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Images/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Images/Logo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Images/StoreLogo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Images/StoreLogo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Images/Logo.scale-200.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include MRT variant Images/Logo.scale-200.png");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithSigningAndMatchingPublishers_ShouldSucceed()
    {
        // Arrange - Create package structure
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "SigningTestPackage"));
        CreateTestPackageStructure(packageDir);

        // Create a certificate with the same publisher as the manifest
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "matching_cert.pfx"));
        const string testPassword = "testpassword123";
        const string testPublisher = "CN=TestPublisher"; // This matches StandardTestManifestContent

        await _certificateService.GenerateDevCertificateAsync(
            testPublisher, certPath, TestTaskContext, testPassword, cancellationToken: TestContext.CancellationToken);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act & Assert - This should succeed because publishers match
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "SigningTestPackage",
            skipPri: true,
            autoSign: true,
            certificatePath: certPath,
            certificatePassword: testPassword,
            cancellationToken: CancellationToken.None
        );

        // Verify the package was created and signed
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.Signed, "Package should be marked as signed");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithSigningAndMismatchedPublishers_ShouldFail()
    {
        // Arrange - Create package structure
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "MismatchedSigningTest"));
        CreateTestPackageStructure(packageDir);

        // Create a certificate with a DIFFERENT publisher than the manifest
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "mismatched_cert.pfx"));
        const string testPassword = "testpassword123";
        const string wrongPublisher = "CN=WrongPublisher"; // This does NOT match StandardTestManifestContent

        var certResult = await _certificateService.GenerateDevCertificateAsync(
            wrongPublisher, certPath, TestTaskContext, testPassword, cancellationToken: TestContext.CancellationToken);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act & Assert - This should fail because publishers don't match
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: _tempDirectory,
                TestTaskContext,
                packageName: "MismatchedSigningTest",
                skipPri: true,
                autoSign: true,
                certificatePath: certPath,
                certificatePassword: testPassword,
                cancellationToken: CancellationToken.None
            ));

        // Verify the error message contains the expected format
        Assert.Contains("Publisher in", ex.Message, "Error should mention manifest publisher");
        Assert.Contains("does not match the publisher in the certificate", ex.Message, "Error should mention certificate publisher mismatch");
        Assert.Contains("CN=TestPublisher", ex.Message, "Error should show manifest publisher");
        Assert.Contains("CN=WrongPublisher", ex.Message, "Error should show certificate publisher");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithExternalManifestAndMismatchedCertificate_ShouldFail()
    {
        // Arrange - Create input folder and external manifest with different publisher
        var packageDir = Path.Combine(_tempDirectory.FullName, "ExternalMismatchTest");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        // Create external manifest with specific publisher
        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifestForMismatch");
        Directory.CreateDirectory(externalManifestDir);
        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest(), TestContext.CancellationToken); // Uses "CN=ExternalTestPublisher"

        // Create certificate with different publisher
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "external_mismatch_cert.pfx"));
        const string testPassword = "testpassword123";
        const string wrongPublisher = "CN=DifferentPublisher";

        await _certificateService.GenerateDevCertificateAsync(
            wrongPublisher, certPath, TestTaskContext, testPassword, cancellationToken: TestContext.CancellationToken);

        // Create minimal winapp.yaml
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act & Assert - Should fail due to publisher mismatch
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await _msixService.CreateMsixPackageAsync(
                inputFolder: new DirectoryInfo(packageDir),
                outputPath: _tempDirectory,
                TestTaskContext,
                packageName: "ExternalMismatchTest",
                skipPri: true,
                autoSign: true,
                certificatePath: certPath,
                certificatePassword: testPassword,
                manifestPath: new FileInfo(externalManifestPath),
                cancellationToken: CancellationToken.None
            ));

        // Verify error message format
        Assert.Contains("CN=ExternalTestPublisher", ex.Message, "Error should show external manifest publisher");
        Assert.Contains("CN=DifferentPublisher", ex.Message, "Error should show certificate publisher");
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_ShouldReturnCorrectPublisher()
    {
        // This test uses a pre-generated certificate to test publisher extraction
        // We need to create a test certificate first

        // Arrange
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "publisher_test_cert.pfx"));
        const string testPassword = "testpassword123";
        const string expectedPublisher = "TestCertificatePublisher";
        const string testPublisherCN = $"CN={expectedPublisher}";

        // Create a test certificate using the existing certificate service
        _certificateService.GenerateDevCertificateAsync(
            testPublisherCN, certPath, TestTaskContext, testPassword, cancellationToken: TestContext.CancellationToken).GetAwaiter().GetResult();

        // Act
        var extractedPublisher = CertificateService.ExtractPublisherFromCertificate(certPath, testPassword);

        // Assert
        Assert.AreEqual(expectedPublisher, extractedPublisher, "Extracted publisher should match the expected publisher");
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_WithNonExistentFile_ShouldThrow()
    {
        // Arrange
        var nonExistentPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "nonexistent.pfx"));

        // Act & Assert
        Assert.ThrowsExactly<FileNotFoundException>(() =>
        {
            CertificateService.ExtractPublisherFromCertificate(nonExistentPath, "password");
        });
    }

    [TestMethod]
    public void CertificateService_ExtractPublisherFromCertificate_WithWrongPassword_ShouldThrow()
    {
        // Arrange - Create test certificate
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "password_test_cert.pfx"));
        const string correctPassword = "correct123";
        const string wrongPassword = "wrong123";

        _certificateService.GenerateDevCertificateAsync(
            "CN=PasswordTestPublisher", certPath, TestTaskContext, correctPassword, cancellationToken: TestContext.CancellationToken).GetAwaiter().GetResult();

        // Act & Assert
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            CertificateService.ExtractPublisherFromCertificate(certPath, wrongPassword);
        });
    }

    [TestMethod]
    public async Task CertificateService_ValidatePublisherMatch_WithMatchingPublishers_ShouldSucceed()
    {
        // Arrange - Create certificate and manifest with same publisher
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "matching_validation_cert.pfx"));
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "matching_validation_manifest.xml"));
        const string testPassword = "testpassword123";
        const string commonPublisher = "CN=CommonValidationPublisher";

        // Create certificate
        await _certificateService.GenerateDevCertificateAsync(
            commonPublisher, certPath, TestTaskContext, testPassword, cancellationToken: TestContext.CancellationToken);

        // Create manifest with same publisher
        var manifestContent = StandardTestManifestContent.Replace(
            "CN=TestPublisher", commonPublisher);
        await File.WriteAllTextAsync(manifestPath.FullName, manifestContent, TestContext.CancellationToken);

        // Act & Assert - Should not throw
        await CertificateService.ValidatePublisherMatchAsync(certPath, testPassword, manifestPath, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task CertificateService_ValidatePublisherMatch_WithMismatchedPublishers_ShouldThrow()
    {
        // Arrange - Create certificate and manifest with different publishers
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "mismatch_validation_cert.pfx"));
        var manifestPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "mismatch_validation_manifest.xml"));
        const string testPassword = "testpassword123";

        // Create certificate with one publisher
        await _certificateService.GenerateDevCertificateAsync(
            "CN=CertificatePublisher", certPath, TestTaskContext, testPassword, cancellationToken: TestContext.CancellationToken);

        // Create manifest with different publisher
        var manifestContent = StandardTestManifestContent.Replace(
            "CN=TestPublisher", "CN=ManifestPublisher");
        await File.WriteAllTextAsync(manifestPath.FullName, manifestContent, TestContext.CancellationToken);

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await CertificateService.ValidatePublisherMatchAsync(certPath, testPassword, manifestPath, TestContext.CancellationToken));

        // Verify error message format matches requirement
        Assert.Contains($"Publisher in {manifestPath} (CN=ManifestPublisher)", ex.Message);
        Assert.Contains($"does not match the publisher in the certificate {certPath} (CN=CertificatePublisher)", ex.Message);
    }

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_WithWindowsAppSdkDependency_AddsPackageDependencyOnNewLine(string winAppSdkVersion)
    {
        // Arrange - Create package structure with a manifest that has Dependencies but no WinAppSDK dependency
        var packageDir = _tempDirectory.CreateSubdirectory("WinAppSdkDependencyTest");

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        // Create Assets directory and files
        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Create winapp.yaml with Windows App SDK package to trigger dependency injection
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        // Ensure the runtime MSIX package is in the test cache — SetupWorkspaceAsync's
        // recursive NuGet resolution can silently fail under parallel test network contention
        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act - Create package (this should trigger the Windows App SDK dependency injection)
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "WinAppSdkDependencyTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: CancellationToken.None
        );

        // Assert - Read the manifest from the package and verify PackageDependency is on its own line
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        var finalManifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "extracted");

        // Verify the PackageDependency exists
        Assert.Contains("<PackageDependency Name=\"Microsoft.WindowsAppRuntime", finalManifestContent,
            "Manifest should contain Windows App SDK PackageDependency");

        // Verify it's on its own line (not on the same line as </Dependencies>)
        // The pattern we're checking: there should be a newline after the PackageDependency closing tag
        // and before the </Dependencies> tag
        var dependenciesSectionPattern = @"<Dependencies>.*?</Dependencies>";
        var dependenciesMatch = Regex.Match(finalManifestContent, dependenciesSectionPattern, RegexOptions.Singleline);
        Assert.IsTrue(dependenciesMatch.Success, "Should find Dependencies section");

        var dependenciesSection = dependenciesMatch.Value;

        // Check that PackageDependency and </Dependencies> are NOT on the same line
        var lines = dependenciesSection.Split('\n');
        var packageDependencyLine = lines.FirstOrDefault(l => l.Contains("<PackageDependency"));
        var closingTagLine = lines.FirstOrDefault(l => l.Trim() == "</Dependencies>");

        Assert.IsNotNull(packageDependencyLine, "Should find PackageDependency line");
        Assert.IsNotNull(closingTagLine, "Should find closing Dependencies tag line");

        // Verify they are different lines
        Assert.AreNotEqual(packageDependencyLine, closingTagLine,
            "PackageDependency and </Dependencies> should be on separate lines");

        // Also verify proper formatting - there should be whitespace/newline between them
        var packageDependencyIndex = dependenciesSection.IndexOf("<PackageDependency", StringComparison.InvariantCulture);
        var closingBracketIndex = dependenciesSection.IndexOf("/>", packageDependencyIndex, StringComparison.InvariantCulture) + 2;
        var closingTagIndex = dependenciesSection.IndexOf("</Dependencies>", closingBracketIndex, StringComparison.InvariantCulture);

        var betweenContent = dependenciesSection.Substring(closingBracketIndex, closingTagIndex - closingBracketIndex);
        Assert.Contains("\n", betweenContent,
            "There should be a newline between PackageDependency closing and </Dependencies> tag");
    }

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_InternalManifestWithPri_ComputesResourcesWithoutCopyingAssets(string winAppSdkVersion)
    {
        // Arrange - Manifest is INSIDE the input folder, skipPri is false.
        // This exercises the path where expandedFiles are computed for PRI generation
        // but NOT copied (because manifestIsOutsideInputFolder is false).
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "InternalManifestPriTest"));
        CreateTestPackageStructure(packageDir);

        // Create winapp.yaml with Windows App SDK package for PRI generation
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act - skipPri: false with manifest inside input folder
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "InternalManifestPriTest",
            skipPri: false,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // Assert - package should be created with PRI resources
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);

        // PRI file should have been generated
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("resources.pri", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include resources.pri when skipPri is false");

        // Assets should still be present (they were already in the input folder, not copied)
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include Assets/Logo.png from the input folder");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_ExternalManifestSkipPri_CopiesAssetsIntoMsix()
    {
        // Arrange - Manifest is OUTSIDE the input folder, skipPri is true.
        // This exercises the path where assets are copied from the external manifest
        // directory into the staging directory without PRI generation.
        var packageDir = Path.Combine(_tempDirectory.FullName, "InputPackage_ExternalSkipPri");
        Directory.CreateDirectory(packageDir);
        await File.WriteAllTextAsync(Path.Combine(packageDir, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        var externalManifestDir = Path.Combine(_tempDirectory.FullName, "ExternalManifest_SkipPri");
        Directory.CreateDirectory(externalManifestDir);

        var externalAssetsDir = Path.Combine(externalManifestDir, "Assets");
        Directory.CreateDirectory(externalAssetsDir);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "Logo.png"), "external logo content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(externalAssetsDir, "StoreLogo.png"), "external store logo content", TestContext.CancellationToken);

        var externalManifestPath = Path.Combine(externalManifestDir, "AppxManifest.xml");
        await File.WriteAllTextAsync(externalManifestPath, CreateExternalTestManifest(), TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: new DirectoryInfo(packageDir),
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "ExternalSkipPriTest",
            skipPri: true,
            autoSign: false,
            manifestPath: new FileInfo(externalManifestPath),
            cancellationToken: CancellationToken.None
        );

        // Assert - external assets should be inside the MSIX even with skipPri
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Assets/Logo.png");
        Assert.IsTrue(
            archive.Entries.Any(entry => entry.FullName.Equals("Assets/StoreLogo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include external Assets/StoreLogo.png");

        // PRI file should NOT be present since skipPri is true
        Assert.IsFalse(
            archive.Entries.Any(entry => entry.FullName.Equals("resources.pri", StringComparison.OrdinalIgnoreCase)),
            "MSIX should NOT include resources.pri when skipPri is true");
    }

    #region Placeholder Resolution Tests

    /// <summary>
    /// Manifest content with $targetnametoken$ and $targetentrypoint$ placeholders
    /// </summary>
    private const string PlaceholderTestManifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap rescap"">
  <Identity Name=""TestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package for integration testing</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""$targetnametoken$.exe"" EntryPoint=""$targetentrypoint$"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
</Package>";

    /// <summary>
    /// Creates a test package structure with placeholder manifest and a specified exe name
    /// </summary>
    private static void CreatePlaceholderTestPackageStructure(DirectoryInfo packageDir, params string[] exeFileNames)
    {
        packageDir.Create();

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), PlaceholderTestManifestContent);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");

        foreach (var exeName in exeFileNames)
        {
            File.WriteAllText(Path.Combine(packageDir.FullName, exeName), "fake exe content");
        }
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithExecutableOption_ResolvesPlaceholders()
    {
        // Arrange
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "PlaceholderExeTest"));
        CreatePlaceholderTestPackageStructure(packageDir, "MyApp.exe");

        // Create a minimal winapp.yaml to satisfy config requirements
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            executable: "MyApp.exe",
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - extract and verify the manifest from the created package
        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "PlaceholderExeExtracted");
        Assert.Contains(@"Executable=""MyApp.exe""", manifestContent, "Executable should be resolved from --executable option");
        Assert.Contains("Windows.FullTrustApplication", manifestContent,
            "$targetentrypoint$ should be resolved to Windows.FullTrustApplication");
        Assert.DoesNotContain("$targetnametoken$", manifestContent, "No $targetnametoken$ placeholders should remain");
        Assert.DoesNotContain("$targetentrypoint$", manifestContent, "No $targetentrypoint$ placeholders should remain");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_PlaceholderWithSingleExe_AutoInfers()
    {
        // Arrange - one exe file in the folder
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "PlaceholderAutoInferTest"));
        CreatePlaceholderTestPackageStructure(packageDir, "AutoDetected.exe");

        // Create a minimal winapp.yaml to satisfy config requirements
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - extract and verify the manifest from the created package
        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "PlaceholderAutoInferExtracted");
        Assert.Contains(@"Executable=""AutoDetected.exe""", manifestContent, "Executable should be auto-inferred from single exe");
        Assert.DoesNotContain("$targetnametoken$", manifestContent, "No $targetnametoken$ placeholders should remain");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_PlaceholderWithMultipleExes_Throws()
    {
        // Arrange - multiple exe files in the folder
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "PlaceholderMultiExeTest"));
        CreatePlaceholderTestPackageStructure(packageDir, "App1.exe", "App2.exe");

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: _tempDirectory,
                TestTaskContext,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                cancellationToken: TestContext.CancellationToken
            );
        });

        Assert.Contains("--executable", ex.Message, "Error message should mention --executable option");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_PlaceholderWithNoExe_Throws()
    {
        // Arrange - no exe files in the folder
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "PlaceholderNoExeTest"));
        CreatePlaceholderTestPackageStructure(packageDir); // no exe files

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await _msixService.CreateMsixPackageAsync(
                inputFolder: packageDir,
                outputPath: _tempDirectory,
                TestTaskContext,
                packageName: "TestPackage",
                skipPri: true,
                autoSign: false,
                cancellationToken: TestContext.CancellationToken
            );
        });

        Assert.Contains("--executable", ex.Message, "Error message should mention --executable option");
        Assert.Contains("no", ex.Message, "Error message should mention no exe files found");
    }

    #endregion

    #region Third-Party WinRT Component Integration Tests

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_WithWin2DPackage_AddsInProcessServerEntries(string winAppSdkVersion)
    {
        // Arrange - Create package structure
        var packageDir = _tempDirectory.CreateSubdirectory("Win2DInProcessServerTest");

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Create winapp.yaml with Windows App SDK (for build tools / workspace setup) + Win2D
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}
  - name: Microsoft.Graphics.Win2D
    version: 1.3.0";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore workspace (downloads NuGet packages and build tools)
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act - Create package (non-self-contained, should add InProcessServer entries to AppxManifest)
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "Win2DInProcessServerTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - the MSIX should be created
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "extracted_win2d");

        // Verify InProcessServer extension block was added for Win2D
        Assert.Contains("windows.activatableClass.inProcessServer", manifestContent,
            "Manifest should contain inProcessServer extension category");
        Assert.Contains("<InProcessServer>", manifestContent,
            "Manifest should contain InProcessServer element");
        Assert.Contains("Microsoft.Graphics.Canvas.dll", manifestContent,
            "Manifest should reference Microsoft.Graphics.Canvas.dll");

        // Verify well-known Win2D activatable classes are registered
        Assert.Contains("Microsoft.Graphics.Canvas.CanvasDevice", manifestContent,
            "Manifest should register CanvasDevice activatable class");
        Assert.Contains("Microsoft.Graphics.Canvas.CanvasBitmap", manifestContent,
            "Manifest should register CanvasBitmap activatable class");
        Assert.Contains("Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl", manifestContent,
            "Manifest should register CanvasControl activatable class");

        // Verify threading model is set
        Assert.Contains(@"ThreadingModel=""both""", manifestContent,
            "Activatable classes should use 'both' threading model");
    }

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_WithWin2DAndExistingExtensions_AppendsInProcessServer(string winAppSdkVersion)
    {
        // Arrange - Create manifest with existing Extensions block
        var manifestWithExtensions = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         IgnorableNamespaces=""uap rescap"">
  <Identity Name=""TestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package for integration testing</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.exe"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
  <Extensions>
    <Extension Category=""windows.activatableClass.inProcessServer"">
      <InProcessServer>
        <Path>SomeOtherComponent.dll</Path>
        <ActivatableClass ActivatableClassId=""SomeOther.Component.Class1"" ThreadingModel=""both""/>
      </InProcessServer>
    </Extension>
  </Extensions>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
</Package>";

        var packageDir = _tempDirectory.CreateSubdirectory("Win2DAppendExtensionsTest");
        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), manifestWithExtensions);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Install Win2D to NuGet cache
        var nugetService = GetRequiredService<INugetService>();
        await nugetService.InstallPackageAsync("Microsoft.Graphics.Win2D", "1.3.0", TestTaskContext, TestContext.CancellationToken);

        // Create winapp.yaml with WinApp SDK + Win2D
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}
  - name: Microsoft.Graphics.Win2D
    version: 1.3.0";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore workspace
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "Win2DAppendExtensionsTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.MsixPath.Exists);

        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "extracted_win2d_append");

        // Verify existing extension is preserved
        Assert.Contains("SomeOtherComponent.dll", manifestContent,
            "Existing InProcessServer entries should be preserved");
        Assert.Contains("SomeOther.Component.Class1", manifestContent,
            "Existing activatable class registrations should be preserved");

        // Verify Win2D entries were appended
        Assert.Contains("Microsoft.Graphics.Canvas.dll", manifestContent,
            "Win2D InProcessServer entries should be added alongside existing extensions");
        Assert.Contains("Microsoft.Graphics.Canvas.CanvasDevice", manifestContent,
            "Win2D CanvasDevice should be registered");
    }

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_WithWebView2Package_AddsInProcessServerEntries(string winAppSdkVersion)
    {
        // Arrange - WebView2's implementation DLL is in lib/ (not runtimes/win-{arch}/native/)
        // This tests the lib/ fallback discovery path
        var packageDir = _tempDirectory.CreateSubdirectory("WebView2InProcessServerTest");

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Create winapp.yaml with Windows App SDK + WebView2
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}
  - name: Microsoft.Web.WebView2
    version: 1.0.3179.45";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore workspace
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "WebView2InProcessServerTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "extracted_webview2");

        // Verify InProcessServer extension block was added for WebView2
        Assert.Contains("windows.activatableClass.inProcessServer", manifestContent,
            "Manifest should contain inProcessServer extension category");
        Assert.Contains("<InProcessServer>", manifestContent,
            "Manifest should contain InProcessServer element");
        Assert.Contains("Microsoft.Web.WebView2.Core.dll", manifestContent,
            "Manifest should reference Microsoft.Web.WebView2.Core.dll");

        // Verify well-known WebView2 activatable classes are registered
        Assert.Contains("Microsoft.Web.WebView2.Core.CoreWebView2", manifestContent,
            "Manifest should register CoreWebView2 activatable class");
        Assert.Contains("Microsoft.Web.WebView2.Core.CoreWebView2Environment", manifestContent,
            "Manifest should register CoreWebView2Environment activatable class");

        // Verify threading model is set
        Assert.Contains(@"ThreadingModel=""both""", manifestContent,
            "Activatable classes should use 'both' threading model");
    }

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_WithWin2DAndWebView2_AddsBothInProcessServers(string winAppSdkVersion)
    {
        // Arrange - Test both Win2D (native DLL in runtimes/) and WebView2 (managed DLL in lib/) together
        var packageDir = _tempDirectory.CreateSubdirectory("BothComponentsTest");

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Create winapp.yaml with Windows App SDK + Win2D + WebView2
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}
  - name: Microsoft.Graphics.Win2D
    version: 1.3.0
  - name: Microsoft.Web.WebView2
    version: 1.0.3179.45";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore workspace
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "BothComponentsTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.IsNotNull(result, "Result should not be null");
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package file should exist");

        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "extracted_both_components");

        // Verify both Win2D and WebView2 InProcessServer entries are present
        Assert.Contains("Microsoft.Graphics.Canvas.dll", manifestContent,
            "Manifest should contain Win2D DLL reference");
        Assert.Contains("Microsoft.Graphics.Canvas.CanvasDevice", manifestContent,
            "Manifest should contain Win2D CanvasDevice registration");

        Assert.Contains("Microsoft.Web.WebView2.Core.dll", manifestContent,
            "Manifest should contain WebView2 DLL reference");
        Assert.Contains("Microsoft.Web.WebView2.Core.CoreWebView2", manifestContent,
            "Manifest should contain WebView2 CoreWebView2 registration");
    }

    [TestMethod]
    [DataRow("2.0.250930001-experimental1")]
    [DataRow("1.8.251106002")]
    public async Task CreateMsixPackageAsync_WithNoWinRTComponents_DoesNotAddInProcessServer(string winAppSdkVersion)
    {
        // Arrange - Create package with a non-WinRT package (no .winmd files)
        var packageDir = _tempDirectory.CreateSubdirectory("NoWinRTComponentsTest");

        File.WriteAllText(Path.Combine(packageDir.FullName, "AppxManifest.xml"), StandardTestManifestContent);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "Logo.png"), "fake png content");
        File.WriteAllText(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content");

        // Create winapp.yaml with a non-WinRT package (no .winmd files)
        var configContent = $@"packages:
  - name: Microsoft.WindowsAppSDK
    version: {winAppSdkVersion}";
        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, configContent, TestContext.CancellationToken);

        // Restore workspace
        await _workspaceSetupService.SetupWorkspaceAsync(new WorkspaceSetupOptions
        {
            BaseDirectory = _tempDirectory,
            ConfigDir = _tempDirectory,
            RequireExistingConfig = true,
            ForceLatestBuildTools = false
        }, CancellationToken.None);

        await EnsureWinAppSdkRuntimeInTestCacheAsync(winAppSdkVersion);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "NoWinRTComponentsTest",
            skipPri: true,
            autoSign: false,
            selfContained: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert
        Assert.IsNotNull(result);
        Assert.IsTrue(result.MsixPath.Exists);

        var manifestContent = await ExtractManifestContentFromPackageAsync(result.MsixPath, "extracted_no_winrt");

        // Verify no InProcessServer entries were added
        Assert.DoesNotContain("inProcessServer", manifestContent,
            "Manifest should not contain inProcessServer entries when there are no WinRT components");
        Assert.DoesNotContain("<InProcessServer>", manifestContent,
            "No InProcessServer elements should be present");
    }

    #endregion

    #region PFX Certificate Warning Tests

    [TestMethod]
    public async Task CreateMsixPackageAsync_PfxFileInInputFolder_EmitsWarning()
    {
        // Arrange - Create a valid package structure with a .pfx file inside
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "PackageWithPfx"));
        CreateTestPackageStructure(packageDir);

        // Place a fake .pfx file in the input folder
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "dev-cert.pfx"), "fake pfx content", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Package should still be created (warning, not blocking)
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should still be created when .pfx is present");

        // Verify warning was emitted as a status message on the task context
        var statusMessages = TestTask.SubTasks
            .OfType<StatusMessageTask>()
            .Select(t => t.CompletedMessage ?? string.Empty)
            .ToList();
        Assert.IsTrue(
            statusMessages.Any(m => m.Contains("PFX certificate file found", StringComparison.OrdinalIgnoreCase)),
            $"Status messages should contain PFX warning. Messages:\n{string.Join("\n", statusMessages)}");
        Assert.IsTrue(
            statusMessages.Any(m => m.Contains("dev-cert.pfx", StringComparison.OrdinalIgnoreCase)),
            $"Warning should mention the specific PFX file name. Messages:\n{string.Join("\n", statusMessages)}");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_PfxFileInSubdirectory_EmitsWarning()
    {
        // Arrange - Create a valid package structure with a .pfx file in a subdirectory
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "PackageWithNestedPfx"));
        CreateTestPackageStructure(packageDir);

        // Place a fake .pfx file in a subdirectory
        var certsDir = Path.Combine(packageDir.FullName, "certs");
        Directory.CreateDirectory(certsDir);
        await File.WriteAllTextAsync(Path.Combine(certsDir, "test.pfx"), "fake pfx content", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "TestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: TestContext.CancellationToken
        );

        // Assert - Package should still be created
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should still be created when .pfx is in subdirectory");

        // Verify warning includes the relative path
        var statusMessages = TestTask.SubTasks
            .OfType<StatusMessageTask>()
            .Select(t => t.CompletedMessage ?? string.Empty)
            .ToList();
        Assert.IsTrue(
            statusMessages.Any(m => m.Contains("certs\\test.pfx", StringComparison.OrdinalIgnoreCase)
                                    || m.Contains("certs/test.pfx", StringComparison.OrdinalIgnoreCase)),
            $"Warning should mention the relative path to the PFX file. Messages:\n{string.Join("\n", statusMessages)}");
    }

    #endregion

    /// <summary>
    /// Installs the Windows App SDK and its critical transitive dependencies
    /// (runtime MSIX package, build tools) into the test NuGet cache.
    /// <para>
    /// <c>SetupWorkspaceAsync</c> installs packages via NuGet recursive resolution, which
    /// can silently fail under network contention when tests run in parallel
    /// (<c>ResolveDependenciesAsync</c> has a catch-all swallowing all exceptions).
    /// This method queries the NuGet API for the dependency tree and installs each
    /// WinAppSDK-related dependency individually, ensuring the runtime MSIX directory
    /// is always available for <c>FindWindowsAppSdkMsixDirectory</c>.
    /// </para>
    /// </summary>
    private async Task EnsureWinAppSdkRuntimeInTestCacheAsync(string winAppSdkVersion)
    {
        var nugetService = GetRequiredService<INugetService>();

        var deps = await nugetService.GetPackageDependenciesAsync(
            BuildToolsService.WINAPP_SDK_PACKAGE, winAppSdkVersion, TestContext.CancellationToken);

        // Install the main package plus any runtime/build tools dependencies.
        // Uses EnsurePackageInTestCacheAsync which copies from the real NuGet cache
        // when available, avoiding HTTP downloads that timeout under parallel execution.
        deps.TryAdd(BuildToolsService.WINAPP_SDK_PACKAGE, winAppSdkVersion);

        foreach (var (depId, depVersion) in deps)
        {
            if (depId.StartsWith("Microsoft.WindowsAppSDK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(depId, BuildToolsService.BUILD_TOOLS_PACKAGE, StringComparison.OrdinalIgnoreCase))
            {
                await EnsurePackageInTestCacheAsync(depId, depVersion, TestContext.CancellationToken);
            }
        }
    }

    #region AppxRecipe helpers

    /// <summary>
    /// Manifest content that includes build:Metadata with makepri.exe entry,
    /// mimicking what MSBuild generates during dotnet build.
    /// </summary>
    private const string MSBuildGeneratedManifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
         xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
         xmlns:build=""http://schemas.microsoft.com/developer/appx/2015/build""
         IgnorableNamespaces=""uap rescap build"">
  <Identity Name=""RecipeTestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Recipe Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package with MSBuild metadata</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.exe"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
  </Capabilities>
  <build:Metadata>
    <build:Item Name=""makepri.exe"" Version=""10.0.22621.3233"" />
  </build:Metadata>
</Package>";

    /// <summary>
    /// Creates a .build.appxrecipe XML file that maps source files to their package paths.
    /// </summary>
    private static string CreateAppxRecipeContent(string inputDir, (string relativeSource, string packagePath)[] files, string? manifestRelativePath = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
        sb.AppendLine(@"<Project xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">");
        sb.AppendLine(@"  <ItemGroup>");

        // Add manifest entry
        var manifestSource = manifestRelativePath ?? "AppxManifest.xml";
        sb.AppendLine($@"    <AppXManifest Include=""{Path.Combine(inputDir, manifestSource)}"">");
        sb.AppendLine(@"      <PackagePath>AppxManifest.xml</PackagePath>");
        sb.AppendLine(@"    </AppXManifest>");

        // Add file entries
        foreach (var (relativeSource, packagePath) in files)
        {
            sb.AppendLine($@"    <AppxPackagedFile Include=""{Path.Combine(inputDir, relativeSource)}"">");
            sb.AppendLine($@"      <PackagePath>{packagePath}</PackagePath>");
            sb.AppendLine(@"    </AppxPackagedFile>");
        }

        sb.AppendLine(@"  </ItemGroup>");
        sb.AppendLine(@"</Project>");
        return sb.ToString();
    }

    #endregion

    #region AppxRecipe packaging tests

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithAppxRecipe_OnlyIncludesRecipeFiles()
    {
        // Arrange — create an input folder with an MSBuild-generated manifest,
        // a .build.appxrecipe, and some files that should NOT end up in the package.
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "RecipeTestPackage"));
        packageDir.Create();

        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "AppxManifest.xml"), MSBuildGeneratedManifestContent, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.dll"), "fake dll content", TestContext.CancellationToken);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "Logo.png"), "fake logo content", TestContext.CancellationToken);

        // Files that should NOT be in the package (not in recipe)
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.pdb"), "debug symbols", TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.deps.json"), "{}", TestContext.CancellationToken);

        // Create the .build.appxrecipe — only lists the files that belong in the package
        var recipeContent = CreateAppxRecipeContent(packageDir.FullName,
        [
            ("TestApp.exe", "TestApp.exe"),
            ("TestApp.dll", "TestApp.dll"),
            (@"Assets\Logo.png", @"Assets\Logo.png"),
        ]);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.build.appxrecipe"), recipeContent, TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "RecipeTestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        var entryNames = archive.Entries.Select(e => e.FullName).ToList();

        // Files listed in the recipe should be present
        Assert.IsTrue(entryNames.Any(e => e.Equals("TestApp.exe", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include TestApp.exe from recipe");
        Assert.IsTrue(entryNames.Any(e => e.Equals("TestApp.dll", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include TestApp.dll from recipe");
        Assert.IsTrue(entryNames.Any(e => e.Equals("Assets/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include Assets/Logo.png from recipe");

        // Files NOT in the recipe should be excluded
        Assert.IsFalse(entryNames.Any(e => e.Equals("TestApp.pdb", StringComparison.OrdinalIgnoreCase)),
            "MSIX should NOT include TestApp.pdb (not in recipe)");
        Assert.IsFalse(entryNames.Any(e => e.Equals("TestApp.deps.json", StringComparison.OrdinalIgnoreCase)),
            "MSIX should NOT include TestApp.deps.json (not in recipe)");
        Assert.IsFalse(entryNames.Any(e => e.Contains(".appxrecipe", StringComparison.OrdinalIgnoreCase)),
            "MSIX should NOT include the .appxrecipe file itself");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_MSBuildManifestWithoutRecipe_FallsBackToFullCopy()
    {
        // Arrange — MSBuild-generated manifest but no .build.appxrecipe file
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "NoRecipeTestPackage"));
        packageDir.Create();

        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "AppxManifest.xml"), MSBuildGeneratedManifestContent, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "Logo.png"), "fake logo content", TestContext.CancellationToken);

        // Extra file — without a recipe, full copy should include it
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.pdb"), "debug symbols", TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "NoRecipeTestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // Assert — all files should be present because we fell back to full copy
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        var entryNames = archive.Entries.Select(e => e.FullName).ToList();

        Assert.IsTrue(entryNames.Any(e => e.Equals("TestApp.exe", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include TestApp.exe");
        Assert.IsTrue(entryNames.Any(e => e.Equals("TestApp.pdb", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include TestApp.pdb (full copy fallback)");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_NonMSBuildManifest_UsesFullCopy()
    {
        // Arrange — standard manifest without build:Metadata
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "NonMSBuildTestPackage"));
        CreateTestPackageStructure(packageDir);

        // Add extra file — should be included since it's a non-MSBuild manifest
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "extra.dll"), "extra dll content", TestContext.CancellationToken);

        // Even though there's a .appxrecipe, it should be ignored for non-MSBuild manifests
        var recipeContent = CreateAppxRecipeContent(packageDir.FullName,
        [
            ("TestApp.exe", "TestApp.exe"),
        ]);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.build.appxrecipe"), recipeContent, TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "NonMSBuildTestPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // Assert — all files should be present (non-MSBuild manifests always use full copy)
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        var entryNames = archive.Entries.Select(e => e.FullName).ToList();

        Assert.IsTrue(entryNames.Any(e => e.Equals("TestApp.exe", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include TestApp.exe");
        Assert.IsTrue(entryNames.Any(e => e.Equals("extra.dll", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include extra.dll (full copy for non-MSBuild manifest)");
    }

    [TestMethod]
    public async Task CreateMsixPackageAsync_WithAppxRecipe_MapsPackagePathsCorrectly()
    {
        // Arrange — recipe maps a file from a flat source to a nested package path
        var packageDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "RecipePathMappingPackage"));
        packageDir.Create();

        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "AppxManifest.xml"), MSBuildGeneratedManifestContent, TestContext.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.exe"), "fake exe content", TestContext.CancellationToken);

        // File at root that the recipe says should go to a subdirectory
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "logo.png"), "logo content", TestContext.CancellationToken);

        var assetsDir = Path.Combine(packageDir.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        await File.WriteAllTextAsync(Path.Combine(assetsDir, "Logo.png"), "asset logo content", TestContext.CancellationToken);

        var recipeContent = CreateAppxRecipeContent(packageDir.FullName,
        [
            ("TestApp.exe", "TestApp.exe"),
            ("logo.png", @"Assets\Logo.png"),  // remap: root → Assets subdirectory
        ]);
        await File.WriteAllTextAsync(Path.Combine(packageDir.FullName, "TestApp.build.appxrecipe"), recipeContent, TestContext.CancellationToken);

        await File.WriteAllTextAsync(_configService.ConfigPath.FullName, "packages: []", TestContext.CancellationToken);

        // Act
        var result = await _msixService.CreateMsixPackageAsync(
            inputFolder: packageDir,
            outputPath: _tempDirectory,
            TestTaskContext,
            packageName: "RecipePathMappingPackage",
            skipPri: true,
            autoSign: false,
            cancellationToken: CancellationToken.None
        );

        // Assert
        Assert.IsTrue(result.MsixPath.Exists, "MSIX package should exist");

        using var archive = ZipFile.OpenRead(result.MsixPath.FullName);
        var entryNames = archive.Entries.Select(e => e.FullName).ToList();

        // The logo.png from root should appear at Assets/Logo.png per recipe mapping
        Assert.IsTrue(entryNames.Any(e => e.Equals("Assets/Logo.png", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include Assets/Logo.png mapped from root logo.png by recipe");
        Assert.IsTrue(entryNames.Any(e => e.Equals("TestApp.exe", StringComparison.OrdinalIgnoreCase)),
            "MSIX should include TestApp.exe");
    }

    #endregion
}
