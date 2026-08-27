// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class UnregisterCommandTests : BaseCommandTests
{
    private FakePackageRegistrationService _fakePackageRegistrationService = null!;
    private FakeProjectRunService _fakeProjectRunService = null!;

    private const string TestManifestContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                 IgnorableNamespaces="uap rescap">
          <Identity Name="TestPackage"
                    Publisher="CN=TestPublisher"
                    Version="1.0.0.0" />
          <Properties>
            <DisplayName>Test Package</DisplayName>
            <PublisherDisplayName>Test Publisher</PublisherDisplayName>
            <Description>Test package</Description>
            <Logo>Assets\Logo.png</Logo>
          </Properties>
          <Dependencies>
            <TargetDeviceFamily Name="Windows.Universal" MinVersion="10.0.18362.0" MaxVersionTested="10.0.26100.0" />
          </Dependencies>
          <Applications>
            <Application Id="TestApp" Executable="TestApp.exe" EntryPoint="TestApp.App">
              <uap:VisualElements DisplayName="Test App" Description="Test application"
                                  BackgroundColor="#777777" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\Logo.png" />
            </Application>
          </Applications>
          <Capabilities>
            <rescap:Capability Name="runFullTrust" />
          </Capabilities>
        </Package>
        """;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakePackageRegistrationService = new FakePackageRegistrationService();
        _fakeProjectRunService = new FakeProjectRunService();
        return services
            .AddSingleton<IPackageRegistrationService>(_fakePackageRegistrationService)
            .AddSingleton<IProjectRunService>(_fakeProjectRunService);
    }

    private async Task<FileInfo> CreateTestManifestAsync(string? directory = null)
    {
        directory ??= _tempDirectory.FullName;
        var manifestPath = Path.Combine(directory, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, TestManifestContent, TestContext.CancellationToken);
        return new FileInfo(manifestPath);
    }

    [TestMethod]
    public async Task UnregisterCommand_WithManifest_UnregistersDevPackages()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage"));
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "TestPackage"));
    }

    [TestMethod]
    public async Task UnregisterCommand_ChecksBothNameAndDebugVariant()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();
        _fakePackageRegistrationService.FakeDevPackages = [];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage"));
        Assert.IsTrue(_fakePackageRegistrationService.FindDevPackagesCalls.Contains("TestPackage.debug"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SkipsNonDevModePackages()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: false)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SkipsPackagesFromDifferentTree()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_ExplicitManifestOutsideCurrentDirectory_UnregistersFromManifestTree()
    {
        // A file-based app's generated manifest lives in the SDK's runfile output under
        // %LOCALAPPDATA%\Temp, never beneath the directory the user runs from, so the guard has to
        // be scoped to the manifest the caller named rather than the current directory.
        var layoutRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("bin_debug");
        var manifest = await CreateTestManifestAsync(layoutRoot.FullName);
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                Path.Combine(layoutRoot.FullName, "AppX"), IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act — no --force, and the working directory is _tempDirectory, not the layout
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "TestPackage"));
    }

    [TestMethod]
    public async Task UnregisterCommand_ExplicitManifest_StillSkipsPackageRegisteredElsewhere()
    {
        // Scoping to the manifest must not weaken the guard: a package registered from an unrelated
        // tree is still refused even though the manifest itself is outside the working directory.
        var layoutRoot = _tempDirectory.CreateSubdirectory("elsewhere");
        var manifest = await CreateTestManifestAsync(layoutRoot.FullName);
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName]);

        // Assert
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task UnregisterCommand_WithForce_SkipsLocationCheck()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                @"C:\OtherProject\bin\Debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--force"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "TestPackage"));
    }

    [TestMethod]
    public async Task UnregisterCommand_WithJson_ReturnsJsonOutput()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<UnregisterCommand>();

        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("TestPackage_1.0.0.0_x64__abc123", "TestPackage", "1.0.0.0",
                _tempDirectory.FullName, IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--manifest", manifest.FullName, "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("TestPackage_1.0.0.0_x64__abc123"));
    }

    [TestMethod]
    public async Task UnregisterCommand_NoManifest_ReturnsError()
    {
        // Arrange — empty temp directory with no manifest
        var emptyDir = new DirectoryInfo(Path.Combine(_tempDirectory.FullName, "empty"));
        emptyDir.Create();
        var command = GetRequiredService<UnregisterCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        // Assert
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task UnregisterCommand_NoManifest_WithJson_EmitsJsonError()
    {
        // No manifest in the current directory + --json should emit a structured JSON error
        // (rather than a plain log line) and still fail.
        TestAnsiConsole.Profile.Width = 1000; // avoid line-wrapping that would corrupt the JSON
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(1, exitCode);
        var output = TestAnsiConsole.Output.Trim();
        var root = System.Text.Json.JsonDocument.Parse(output).RootElement;
        Assert.IsTrue(root.TryGetProperty("Error", out var error), "JSON output should carry an Error property");
        StringAssert.Contains(error.GetString(), "No manifest found");
    }

    #region Single-file apps

    private FileInfo CreateSingleFile(string name = "counter.cs")
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        File.WriteAllText(path, "Console.WriteLine(\"hi\");");
        return new FileInfo(path);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_UnregistersInferredIdentity()
    {
        // `winapp unregister counter.cs` must remove what `winapp run counter.cs` registered, without a
        // manifest path: the generated manifest lives in the SDK's temp output, which the user never sees.
        var singleFile = CreateSingleFile();
        var buildRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("counter-abc123");
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, buildRoot.FullName);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                Path.Combine(buildRoot.FullName, "bin", "debug_win-x64", "AppX"), IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "counter"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_SkipsPackageRegisteredFromAnotherFile()
    {
        // Two counter.cs files in different folders share the default identity, so the guard has to
        // confirm the registration came from THIS file's build root before removing it.
        var singleFile = CreateSingleFile();
        var buildRoot = _tempDirectory.CreateSubdirectory("runfile").CreateSubdirectory("counter-mine");
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, buildRoot.FullName);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-theirs\bin\debug\AppX", IsDevelopmentMode: true)
        ];

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_WithForce_RemovesRegardlessOfLocation()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, @"C:\Temp\runfile\counter-mine");
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-theirs\bin\debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName, "--force"]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "counter"));
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_UnresolvedBuildRoot_StillUnregistersByIdentity()
    {
        // A null build root means the guard has nothing to compare against. Falling back to identity
        // alone beats refusing to remove a package the user explicitly named by its own source file.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Packaged, BuildRootDirectory: null);
        _fakePackageRegistrationService.FakeDevPackages =
        [
            new DevPackageInfo("counter_1.0.0.0_x64__abc", "counter", "1.0.0.0",
                @"C:\Temp\dotnet\runfile\counter-abc\bin\debug\AppX", IsDevelopmentMode: true)
        ];
        _fakePackageRegistrationService.FakeUnregisterResult = true;

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakePackageRegistrationService.UnregisterCalls.Any(c => c.PackageName == "counter"));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnregisterCommand_SingleFile_Unpackaged_ReportsNothingToRemove()
    {
        // An unpackaged app never registered, so "no dev-registered package found" would read as a
        // failure to find something that ought to exist.
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentity =
            new SingleFileIdentityResolution("counter", ProjectPackaging.Unpackaged, BuildRootDirectory: null);

        var (exitCode, ambientOutput) = await InvokeWithAmbientConsoleCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
        StringAssert.Contains(ambientOutput, "unpackaged app");
    }

    [TestMethod]
    public async Task UnregisterCommand_NonCsFileInput_IsRejectedWithGuidance()
    {
        var notAnApp = new FileInfo(Path.Combine(_tempDirectory.FullName, "Package.appxmanifest"));
        await File.WriteAllTextAsync(notAnApp.FullName, TestManifestContent, TestContext.CancellationToken);
        var command = GetRequiredService<UnregisterCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [notAnApp.FullName]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(ConsoleStdErr.ToString(), "--manifest");
        Assert.AreEqual(0, _fakeProjectRunService.ResolveSingleFileIdentityCalls.Count);
    }

    [TestMethod]
    public async Task UnregisterCommand_SingleFile_EvaluationFailure_ReportsError()
    {
        var singleFile = CreateSingleFile();
        var command = GetRequiredService<UnregisterCommand>();

        _fakeProjectRunService.SingleFileIdentityThrows =
            new ProjectRunException("Could not evaluate 'counter.cs' to determine its package identity.");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [singleFile.FullName]);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(0, _fakePackageRegistrationService.UnregisterCalls.Count);
    }

    #endregion
}
