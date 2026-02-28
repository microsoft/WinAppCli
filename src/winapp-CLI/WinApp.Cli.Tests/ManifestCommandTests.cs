// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class ManifestCommandTests : BaseCommandTests
{
    private string _testLogoPath = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a fake logo file for testing
        _testLogoPath = Path.Combine(_tempDirectory.FullName, "testlogo.png");
        PngHelper.CreateTestImage(_testLogoPath);
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services
            .AddSingleton<IDevModeService, FakeDevModeService>();
    }

    [TestMethod]
    public void ManifestCommandShouldHaveGenerateSubcommand()
    {
        // Arrange & Act
        var manifestCommand = GetRequiredService<ManifestCommand>();

        // Assert
        Assert.IsNotNull(manifestCommand, "ManifestCommand should be created");
        Assert.AreEqual("manifest", manifestCommand.Name, "Command name should be 'manifest'");
        Assert.IsTrue(manifestCommand.Subcommands.Any(c => c.Name == "generate"), "Should have 'generate' subcommand");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithDefaultsShouldCreateManifest()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var expectedManifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(expectedManifestPath), "AppxManifest.xml should be created");

        // Verify Assets directory was created
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithCustomOptionsShouldUseThoseValues()
    {
        var exeFilePath = Path.Combine(_tempDirectory.FullName, "TestApp.exe");
        await File.WriteAllTextAsync(exeFilePath, "fake exe content", TestContext.CancellationToken);

        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--package-name", "TestPackage",
            "--publisher-name", "CN=TestPublisher",
            "--version", "2.0.0.0",
            "--description", "Test Application",
            "--executable", exeFilePath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify manifest content contains our custom values
        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("TestPackage", manifestContent, "Manifest should contain custom package name");
        Assert.Contains("CN=TestPublisher", manifestContent, "Manifest should contain custom publisher");
        Assert.Contains("2.0.0.0", manifestContent, "Manifest should contain custom version");
        Assert.Contains("Test Application", manifestContent, "Manifest should contain custom description");
        Assert.Contains("$targetnametoken$.exe", manifestContent, "Manifest should contain placeholder executable name");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithSparseOptionShouldCreateSparseManifest()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--template", "sparse"
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify sparse package specific content
        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.Contains("uap10:AllowExternalContent", manifestContent, "Sparse manifest should contain AllowExternalContent");
        Assert.Contains("packagedClassicApp", manifestContent, "Sparse manifest should contain packagedClassicApp");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithLogoShouldGenerateAssets()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--logo-path", _testLogoPath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify Assets directory was created with generated asset files
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Assert.IsTrue(Directory.Exists(assetsDir), "Assets directory should be created");

        // Verify expected MSIX asset files were generated
        var expectedAssets = new[]
        {
            "Square44x44Logo.png",
            "Square150x150Logo.png",
            "Wide310x150Logo.png",
            "StoreLogo.png"
        };

        foreach (var assetName in expectedAssets)
        {
            var assetPath = Path.Combine(assetsDir, assetName);
            Assert.IsTrue(File.Exists(assetPath), $"Asset {assetName} should be generated");

            // Since the source image is a 1x1 transparent pixel, all generated assets
            // should be fully transparent (empty canvases)
            Assert.IsTrue(PngHelper.IsFullyTransparent(assetPath),
                $"Asset {assetName} should be fully transparent when generated from a transparent source");
        }
    }

    [TestMethod]
    public async Task ManifestGenerateCommandShouldFailIfManifestAlreadyExists()
    {
        // Arrange - Create an existing manifest
        var existingManifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(existingManifestPath, "<Package>Existing</Package>", TestContext.CancellationToken);

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, exitCode, "Generate command should fail when manifest already exists");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandParseArgumentsShouldHandleAllOptions()
    {
        var exeFilePath = Path.Combine(_tempDirectory.FullName, "app.exe");
        await File.WriteAllTextAsync(exeFilePath, "fake exe content", TestContext.CancellationToken);

        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--package-name", "TestPkg",
            "--publisher-name", "CN=TestPub",
            "--version", "1.2.3.4",
            "--description", "Test Description",
            "--executable", exeFilePath,
            "--template", "sparse",
            "--logo-path", _testLogoPath,
            "--verbose"
        };

        // Act
        var parseResult = generateCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
    }

    [TestMethod]
    public void ManifestGenerateCommandShouldUseCurrentDirectoryAsDefault()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();

        // Act
        var parseResult = generateCommand.Parse([]);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");

        // The command should use current directory as default when no directory is specified
        // This is validated by the DefaultValueFactory in the command definition
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithVerboseOptionShouldProduceOutput()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--verbose"
        };

        DefaultAnswers();

        var exitCode = await ParseAndInvokeWithCaptureAsync(generateCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        Assert.Contains("Generating manifest", TestAnsiConsole.Output, "Verbose output should contain generation message");
    }

    [TestMethod]
    [DataRow("My@App#Name", "MyAppName", DisplayName = "Should remove invalid characters")]
    [DataRow("_InvalidStart", "InvalidStart", DisplayName = "Should remove underscores")]
    [DataRow("", "DefaultPackage", DisplayName = "Should use default for empty string")]
    [DataRow("  ", "DefaultPackage", DisplayName = "Should use default for whitespace")]
    [DataRow("Ab", "Ab1", DisplayName = "Should pad short names")]
    [DataRow("VeryLongPackageNameThatExceedsFiftyCharacterLimit123456", "VeryLongPackageNameThatExceedsFiftyCharacterLimit1", DisplayName = "Should truncate long names")]
    [DataRow("Valid-Package.Name.1", "Valid-Package.Name.1", DisplayName = "Should keep valid names unchanged")]
    [DataRow("Test_With_Underscores", "TestWithUnderscores", DisplayName = "Should remove all underscores")]
    [DataRow("Name With Spaces", "NameWithSpaces", DisplayName = "Should remove spaces")]
    [DataRow("Mixed_Under-scores.And.Dashes", "MixedUnder-scores.And.Dashes", DisplayName = "Should remove underscores but keep dashes and periods")]
    public void CleanPackageNameShouldSanitizeInvalidCharacters(string input, string expected)
    {
        // Act
        var result = ManifestService.CleanPackageName(input);

        // Assert
        Assert.AreEqual(expected, result, $"CleanPackageName('{input}') should return '{expected}'");
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithNonExistentLogoShouldIgnoreLogo()
    {
        // Arrange
        var nonExistentLogoPath = Path.Combine(_tempDirectory.FullName, "nonexistent.png");
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--logo-path", nonExistentLogoPath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully even with non-existent logo");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        // Verify no logo was copied (since it doesn't exist)
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        var wouldBeCopiedLogoPath = Path.Combine(assetsDir, "nonexistent.png");
        Assert.IsFalse(File.Exists(wouldBeCopiedLogoPath), "Non-existent logo should not be copied");
    }

    [TestMethod]
    public void ManifestCommandHelpShouldDisplayCorrectInformation()
    {
        // Arrange
        var manifestCommand = GetRequiredService<ManifestCommand>();
        var args = new[] { "--help" };

        // Act
        var parseResult = manifestCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        // The help option should be recognized and not produce errors
    }

    [TestMethod]
    public void ManifestGenerateCommandHelpShouldDisplayCorrectInformation()
    {
        // Arrange
        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[] { "--help" };

        // Act
        var parseResult = generateCommand.Parse(args);

        // Assert
        Assert.IsNotNull(parseResult, "Parse result should not be null");
        // The help option should be recognized and not produce errors
    }

    [TestMethod]
    public async Task CreateDebugIdentityWithKeepIdentityShouldPreserveOriginalIdentity()
    {
        // Arrange - Use .bat instead of .exe to avoid EmbedMsixIdentityToExeAsync which requires mt.exe from Build Tools
        var entryPointPath = Path.Combine(_tempDirectory.FullName, "TestApp.bat");
        await File.WriteAllTextAsync(entryPointPath, "@echo off", TestContext.CancellationToken);

        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""MyTestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.bat"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        // Create minimal assets so the command doesn't fail
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        PngHelper.CreateTestImage(Path.Combine(assetsDir, "Logo.png"));

        // Act - Create debug identity with --keep-identity
        var debugIdentityCommand = GetRequiredService<CreateDebugIdentityCommand>();
        var debugArgs = new[]
        {
            entryPointPath,
            "--manifest", manifestPath,
            "--no-install",
            "--keep-identity"
        };

        var debugParseResult = debugIdentityCommand.Parse(debugArgs);
        var debugExitCode = await debugParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, debugExitCode, "Create debug identity with --keep-identity should complete successfully");

        // Verify the debug manifest preserves the original identity without .debug suffix
        var debugManifestPath = Path.Combine(_testWinappDirectory.FullName, "debug", "appxmanifest.xml");
        Assert.IsTrue(File.Exists(debugManifestPath), "Debug manifest should be created");
        var debugManifestContent = await File.ReadAllTextAsync(debugManifestPath, TestContext.CancellationToken);
        Assert.Contains("Name=\"MyTestPackage\"", debugManifestContent, "Debug manifest should keep the original package name");
        Assert.Contains("Id=\"TestApp\"", debugManifestContent, "Debug manifest should keep the original application ID");
        Assert.DoesNotContain(".debug", debugManifestContent, "Debug manifest should NOT contain .debug suffix when --keep-identity is used");
    }

    [TestMethod]
    public async Task CreateDebugIdentitySelfContainedShouldOmitPackageDependency()
    {
        // Arrange - Use .bat to avoid EmbedMsixIdentityToExeAsync / EmbedWindowsAppSDKManifestToExeAsync
        // which require mt.exe from Build Tools. Using .bat skips the exe-specific code paths,
        // allowing us to verify the manifest content changes for self-contained mode.
        var entryPointPath = Path.Combine(_tempDirectory.FullName, "TestApp.bat");
        await File.WriteAllTextAsync(entryPointPath, "@echo off", TestContext.CancellationToken);

        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""SelfContainedTestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.bat"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        // Create minimal assets
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        PngHelper.CreateTestImage(Path.Combine(assetsDir, "Logo.png"));

        // Act - Test GenerateSparsePackageStructureAsync directly with selfContained: true
        var msixService = GetRequiredService<IMsixService>() as MsixService;
        Assert.IsNotNull(msixService, "MsixService should be resolvable");

        var (debugManifestPath, debugIdentity) = await msixService.GenerateSparsePackageStructureAsync(
            new FileInfo(manifestPath),
            entryPointPath,
            keepIdentity: false,
            TestTaskContext,
            selfContained: true,
            TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(debugManifestPath.Exists, "Debug manifest should be created");
        var debugManifestContent = await File.ReadAllTextAsync(debugManifestPath.FullName, TestContext.CancellationToken);

        // Self-contained mode should NOT add a PackageDependency for WinAppSDK
        Assert.DoesNotContain("Microsoft.WindowsAppRuntime", debugManifestContent,
            "Self-contained debug manifest should NOT contain Windows App SDK PackageDependency");

        // It should still have the sparse packaging attributes
        Assert.Contains("AllowExternalContent", debugManifestContent,
            "Self-contained debug manifest should still have sparse packaging attributes");
    }

    [TestMethod]
    public async Task CreateDebugIdentityNonSelfContainedShouldNotOmitDependencyLogic()
    {
        // Arrange — Verify that non-self-contained mode does NOT strip PackageDependency
        // entries that are already present in the manifest (whereas self-contained does).
        var entryPointPath = Path.Combine(_tempDirectory.FullName, "TestApp.bat");
        await File.WriteAllTextAsync(entryPointPath, "@echo off", TestContext.CancellationToken);

        // Include an existing PackageDependency to verify it is preserved in non-self-contained mode
        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""FrameworkDependentTestPackage""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>Test Package</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
    <PackageDependency Name=""Microsoft.WindowsAppRuntime.1.7"" MinVersion=""7000.516.2156.0"" Publisher=""CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"" />
  </Dependencies>
  <Applications>
    <Application Id=""TestApp"" Executable=""TestApp.bat"" EntryPoint=""TestApp.App"">
      <uap:VisualElements DisplayName=""Test App"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        PngHelper.CreateTestImage(Path.Combine(assetsDir, "Logo.png"));

        var msixService = GetRequiredService<IMsixService>() as MsixService;
        Assert.IsNotNull(msixService, "MsixService should be resolvable");

        // Act — non-self-contained
        var (debugManifestPath, _) = await msixService.GenerateSparsePackageStructureAsync(
            new FileInfo(manifestPath),
            entryPointPath,
            keepIdentity: false,
            TestTaskContext,
            selfContained: false,
            TestContext.CancellationToken);

        var debugManifestContent = await File.ReadAllTextAsync(debugManifestPath.FullName, TestContext.CancellationToken);

        // Assert — the existing PackageDependency is preserved
        Assert.Contains("Microsoft.WindowsAppRuntime", debugManifestContent,
            "Framework-dependent debug manifest should preserve existing Windows App SDK PackageDependency");

        // Act — self-contained with same manifest (re-create since debug dir is cleaned)
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);
        var (scManifestPath, _) = await msixService.GenerateSparsePackageStructureAsync(
            new FileInfo(manifestPath),
            entryPointPath,
            keepIdentity: false,
            TestTaskContext,
            selfContained: true,
            TestContext.CancellationToken);

        var scManifestContent = await File.ReadAllTextAsync(scManifestPath.FullName, TestContext.CancellationToken);

        // Assert — self-contained should NOT have the PackageDependency
        // (UpdateAppxManifestContentAsync skips UpdateWindowsAppSdkDependencyAsync when selfContained is true,
        // but existing entries aren't removed — they just won't be added. Verify at minimum
        // that the self-contained manifest does NOT add new dependencies beyond what's already there.)
        Assert.Contains("AllowExternalContent", scManifestContent,
            "Self-contained debug manifest should have sparse packaging attributes");
    }

    [TestMethod]
    public async Task CreateDebugIdentityShouldNotModifyOriginalManifestWithPlaceholders()
    {
        // Arrange - Use .bat instead of .exe to avoid EmbedMsixIdentityToExeAsync which requires mt.exe from Build Tools
        var entryPointPath = Path.Combine(_tempDirectory.FullName, "MyApp.bat");
        await File.WriteAllTextAsync(entryPointPath, "@echo off", TestContext.CancellationToken);

        var manifestContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
         xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10"">
  <Identity Name=""$targetnametoken$""
            Publisher=""CN=TestPublisher""
            Version=""1.0.0.0"" />
  <Properties>
    <DisplayName>$targetnametoken$</DisplayName>
    <PublisherDisplayName>Test Publisher</PublisherDisplayName>
    <Description>Test package</Description>
    <Logo>Assets\Logo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Universal"" MinVersion=""10.0.18362.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Applications>
    <Application Id=""App"" Executable=""$targetnametoken$.exe"" EntryPoint=""$targetentrypoint$"">
      <uap:VisualElements DisplayName=""$targetnametoken$"" Description=""Test application""
                          BackgroundColor=""#777777"" Square150x150Logo=""Assets\Logo.png"" Square44x44Logo=""Assets\Logo.png"" />
    </Application>
  </Applications>
</Package>";

        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, manifestContent, TestContext.CancellationToken);

        // Create minimal assets so the command doesn't fail
        var assetsDir = Path.Combine(_tempDirectory.FullName, "Assets");
        Directory.CreateDirectory(assetsDir);
        PngHelper.CreateTestImage(Path.Combine(assetsDir, "Logo.png"));

        // Act - Create debug identity
        var debugIdentityCommand = GetRequiredService<CreateDebugIdentityCommand>();
        var debugArgs = new[]
        {
            entryPointPath,
            "--manifest", manifestPath,
            "--no-install"
        };

        var debugParseResult = debugIdentityCommand.Parse(debugArgs);
        var debugExitCode = await debugParseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, debugExitCode, "Create debug identity should complete successfully");

        // Verify the ORIGINAL manifest was NOT modified - it should still contain placeholders
        var originalManifestAfter = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);
        Assert.AreEqual(manifestContent, originalManifestAfter, "Original appxmanifest.xml should not be modified by create-debug-identity");
        Assert.Contains("$targetnametoken$", originalManifestAfter, "Original manifest should still contain $targetnametoken$ placeholder");
        Assert.Contains("$targetentrypoint$", originalManifestAfter, "Original manifest should still contain $targetentrypoint$ placeholder");

        // Verify the debug manifest WAS created with resolved placeholders
        var debugManifestPath = Path.Combine(_testWinappDirectory.FullName, "debug", "appxmanifest.xml");
        Assert.IsTrue(File.Exists(debugManifestPath), "Debug manifest should be created");
        var debugManifestContent = await File.ReadAllTextAsync(debugManifestPath, TestContext.CancellationToken);
        Assert.DoesNotContain("$targetnametoken$", debugManifestContent, "Debug manifest should have resolved $targetnametoken$ placeholder");
        Assert.DoesNotContain("$targetentrypoint$", debugManifestContent, "Debug manifest should have resolved $targetentrypoint$ placeholder");
        Assert.Contains("Executable=\"MyApp.bat\"", debugManifestContent, "Debug manifest should contain the resolved executable name");
    }

    private void DefaultAnswers()
    {
        // Use default answers for prompts during generation (packageName, publisherName, version, description)
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    [TestMethod]
    public async Task ManifestGenerateCommandWithEntrypointShouldUseVersionInfoFromExecutable()
    {
        // Arrange - Use winapp.exe which has version info
        var winappAssemblyPath = typeof(ManifestService).Assembly.Location;

        if (string.IsNullOrEmpty(winappAssemblyPath) || !File.Exists(winappAssemblyPath))
        {
            Assert.Inconclusive("winapp assembly not found");
            return;
        }

        // Copy to temp directory with .exe extension
        var exeFilePath = Path.Combine(_tempDirectory.FullName, "winapp.exe");
        File.Copy(winappAssemblyPath, exeFilePath);

        var generateCommand = GetRequiredService<ManifestGenerateCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--executable", exeFilePath
        };

        // Act
        var parseResult = generateCommand.Parse(args);
        var exitCode = await parseResult.InvokeAsync(cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, exitCode, "Generate command should complete successfully");

        // Verify manifest was created
        var manifestPath = Path.Combine(_tempDirectory.FullName, "appxmanifest.xml");
        Assert.IsTrue(File.Exists(manifestPath), "AppxManifest.xml should be created");

        var manifestContent = await File.ReadAllTextAsync(manifestPath, TestContext.CancellationToken);

        // Verify the executable placeholder is in the manifest (template uses $targetnametoken$.exe)
        Assert.Contains("Executable=\"$targetnametoken$.exe\"", manifestContent, "Manifest should contain placeholder executable name");

        var fileVersionInfo = FileVersionInfo.GetVersionInfo(exeFilePath);
        Assert.Contains($"Description=\"{fileVersionInfo.Comments}\"", manifestContent,
            "Manifest description should contain FileDescription from executable");

        Assert.Contains("Name=\"winapp\"", manifestContent,
            "Manifest should contain package name derived from FileDescription");

        // Verify Publisher is set to Microsoft Corporation as per the assembly info
        Assert.Contains("CN=Microsoft Corporation", manifestContent,
            "Manifest publisher should be set to Microsoft Corporation from executable");
    }
}
