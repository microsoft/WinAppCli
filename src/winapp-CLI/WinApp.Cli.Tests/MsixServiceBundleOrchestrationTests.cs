// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
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

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, CancellationToken cancellationToken = default)
    {
        Invocations.Add((tool.ExecutableName, arguments));

        var match = OutputPathRegex.Match(arguments);
        if (match.Success)
        {
            var outputPath = NormalizeLongPath(match.Groups["path"].Value);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, $"fake {tool.ExecutableName} output");
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

    private DirectoryInfo CreateSliceFolder(string folderName, ushort machineType, string manifestArchitecture)
    {
        var folder = _tempDirectory.CreateSubdirectory(folderName);
        File.WriteAllBytes(Path.Combine(folder.FullName, "TestApp.exe"), BuildMinimalNativePe(machineType));
        File.WriteAllBytes(Path.Combine(folder.FullName, "logo.png"), []);
        File.WriteAllText(Path.Combine(folder.FullName, "Package.appxmanifest"), CreateManifestContent(manifestArchitecture));
        return folder;
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
}
