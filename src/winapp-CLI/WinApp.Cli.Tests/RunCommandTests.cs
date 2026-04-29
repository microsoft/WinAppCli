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

    #region -- passthrough argument tests

    // --- Parse-level behaviour ---

    [TestMethod]
    public void ParseOptions_DoubleDashPassthrough_ProducesNoParseErrors()
    {
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--", "--flag", "value"]);
        Assert.IsEmpty(parseResult.Errors, "Tokens after -- should not cause parse errors");
    }

    [TestMethod]
    public void ParseOptions_BareDoubleDash_ProducesNoParseErrors()
    {
        // A bare '--' with nothing after it is valid; the app simply receives no passthrough args.
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--"]);
        Assert.IsEmpty(parseResult.Errors, "A bare -- with nothing following should not cause parse errors");
    }

    [TestMethod]
    public void ParseOptions_UnknownOptionBeforeDoubleDash_AbsorbedIntoZeroOrMore_NoParseError()
    {
        // With a ZeroOrMore positional argument, System.CommandLine absorbs unrecognised
        // option-like tokens (e.g. '--unknown-opt') into the argument rather than reporting
        // them as parse errors. The handler uses SplitPassthroughTokens to detect and reject
        // these tokens at invocation time.
        var command = GetRequiredService<RunCommand>();
        var parseResult = command.Parse([_tempDirectory.FullName, "--unknown-opt", "--", "--app-flag"]);
        Assert.IsEmpty(parseResult.Errors,
            "ZeroOrMore absorbs pre-'--' unknown tokens silently; the handler validates them");
    }

    // --- Handler: basic passthrough scenarios ---

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_ForwardsArgsToLauncher()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--my-flag", "value"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--my-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Passthrough args after -- should be forwarded to the launcher");
    }

    [TestMethod]
    public async Task RunCommand_BareDoubleDash_LaunchesWithNoArgs()
    {
        // A bare '--' separator with nothing after it should launch successfully with no app args.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--"]);

        Assert.AreEqual(0, exitCode, "Command should succeed with a bare --");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.IsNull(_fakeAppLauncherService.LaunchCalls[0].Arguments,
            "No app args should be passed when nothing follows --");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_MultipleArgs_AllForwarded()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--flag1", "v1", "--flag2", "v2", "--flag3"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual("--flag1 v1 --flag2 v2 --flag3",
            _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "All passthrough tokens should be forwarded in order");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_MergesWithArgsOption()
    {
        // --args value and tokens after -- are both forwarded, --args first.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--args", "--existing", "--", "--flag"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual("--existing --flag", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "--args value and -- passthrough args should both be forwarded");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_ValueWithSpace_QuotedInLaunchArgs()
    {
        // This test verifies the full pipeline: token → JoinArguments → launcher.
        // A value that contains a space must be quoted so the launched app's CommandLineToArgvW
        // recovers the original token correctly.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--title", "hello world"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--title \"hello world\"", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Values containing spaces must be quoted in the final command-line string");
    }

    // --- Handler: unknown-token rejection ---

    [TestMethod]
    public async Task RunCommand_UnknownOptionBeforeDoubleDash_ReturnsError()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--unknown-winapp-option", "--", "--app-flag"]);

        Assert.AreEqual(1, exitCode, "Unknown winapp options before -- should still fail");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_BadTokenBeforeDoubleDash_RejectsWithError_DoesNotForwardGoodToken()
    {
        // Explicit test for: winapp run . --badtoken -- --cooltoken
        // --badtoken is an unrecognised winapp option BEFORE '--' → error, exit 1
        // --cooltoken is a legitimate passthrough AFTER '--' → NOT forwarded (command aborts)
        // This ensures the bad pre-dash token is caught and no launch occurs.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--badtoken", "--", "--cooltoken"]);

        Assert.AreEqual(1, exitCode, "Bad pre-dash token must cause exit code 1");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count,
            "App must NOT be launched when a bad pre-dash token is present");
    }

    [TestMethod]
    public async Task RunCommand_UnknownOptionWithNoDoubleDash_ReturnsError()
    {
        // Ensures the guard fires even when the user never typed '--'.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--unknown-winapp-option"]);

        Assert.AreEqual(1, exitCode, "Unknown options without -- should fail");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    [TestMethod]
    public async Task RunCommand_SameTokenBeforeAndAfterDoubleDash_ReturnsError()
    {
        // The duplicate-value edge case: the same string appears before '--' (bad) and after '--'
        // (legitimate passthrough).  A naïve set-based check would cancel them out and let the bad
        // token through.  The count-based implementation must catch it.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--flag", "--", "--flag"]);

        Assert.AreEqual(1, exitCode,
            "The pre-dash unknown token must be rejected even when the same value appears as passthrough");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "No application should be launched");
    }

    // --- Handler: passthrough interacts correctly with other mode flags ---

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_WithNoLaunch_Succeeds()
    {
        // --no-launch registers the package without launching; passthrough args are collected but
        // irrelevant — the important thing is the command does NOT error just because -- was used.
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--no-launch", "--", "--app-flag"]);

        Assert.AreEqual(0, exitCode, "-- passthrough should not cause an error when combined with --no-launch");
        Assert.AreEqual(1, _fakeMsixService.AddLooseLayoutCalls.Count, "Package should still be registered");
        Assert.AreEqual(0, _fakeAppLauncherService.LaunchCalls.Count, "App must NOT be launched with --no-launch");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_WithDetach_ForwardsArgs()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--detach", "--", "--app-flag", "value"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--app-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Passthrough args should be forwarded to the launcher in --detach mode");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_WithDebugOutput_ForwardsArgs()
    {
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--debug-output", "--", "--app-flag", "value"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--app-flag value", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "Passthrough args should be forwarded to the launcher in --debug-output mode");
        Assert.AreEqual(1, _fakeDebugOutputService.AttachCalls.Count, "Debug service should still be called");
    }

    [TestMethod]
    public async Task RunCommand_DoubleDashPassthrough_ForwardsLiteralDoubleDash()
    {
        // A '--' that appears AFTER the separator is an app argument, not another separator.
        // It must be forwarded as the literal string "--".
        await CreateTestManifestAsync();
        var command = GetRequiredService<RunCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command,
            [_tempDirectory.FullName, "--", "--"]);

        Assert.AreEqual(0, exitCode, "Command should succeed");
        Assert.AreEqual(1, _fakeAppLauncherService.LaunchCalls.Count, "Application should be launched");
        Assert.AreEqual("--", _fakeAppLauncherService.LaunchCalls[0].Arguments,
            "A literal -- after the passthrough separator should be forwarded to the app");
    }

    #endregion
}
