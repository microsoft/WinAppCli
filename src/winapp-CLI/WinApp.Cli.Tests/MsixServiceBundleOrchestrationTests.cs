// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Tests;

internal sealed class FakeBundleService : IBundleService
{
    public int CallCount { get; private set; }
    public IReadOnlyList<FileInfo>? LastMsixFiles { get; private set; }
    public FileInfo? LastOutput { get; private set; }

    public Task CreateBundleAsync(IReadOnlyList<FileInfo> msixFiles, FileInfo output, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastMsixFiles = msixFiles.ToList();
        LastOutput = output;

        output.Directory?.Create();
        File.WriteAllText(output.FullName, "fake bundle");

        return Task.CompletedTask;
    }
}

internal sealed class FakeBundleValidationService : IBundleValidationService
{
    public int CallCount { get; private set; }
    public IReadOnlyList<AppxManifestDocument>? LastSliceManifests { get; private set; }
    public IReadOnlyList<string>? LastDetectedArchitectures { get; private set; }
    public IReadOnlyList<DirectoryInfo>? LastInputFolders { get; private set; }
    public IReadOnlyList<BundleValidationError> ErrorsToReturn { get; set; } = [];

    public IReadOnlyList<BundleValidationError> Validate(
        IReadOnlyList<AppxManifestDocument> sliceManifests,
        IReadOnlyList<string> detectedArchitectures,
        IReadOnlyList<DirectoryInfo> inputFolders)
    {
        CallCount++;
        LastSliceManifests = sliceManifests.ToList();
        LastDetectedArchitectures = detectedArchitectures.ToList();
        LastInputFolders = inputFolders.ToList();
        return ErrorsToReturn;
    }
}

internal sealed class FakeBuildToolsService : IBuildToolsService
{
    private static readonly Regex OutputPathRegex = new("(?:^|\\s)/p\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public List<(string ToolName, string Arguments)> Invocations { get; } = [];

    public FileInfo? GetBuildToolPath(string toolName)
    {
        return new FileInfo(Path.Combine(Path.GetTempPath(), toolName));
    }

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new FileInfo(Path.Combine(Path.GetTempPath(), toolName)));
    }

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<DirectoryInfo?>(null);
    }

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        Invocations.Add((tool.ExecutableName, arguments));

        // Only create fake output files for makeappx (not signtool or other tools)
        if (tool.ExecutableName.Contains("makeappx", StringComparison.OrdinalIgnoreCase))
        {
            var match = OutputPathRegex.Match(arguments);
            if (match.Success)
            {
                var outputPath = NormalizeLongPath(match.Groups["path"].Value);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, $"fake {tool.ExecutableName} output");
            }
        }

        return Task.FromResult<(string stdout, string stderr)>((string.Empty, string.Empty));
    }

    private static string NormalizeLongPath(string path)
    {
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}

[TestClass]
public class MsixServiceBundleOrchestrationTests : BaseCommandTests
{
    private static readonly string[] ExpectedArchitectures = ["x64", "arm64"];
    private static readonly string[] ExpectedPackageNames = ["TestApp", "TestApp"];

    private MsixService _msixService = null!;
    private FakeBundleService _fakeBundleService = null!;
    private FakeBundleValidationService _fakeBundleValidationService = null!;
    private FakeBuildToolsService _fakeBuildToolsService = null!;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeBundleService = new FakeBundleService();
        _fakeBundleValidationService = new FakeBundleValidationService();
        _fakeBuildToolsService = new FakeBuildToolsService();

