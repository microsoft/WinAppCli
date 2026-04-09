// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class RunCommandTests : BaseCommandTests
{
    private FakeMsixService _fakeMsixService = null!;
    private FakeAppLauncherService _fakeAppLauncherService = null!;
    private FakeDebugOutputService _fakeDebugOutputService = null!;

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
        _fakeMsixService = new FakeMsixService();
        _fakeAppLauncherService = new FakeAppLauncherService();
        _fakeDebugOutputService = new FakeDebugOutputService();
        return services
            .AddSingleton<IMsixService>(_fakeMsixService)
            .AddSingleton<IAppLauncherService>(_fakeAppLauncherService)
            .AddSingleton<IDebugOutputService>(_fakeDebugOutputService)
            .AddSingleton<INugetService, FakeNugetService>();
    }

    private async Task<FileInfo> CreateTestManifestAsync(string? directory = null)
    {
        directory ??= _tempDirectory.FullName;
        var manifestPath = Path.Combine(directory, "appxmanifest.xml");
        await File.WriteAllTextAsync(manifestPath, TestManifestContent, TestContext.CancellationToken);
        return new FileInfo(manifestPath);
    }

    #region Option parsing tests

    [TestMethod]
    public void ParseOptions_NoLaunch_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.NoLaunchOption));
    }

    [TestMethod]
    public void ParseOptions_NoLaunchNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.NoLaunchOption));
    }

    [TestMethod]
    public void ParseOptions_InputFolder_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        var folder = parseResult.GetValue(RunCommand.InputFolderArgument);
        Assert.IsNotNull(folder);
        Assert.AreEqual(_tempDirectory.FullName, folder.FullName);
    }

    [TestMethod]
    public void ParseOptions_NoInputFolder_HasParseError()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([]);

        // Assert
        Assert.IsNotEmpty(parseResult.Errors, "Missing required input-folder should produce a parse error");
    }

    [TestMethod]
    public async Task ParseOptions_AllOptions_AreParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();
        var manifest = await CreateTestManifestAsync();
        var outputDir = Path.Combine(_tempDirectory.FullName, "output");
        var args = new[]
        {
            _tempDirectory.FullName,
            "--manifest", manifest.FullName,
            "--output-appx-directory", outputDir,
            "--args", "arg1 arg2",
            "--no-launch"
        };

        // Act
        var parseResult = command.Parse(args);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.NoLaunchOption));
        Assert.AreEqual("arg1 arg2", parseResult.GetValue(RunCommand.ArgsOption));
        var folder = parseResult.GetValue(RunCommand.InputFolderArgument);
        Assert.IsNotNull(folder);
        Assert.AreEqual(_tempDirectory.FullName, folder.FullName);
    }

    [TestMethod]
    public void ParseOptions_Clean_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--clean"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.CleanOption));
    }

    [TestMethod]
    public void ParseOptions_CleanNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.CleanOption));
    }

    #endregion

    #region Handler tests

    [TestMethod]
    public async Task RunCommand_WithNoLaunch_RegistersIdentityButDoesNotLaunch()
    {
        // Arrange - manifest in input folder
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.IsFalse(_fakeMsixService.AddLooseLayoutCalls[0].Clean, "Default run should preserve app data (clean=false)");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Application should NOT be launched with --no-launch");
    }

    [TestMethod]
    public async Task RunCommand_WithClean_PassesCleanThroughToMsixService()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--clean"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.IsTrue(_fakeMsixService.AddLooseLayoutCalls[0].Clean, "--clean should be passed through to MSIX service");
    }

    [TestMethod]
    public async Task RunCommand_WithoutClean_DefaultsToPreservingAppData()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.IsFalse(_fakeMsixService.AddLooseLayoutCalls[0].Clean, "Without --clean, app data should be preserved");
    }

    [TestMethod]
    public async Task RunCommand_WithNoLaunchAndManifest_RegistersIdentityButDoesNotLaunch()
    {
        // Arrange
        var manifest = await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--manifest", manifest.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "Application should NOT be launched with --no-launch");
    }

    [TestMethod]
    public async Task RunCommand_WithInputFolder_ResolvesManifestFromFolder()
    {
        // Arrange - manifest in a subfolder, not in cwd
        var subFolder = _tempDirectory.CreateSubdirectory("app-output");
        await CreateTestManifestAsync(subFolder.FullName);
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [subFolder.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        StringAssert.Contains(_fakeMsixService.AddLooseLayoutCalls[0].ManifestPath, subFolder.FullName,
            "Manifest should be resolved from the input folder");
    }

    [TestMethod]
    public async Task RunCommand_WithInputFolderAndManifest_UsesExplicitManifest()
    {
        // Arrange - manifest explicitly specified, different from folder
        var subFolder = _tempDirectory.CreateSubdirectory("app-output");
        var manifest = await CreateTestManifestAsync(subFolder.FullName);
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--manifest", manifest.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        StringAssert.Contains(_fakeMsixService.AddLooseLayoutCalls[0].ManifestPath, manifest.FullName,
            "Explicit --manifest should take priority");
    }

    [TestMethod]
    public async Task RunCommand_WithNoManifestAnywhere_ReturnsError()
    {
        // Arrange - no manifest in cwd or folder
        var emptyFolder = _tempDirectory.CreateSubdirectory("empty");
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [emptyFolder.FullName, "--no-launch"]);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Command should fail when no manifest is found");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    #endregion

    #region JSON output tests

    [TestMethod]
    public async Task RunCommand_WithJsonAndNoLaunch_OutputsJsonWithAumid()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var json = ParseJsonOutput();
        Assert.AreEqual("TestPackage_fakefamily!TestApp", json.GetProperty("AUMID").GetString());
        Assert.IsFalse(json.TryGetProperty("ProcessId", out _), "ProcessId should not be present in no-launch mode");
        Assert.IsFalse(json.TryGetProperty("Error", out _), "Error should not be present on success");
    }

    [TestMethod]
    public async Task RunCommand_WithJsonAndError_OutputsJsonWithErrorField()
    {
        // Arrange
        await CreateTestManifestAsync();
        _fakeMsixService.ExceptionToThrow = new InvalidOperationException("Test error message");
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        // Assert
        Assert.AreNotEqual(0, exitCode, "Command should fail");

        var json = ParseJsonOutput();
        Assert.AreEqual("Test error message", json.GetProperty("Error").GetString());
        Assert.IsFalse(json.TryGetProperty("AUMID", out _), "AUMID should not be present on error before identity is created");
        Assert.IsFalse(json.TryGetProperty("ProcessId", out _), "ProcessId should not be present on error");
    }

    [TestMethod]
    public async Task RunCommand_WithoutJsonFlag_DoesNotOutputJson()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("\"AUMID\""), "JSON fields should not appear without --json flag");
    }

    [TestMethod]
    public async Task RunCommand_WithJson_OutputsValidJsonDocument()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--no-launch", "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var output = TestAnsiConsole.Output;
        Assert.Contains("{\n", output, "JSON should use \\n line endings");
    }

    [TestMethod]
    public void ParseOptions_JsonOption_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--json"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(WinAppRootCommand.JsonOption));
    }

    private JsonElement ParseJsonOutput()
    {
        var output = TestAnsiConsole.Output;

        // Find the JSON object in the output (skip any non-JSON status output)
        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        Assert.IsTrue(jsonStart >= 0 && jsonEnd > jsonStart, "Output should contain a JSON object");

        var jsonText = output[jsonStart..(jsonEnd + 1)];
        var doc = JsonDocument.Parse(jsonText);
        return doc.RootElement;
    }

    #endregion

    #region --with-alias option tests

    [TestMethod]
    public void ParseOptions_WithAlias_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--with-alias"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.WithAliasOption));
    }

    [TestMethod]
    public void ParseOptions_WithAliasNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.WithAliasOption));
    }

    [TestMethod]
    public async Task RunCommand_WithAliasAndNoLaunch_ReturnsError()
    {
        // Arrange - --with-alias and --no-launch are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--with-alias", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --with-alias and --no-launch are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_WithAlias_RegistersIdentityButDoesNotLaunchByAumid()
    {
        // Arrange - manifest in input folder, --with-alias means no AUMID launch.
        // The LaunchViaExecutionAliasAsync will fail because there's no processed manifest
        // in the AppX output directory, but we can verify that it does NOT use AUMID launch.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--with-alias"]);

        // Assert - identity should be created but AUMID launch should NOT be used
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "Application should NOT be launched via AUMID when --with-alias is specified");
    }

    #endregion

    #region --debug-output option tests

    [TestMethod]
    public void ParseOptions_DebugOutput_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.DebugOutputOption));
    }

    [TestMethod]
    public void ParseOptions_DebugOutputNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.DebugOutputOption));
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputAndNoLaunch_ReturnsError()
    {
        // Arrange - --debug-output and --no-launch are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --debug-output and --no-launch are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_LaunchesByAumidAndCallsDebugService()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched via AUMID");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "Debug service should be called");
        Assert.AreEqual(_fakeAppLauncherService.FakeProcessId, _fakeDebugOutputService.AttachCalls[0],
            "Debug service should receive the launched process ID");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputWithAlias_SkipsAumidLaunch()
    {
        // Arrange - with both --debug-output and --with-alias, the execution alias path is used.
        // LaunchViaExecutionAliasAsync will fail because there's no processed manifest in AppX output,
        // but verify that AUMID launch is not used.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--with-alias"]);

        // Assert - identity should be created but AUMID launch should NOT be used
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "Application should NOT be launched via AUMID when --with-alias is specified");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_UsesDebugServiceExitCode()
    {
        // Arrange
        await CreateTestManifestAsync();
        _fakeDebugOutputService.FakeExitCode = 42;
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.AreEqual(42, exitCode, "Exit code should come from the debug service");
    }

    [TestMethod]
    public async Task RunCommand_JsonAndDebugOutput_ReturnsError()
    {
        // Arrange - --json and --debug-output are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output", "--json"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --json and --debug-output are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_JsonAndWithAlias_ReturnsError()
    {
        // Arrange - --json and --with-alias are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--with-alias", "--json"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --json and --with-alias are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutput_PropagatesFailureExitCode()
    {
        // Arrange — debug service returns -1 (e.g., DebugActiveProcess failed)
        await CreateTestManifestAsync();
        _fakeDebugOutputService.FakeExitCode = -1;
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--debug-output"]);

        // Assert
        Assert.AreEqual(-1, exitCode, "Failure exit code from the debug service should propagate");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputWithAliasAndNoLaunch_ReturnsError()
    {
        // Arrange — all three flags conflict; --with-alias + --no-launch is caught first
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--debug-output", "--with-alias", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail with conflicting flags");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_DebugOutputWithArgs_ForwardsArgsToLauncher()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--debug-output", "--args", "--my-flag value"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--my-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Arguments should be forwarded to the launcher");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "Debug service should be called");
    }

    #endregion

    #region --detach option tests

    [TestMethod]
    public void ParseOptions_Detach_IsParsedCorrectly()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName, "--detach"]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsTrue(parseResult.GetValue(RunCommand.DetachOption));
    }

    [TestMethod]
    public void ParseOptions_DetachNotSpecified_DefaultsToFalse()
    {
        // Arrange
        var command = GetRequiredService<RunCommand>();

        // Act
        var parseResult = command.Parse([_tempDirectory.FullName]);

        // Assert
        Assert.IsEmpty(parseResult.Errors, "There should be no parsing errors");
        Assert.IsFalse(parseResult.GetValue(RunCommand.DetachOption));
    }

    [TestMethod]
    public async Task RunCommand_DetachAndNoLaunch_ReturnsError()
    {
        // Arrange - --detach and --no-launch are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--no-launch"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --no-launch are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_DetachAndDebugOutput_ReturnsError()
    {
        // Arrange - --detach and --debug-output are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--debug-output"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --debug-output are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
        Assert.AreEqual(0, _fakeDebugOutputService.AttachCalls.Count, "Debug loop should not run");
    }

    [TestMethod]
    public async Task RunCommand_DetachAndWithAlias_ReturnsError()
    {
        // Arrange - --detach and --with-alias are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--with-alias"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --with-alias are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_DetachAndUnregisterOnExit_ReturnsError()
    {
        // Arrange - --detach and --unregister-on-exit are mutually exclusive
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--unregister-on-exit"]);

        // Assert
        Assert.AreEqual(1, exitCode, "Command should fail when both --detach and --unregister-on-exit are specified");
        Assert.AreEqual(0, _fakeMsixService.AddLooseLayoutCalls.Count, "No identity should be created");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_Detach_LaunchesByAumidAndReturnsImmediately()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Debug identity should be created");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched via AUMID");
    }

    [TestMethod]
    public async Task RunCommand_DetachWithJson_OutputsJsonWithProcessId()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach", "--json"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var json = ParseJsonOutput();
        Assert.AreEqual("TestPackage_fakefamily!TestApp", json.GetProperty("AUMID").GetString());
        Assert.AreEqual(_fakeAppLauncherService.FakeProcessId, json.GetProperty("ProcessId").GetUInt32(),
            "ProcessId should be present in detach mode");
        Assert.IsFalse(json.TryGetProperty("Error", out _), "Error should not be present on success");
    }

    [TestMethod]
    public async Task RunCommand_DetachWithoutJson_DoesNotOutputJson()
    {
        // Arrange
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, [_tempDirectory.FullName, "--detach"]);

        // Assert
        Assert.AreEqual(0, exitCode, "Command should succeed");

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(output.Contains("\"AUMID\""), "JSON fields should not appear without --json flag");
        Assert.IsFalse(output.Contains("\"ProcessId\""), "JSON fields should not appear without --json flag");
    }

    #endregion
}
