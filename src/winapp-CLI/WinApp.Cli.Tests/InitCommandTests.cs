// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the InitCommand including SDK installation mode handling
/// and project detection/selection behavior
/// </summary>
[TestClass]
public class InitCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// Pushes keys to answer "yes" to the "no project detected" confirmation prompt.
    /// The prompt uses DefaultValue=false, so we must push 'y' then Enter.
    /// </summary>
    private void PushConfirmYes()
    {
        TestAnsiConsole.Input.PushKey(ConsoleKey.Y);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    /// <summary>
    /// Pushes keys to answer "no" (the default) to the "no project detected" confirmation prompt.
    /// </summary>
    private void PushConfirmNo()
    {
        TestAnsiConsole.Input.PushKey(ConsoleKey.N);
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);
    }

    [TestMethod]
    public async Task InitCommand_WithConfigOnly_CreatesConfigFile()
    {
        // Arrange
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Explicit directory with no project files triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify winapp.yaml was created in the config directory
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");

        // Verify config contains packages section
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("packages:", configContent, "Config should contain packages section");
    }

    [TestMethod]
    public async Task InitCommand_WithSetupSdksNone_CompletesSuccessfully()
    {
        // Arrange — --no-prompt is an alias for --use-defaults, so no prompts are shown
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--setup-sdks", "none", "--no-prompt" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // When SdkInstallMode is None, the command returns early after "Configuration processed"
        // The .winapp directory and config file are NOT created (this is by design)
    }

    [TestMethod]
    public async Task InitCommand_WithNoGitignore_DoesNotModifyGitignore()
    {
        // Arrange
        var gitignorePath = Path.Combine(_tempDirectory.FullName, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "# Original content\n*.log");

        var initCommand = GetRequiredService<InitCommand>();
        // Use config-only to avoid long-running SDK installation
        var args = new[] { _tempDirectory.FullName, "--config-only", "--no-gitignore" };

        // Explicit directory with no project files triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify .gitignore was not modified
        var gitignoreContent = await File.ReadAllTextAsync(gitignorePath);
        Assert.DoesNotContain(".winapp", gitignoreContent, ".gitignore should not contain .winapp when --no-gitignore is used");
    }

    [TestMethod]
    public async Task InitCommand_WithConfigDir_CreatesConfigInSpecifiedDirectory()
    {
        // Arrange
        var configDir = _tempDirectory.CreateSubdirectory("config");
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-dir", configDir.FullName, "--config-only" };

        // Explicit directory with no project files triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify winapp.yaml was created in the specified config directory
        var configPath = Path.Combine(configDir.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be created at {configPath}");
    }

    [TestMethod]
    public async Task InitCommand_ConfigOnly_ExistingConfigValidated()
    {
        // Arrange - Create existing config
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, @"packages:
  - name: Microsoft.Windows.SDK.BuildTools
    version: 10.0.26100.1
");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Explicit directory with existing config but no project triggers a confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify existing config was not overwritten (same content)
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.Contains("10.0.26100.1", configContent, "Existing config version should be preserved");
    }

    [TestMethod]
    public async Task InitCommand_DoesNotGenerateCertificate()
    {
        // Arrange — --no-prompt skips all interactive prompts
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--setup-sdks", "none", "--no-prompt" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Init command should complete successfully");

        // Verify that no devcert.pfx was created - init should not generate certificates
        var certPath = Path.Combine(_tempDirectory.FullName, "devcert.pfx");
        Assert.IsFalse(File.Exists(certPath), "Init should not generate devcert.pfx - certificates should be generated separately with 'cert generate'");
    }

    // --- Project detection & selection behavior tests ---

    [TestMethod]
    public async Task InitCommand_ExplicitDirectory_SkipsSearchAndInitsDirectly()
    {
        // Arrange — create a .csproj in a subdirectory (would be found by search)
        // but pass the root directory explicitly — search should be skipped
        var subDir = _tempDirectory.CreateSubdirectory("nested");
        File.WriteAllText(Path.Combine(subDir.FullName, "MyApp.csproj"), "<Project />");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // No project at root → confirmation prompt
        PushConfirmYes();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should init in the specified directory (not the nested one)
        Assert.AreEqual(0, exitCode, "Init should complete successfully");
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), "winapp.yaml should be created in the explicitly specified directory");
    }

    [TestMethod]
    public async Task InitCommand_ExplicitDirectory_NoProject_UserDeclines_ReturnsNonZero()
    {
        // Arrange — empty directory, no project
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        // Decline the "no project detected" confirmation prompt
        PushConfirmNo();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — user cancelled, should return non-zero
        Assert.AreEqual(1, exitCode, "Init should return 1 when user cancels");
    }

    [TestMethod]
    public async Task InitCommand_UseDefaults_NoProject_ProceedsAnyway()
    {
        // Arrange — empty directory, no project markers
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { "--use-defaults", "--config-only" };

        // Act — no prompts expected with --use-defaults
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should proceed with warnings but succeed (creates config even without a project)
        Assert.AreEqual(0, exitCode, "Init should complete with --use-defaults even without a detected project");
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), "winapp.yaml should be created with --use-defaults even without a project");
    }

    [TestMethod]
    public async Task InitCommand_ExplicitDirectory_WithProject_NoConfirmationPrompt()
    {
        // Arrange — create a .csproj so the project is detected at the explicit path
        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "MyApp.csproj"), "<Project />");

        var initCommand = GetRequiredService<InitCommand>();
        // Use --no-prompt to skip workspace setup prompts (TFM update, etc.)
        var args = new[] { _tempDirectory.FullName, "--config-only", "--no-prompt" };

        // Act — no "no project detected" confirmation prompt since project is found;
        // --no-prompt skips all subsequent workspace setup prompts
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert — should succeed without needing the no-project confirmation prompt
        Assert.AreEqual(0, exitCode, "Init should complete successfully when project is detected");
    }
}