        return services
            .AddSingleton<IBundleService>(_fakeBundleService)
            .AddSingleton<IBundleValidationService>(_fakeBundleValidationService)
            .AddSingleton<IBuildToolsService>(_fakeBuildToolsService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    [TestInitialize]
    public void SetupService()
    {
        _msixService = (MsixService)GetRequiredService<IMsixService>();
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_NoExecutableInFolder_ThrowsWithClearError()
    {
        // Arrange - folder with no .exe
        var folder = _tempDirectory.CreateSubdirectory("no-exe-folder");
        File.WriteAllBytes(Path.Combine(folder.FullName, "logo.png"), []);
        File.WriteAllText(Path.Combine(folder.FullName, "Package.appxmanifest"), CreateManifestContent("x64"));

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.CreateMsixBundleAsync(
                [folder],
                outputPath: null,
                TestTaskContext,
                skipPri: true,
                cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "no executable found");
        StringAssert.Contains(ex.Message, folder.FullName);
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_NoManifestInFolder_ThrowsFileNotFoundException()
    {
        // Arrange - folder with exe but no manifest
        var folder = _tempDirectory.CreateSubdirectory("no-manifest-folder");
        File.WriteAllBytes(Path.Combine(folder.FullName, "TestApp.exe"), BuildMinimalNativePe(0x8664));

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            _msixService.CreateMsixBundleAsync(
                [folder],
                outputPath: null,
                TestTaskContext,
                skipPri: true,
                cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Manifest file not found");
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_RuntimeToolExeIsSkippedInAutoDetection_UsesRealExe()
    {
        // Arrange - folder with createdump.exe (runtime tool) and the real app exe
        var folder = _tempDirectory.CreateSubdirectory("runtime-tool-folder");
        // Put createdump.exe first alphabetically (c < t) to verify it's skipped
        File.WriteAllBytes(Path.Combine(folder.FullName, "createdump.exe"), BuildMinimalNativePe(0x8664));
        File.WriteAllBytes(Path.Combine(folder.FullName, "TestApp.exe"), BuildMinimalNativePe(0x8664));
        File.WriteAllBytes(Path.Combine(folder.FullName, "logo.png"), []);
        File.WriteAllText(Path.Combine(folder.FullName, "Package.appxmanifest"), CreateManifestContent("x64"));

        var arm64Folder = CreateSliceFolder("slice-arm64", 0xAA64, "arm64");

        // Act - should succeed because manifest specifies executable, so runtime tool filter 
        // only affects the "last resort" fallback
        var result = await _msixService.CreateMsixBundleAsync(
            [folder, arm64Folder],
            outputPath: null,
            TestTaskContext,
            skipPri: true,
            cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Slices.Count);
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_OnlyRuntimeToolExeInFolder_ThrowsNoExecutableFound()
    {
        // Arrange - folder with ONLY runtime tool executables (no manifest exe reference)
        var folder = _tempDirectory.CreateSubdirectory("only-runtime-tools");
        File.WriteAllBytes(Path.Combine(folder.FullName, "createdump.exe"), BuildMinimalNativePe(0x8664));
        File.WriteAllBytes(Path.Combine(folder.FullName, "logo.png"), []);
        // Manifest does NOT reference an executable that exists
        File.WriteAllText(Path.Combine(folder.FullName, "Package.appxmanifest"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Identity Name="TestApp" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="x64" />
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
              </Dependencies>
              <Capabilities>
                <Capability Name="internetClient" />
              </Capabilities>
              <Applications>
                <Application Id="App" Executable="MissingApp.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements DisplayName="TestApp" Description="Test" Square150x150Logo="logo.png" Square44x44Logo="logo.png" BackgroundColor="transparent" />
                </Application>
              </Applications>
            </Package>
            """);

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.CreateMsixBundleAsync(
                [folder],
                outputPath: null,
                TestTaskContext,
                skipPri: true,
                cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "no executable found");
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_ValidationFails_ThrowsWithValidationErrors()
    {
        // Arrange
        var x64Folder = CreateSliceFolder("slice-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("slice-arm64", 0xAA64, "arm64");

        _fakeBundleValidationService.ErrorsToReturn =
        [
            new BundleValidationError(
                "Identity/@Version",
                "Identity/@Version differs across slices. All slices in a bundle must have the same Identity/@Version.",
                [
                    $"{x64Folder.Name}: \"1.0.0.0\"",
                    $"{arm64Folder.Name}: \"2.0.0.0\""
                ])
        ];

        // Act
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.CreateMsixBundleAsync(
                [x64Folder, arm64Folder],
                outputPath: null,
                TestTaskContext,
                skipPri: true,
                cancellationToken: TestContext.CancellationToken));

        // Assert
        StringAssert.Contains(ex.Message, "Bundle validation failed. The following inconsistencies were found across slices:");
        StringAssert.Contains(ex.Message, "Identity/@Version: Identity/@Version differs across slices. All slices in a bundle must have the same Identity/@Version.");
        StringAssert.Contains(ex.Message, $"• {x64Folder.Name}: \"1.0.0.0\"");
        StringAssert.Contains(ex.Message, $"• {arm64Folder.Name}: \"2.0.0.0\"");
        Assert.AreEqual(1, _fakeBundleValidationService.CallCount);
        Assert.AreEqual(0, _fakeBundleService.CallCount);
        Assert.AreEqual(0, _fakeBuildToolsService.Invocations.Count);
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_MultipleFolders_InvokesValidationWithCorrectArchitectures()
    {
        // Arrange
        var x64Folder = CreateSliceFolder("slice-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("slice-arm64", 0xAA64, "arm64");

        // Act
        var result = await _msixService.CreateMsixBundleAsync(
            [x64Folder, arm64Folder],
            outputPath: null,
            TestTaskContext,
            skipPri: true,
            cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, _fakeBundleValidationService.CallCount);
        CollectionAssert.AreEqual(ExpectedArchitectures, _fakeBundleValidationService.LastDetectedArchitectures!.ToArray());
        CollectionAssert.AreEqual(new[] { x64Folder.FullName, arm64Folder.FullName }, _fakeBundleValidationService.LastInputFolders!.Select(folder => folder.FullName).ToArray());
        CollectionAssert.AreEqual(ExpectedArchitectures, _fakeBundleValidationService.LastSliceManifests!.Select(manifest => manifest.IdentityProcessorArchitecture).ToArray());
        CollectionAssert.AreEqual(ExpectedPackageNames, _fakeBundleValidationService.LastSliceManifests!.Select(manifest => manifest.IdentityName).ToArray());
        Assert.AreEqual(2, result.Slices.Count);
        Assert.AreEqual(2, _fakeBuildToolsService.Invocations.Count);
        Assert.AreEqual(1, _fakeBundleService.CallCount);
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_OutputNaming_UsesIdentityFromManifest()
    {
        // Arrange
        var x64Folder = CreateSliceFolder("slice-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("slice-arm64", 0xAA64, "arm64");
        var outputDirectory = _tempDirectory.CreateSubdirectory("bundle-output");

        // Act
        var result = await _msixService.CreateMsixBundleAsync(
            [x64Folder, arm64Folder],
            outputDirectory,
            TestTaskContext,
            skipPri: true,
            cancellationToken: TestContext.CancellationToken);

        // Assert
        var expectedPath = Path.Combine(outputDirectory.FullName, "TestApp_1.0.0.0_arm64_x64.msixbundle");
        Assert.AreEqual(expectedPath, result.BundlePath.FullName);
        Assert.AreEqual(expectedPath, _fakeBundleService.LastOutput!.FullName);
        Assert.IsTrue(result.BundlePath.Exists);
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_OutputPathWithMsixBundleExtension_UsesExplicitBundleFilePath()
    {
        // Arrange
        var x64Folder = CreateSliceFolder("slice-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("slice-arm64", 0xAA64, "arm64");
        var explicitOutputPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "custom-output.msixbundle"));

        // Act
        var result = await _msixService.CreateMsixBundleAsync(
            [x64Folder, arm64Folder],
            explicitOutputPath,
            TestTaskContext,
            skipPri: true,
            cancellationToken: TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(explicitOutputPath.FullName, result.BundlePath.FullName);
        Assert.AreEqual(explicitOutputPath.FullName, _fakeBundleService.LastOutput!.FullName);
        Assert.IsTrue(result.BundlePath.Exists);
    }

    [TestMethod]
    public async Task PackageCommand_DuplicateInputFolders_ReturnsNonZeroExitCode()
    {
        // Arrange
        var folder = CreateSliceFolder("dupe-folder", 0x8664, "x64");
        var packageCommand = GetRequiredService<PackageCommand>();
        var args = new[] { folder.FullName, folder.FullName, "--skip-pri" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, args);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Should fail for duplicate input folders");
        var output = ConsoleStdErr!.ToString();
        StringAssert.Contains(output, "Duplicate input folder");
    }

    [TestMethod]
    public async Task PackageCommand_BundleWithMsixExtension_ReturnsNonZeroExitCode()
    {
        // Arrange - two folders + .msix output (should require .msixbundle)
        var x64Folder = CreateSliceFolder("ext-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("ext-arm64", 0xAA64, "arm64");
        var packageCommand = GetRequiredService<PackageCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "output.msix");
        var args = new[] { x64Folder.FullName, arm64Folder.FullName, "--output", outputPath, "--skip-pri" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, args);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Should fail for .msix extension with bundle");
        var output = ConsoleStdErr!.ToString();
        StringAssert.Contains(output, "Cannot use .msix extension");
    }

    [TestMethod]
    public async Task PackageCommand_SingleFolderWithMsixBundleExtension_ReturnsNonZeroExitCode()
    {
        // Arrange - one folder + .msixbundle output (should require .msix)
        var folder = CreateSliceFolder("single-folder", 0x8664, "x64");
        var packageCommand = GetRequiredService<PackageCommand>();
        var outputPath = Path.Combine(_tempDirectory.FullName, "output.msixbundle");
        var args = new[] { folder.FullName, "--output", outputPath, "--skip-pri" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, args);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Should fail for .msixbundle extension with single package");
        var output = ConsoleStdErr!.ToString();
        StringAssert.Contains(output, "Cannot use .msixbundle extension");
    }

    private DirectoryInfo CreateSliceFolder(string folderName, ushort machineType, string manifestArchitecture)
    {
        var folder = _tempDirectory.CreateSubdirectory(folderName);
        File.WriteAllBytes(Path.Combine(folder.FullName, "TestApp.exe"), BuildMinimalNativePe(machineType));
        File.WriteAllBytes(Path.Combine(folder.FullName, "logo.png"), []);
        File.WriteAllText(Path.Combine(folder.FullName, "Package.appxmanifest"), CreateManifestContent(manifestArchitecture));
        return folder;
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_NeutralArchWithSelfContained_ThrowsWithClearError()
    {
        // Arrange - create folders with neutral PE (AnyCPU managed IL)
        var neutralFolder = _tempDirectory.CreateSubdirectory("neutral-slice");
        File.WriteAllBytes(Path.Combine(neutralFolder.FullName, "TestApp.exe"), BuildManagedAnyCpuPe());
        File.WriteAllBytes(Path.Combine(neutralFolder.FullName, "logo.png"), []);
        File.WriteAllText(Path.Combine(neutralFolder.FullName, "Package.appxmanifest"), CreateManifestContent("neutral"));

        var x64Folder = CreateSliceFolder("slice-x64-sc", 0x8664, "x64");

        // Act & Assert
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _msixService.CreateMsixBundleAsync(
                [neutralFolder, x64Folder],
                outputPath: null,
                TestTaskContext,
                skipPri: true,
                selfContained: true,
                cancellationToken: TestContext.CancellationToken));

        StringAssert.Contains(ex.Message, "Cannot use --self-contained with architecture-neutral slices");
    }

    [TestMethod]
    public async Task CreateMsixBundleAsync_WithAutoSign_InvokesBundleServiceThenSigns()
    {
        // Arrange
        var x64Folder = CreateSliceFolder("sign-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("sign-arm64", 0xAA64, "arm64");
        var certPath = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.pfx"));
        const string password = "password";

        // Generate a real self-signed certificate matching the test manifest publisher
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=TestPublisher", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(1));
        var pfxBytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password);
        File.WriteAllBytes(certPath.FullName, pfxBytes);

        // Act
        var result = await _msixService.CreateMsixBundleAsync(
            [x64Folder, arm64Folder],
            outputPath: null,
            TestTaskContext,
            skipPri: true,
            autoSign: true,
            certificatePath: certPath,
            certificatePassword: password,
            cancellationToken: TestContext.CancellationToken);

        // Assert - bundle was created
        Assert.IsNotNull(result);
        Assert.IsTrue(result.Signed);
        Assert.AreEqual(1, _fakeBundleService.CallCount);
        // Signing invokes signtool
        Assert.IsTrue(_fakeBuildToolsService.Invocations.Any(i =>
            i.ToolName.Contains("signtool", StringComparison.OrdinalIgnoreCase)),
            "Expected signtool invocation for bundle signing");
    }

    [TestMethod]
    public async Task PackageCommand_MultipleFolders_SuccessfullyCreatesBundleViaCommand()
    {
        // Arrange - two valid folders, invoke through PackageCommand
        var x64Folder = CreateSliceFolder("cmd-x64", 0x8664, "x64");
        var arm64Folder = CreateSliceFolder("cmd-arm64", 0xAA64, "arm64");
        var packageCommand = GetRequiredService<PackageCommand>();
        var args = new[] { x64Folder.FullName, arm64Folder.FullName, "--skip-pri" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(packageCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, $"Expected success. Stderr: {ConsoleStdErr}");
        Assert.AreEqual(1, _fakeBundleService.CallCount, "Bundle service should be called exactly once");
        Assert.AreEqual(2, _fakeBundleService.LastMsixFiles!.Count, "Should produce two intermediate MSIX files");
    }

    private static string CreateManifestContent(string processorArchitecture)
    {
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
              <Identity Name="TestApp" Publisher="CN=TestPublisher" Version="1.0.0.0" ProcessorArchitecture="{{processorArchitecture}}" />
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
              </Dependencies>
              <Capabilities>
                <Capability Name="internetClient" />
              </Capabilities>
              <Applications>
                <Application Id="App" Executable="TestApp.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements DisplayName="TestApp" Description="Test" Square150x150Logo="logo.png" Square44x44Logo="logo.png" BackgroundColor="transparent" />
                </Application>
              </Applications>
            </Package>
            """;
    }

    private static byte[] BuildMinimalNativePe(ushort machineType)
    {
        bool is64Bit = machineType is 0x8664 or 0xAA64;
        ushort optionalHeaderSize = is64Bit ? (ushort)0xF0 : (ushort)0xE0;
        int coffHeaderOffset = 0x84;
        var peBytes = new byte[coffHeaderOffset + 20 + optionalHeaderSize + 64];

        peBytes[0] = 0x4D;
        peBytes[1] = 0x5A;
        BitConverter.GetBytes(0x80).CopyTo(peBytes, 0x3C);
        peBytes[0x80] = 0x50;
        peBytes[0x81] = 0x45;

        BitConverter.GetBytes(machineType).CopyTo(peBytes, coffHeaderOffset);
        BitConverter.GetBytes(optionalHeaderSize).CopyTo(peBytes, coffHeaderOffset + 16);
        peBytes[coffHeaderOffset + 18] = 0x02;

        ushort optionalHeaderMagic = is64Bit ? (ushort)0x20B : (ushort)0x10B;
        BitConverter.GetBytes(optionalHeaderMagic).CopyTo(peBytes, coffHeaderOffset + 20);

        return peBytes;
    }

    /// <summary>
    /// Builds a minimal managed AnyCPU PE that PeHelper.DetectPeArchitecture returns "neutral" for.
    /// Uses the test assembly itself as a source of a valid IL-only binary.
    /// </summary>
    private static byte[] BuildManagedAnyCpuPe()
    {
        // Use the test assembly's DLL as source — it's a valid IL-only managed assembly.
        // Read it and return its bytes. The test project targets AnyCPU (arm64 runtime but IL-only).
        var assemblyPath = typeof(MsixServiceBundleOrchestrationTests).Assembly.Location;
        return File.ReadAllBytes(assemblyPath);
    }
}
