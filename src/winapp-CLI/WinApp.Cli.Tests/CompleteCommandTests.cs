// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public class CompleteCommandTests : BaseCommandTests
{
    private string GetOutput() => TestAnsiConsole.Output?.Trim() ?? string.Empty;
    private string[] GetCompletionLines() =>
        GetOutput().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string[] GetCompletionLabels() =>
        GetCompletionLines().Select(line => line.Split('\t')[0]).ToArray();

    [TestMethod]
    public async Task Complete_TopLevelCommands_ReturnsAllCommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — complete at end of "winapp "
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        Assert.IsTrue(completions.Length > 0, "Should return completions");

        // Verify known top-level commands appear
        CollectionAssert.Contains(completions, "init");
        CollectionAssert.Contains(completions, "cert");
        CollectionAssert.Contains(completions, "package");
        CollectionAssert.Contains(completions, "manifest");
        CollectionAssert.Contains(completions, "sign");
        CollectionAssert.Contains(completions, "run");
        CollectionAssert.Contains(completions, "ui");
    }

    [TestMethod]
    public async Task Complete_PartialCommand_ReturnsMatchingCommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — complete "winapp in" (should match "init")
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp in", "--position", "9"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "init");
    }

    [TestMethod]
    public async Task Complete_Subcommands_ReturnsCertSubcommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — complete "winapp cert "
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp cert ", "--position", "12"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "generate");
        CollectionAssert.Contains(completions, "install");
        CollectionAssert.Contains(completions, "info");
    }

    [TestMethod]
    public async Task Complete_Options_ReturnsInitOptions()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — complete "winapp init --"
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init --", "--position", "14"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "--setup-sdks");
        CollectionAssert.Contains(completions, "--config-dir");
        CollectionAssert.Contains(completions, "--use-defaults");
        CollectionAssert.Contains(completions, "--no-gitignore");
    }

    [TestMethod]
    public async Task Complete_HiddenCommands_NotReturned()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — complete at top level
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();

        // "complete" command is hidden and should not appear in completions
        CollectionAssert.DoesNotContain(completions, "complete");
    }

    [TestMethod]
    public async Task Complete_EmptyCommandLine_ReturnsZeroExitCode()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — empty commandline
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "", "--position", "0"]);

        // Assert — should not error
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Complete_NoCommandLine_ReturnsZeroExitCode()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — no --commandline provided at all
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete"]);

        // Assert — should not error
        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task Complete_SetupPowerShell_OutputsRegistrationScript()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "powershell"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = GetOutput();
        Assert.IsTrue(output.Contains("Register-ArgumentCompleter"), "Should contain PowerShell argument completer");
        Assert.IsTrue(output.Contains("winapp complete"), "Should reference winapp complete command");
        Assert.IsTrue(output.Contains("--commandline"), "Should pass commandline");
        Assert.IsTrue(output.Contains("--position"), "Should pass position");
    }

    [TestMethod]
    public async Task Complete_SetupBash_OutputsRegistrationScript()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "bash"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = GetOutput();
        Assert.IsTrue(output.Contains("_winapp_completions"), "Should contain bash completion function");
        Assert.IsTrue(output.Contains("complete -o default -F"), "Should register with bash complete");
    }

    [TestMethod]
    public async Task Complete_SetupZsh_OutputsRegistrationScript()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "zsh"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = GetOutput();
        Assert.IsTrue(output.Contains("compdef _winapp winapp"), "Should contain zsh compdef");
    }

    [TestMethod]
    public async Task Complete_SetupUnknownShell_ReturnsError()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "fish"]);

        // Assert
        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public async Task Complete_ManifestSubcommands_ReturnsExpected()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act — complete "winapp manifest "
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp manifest ", "--position", "16"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "generate");
        CollectionAssert.Contains(completions, "update-assets");
        CollectionAssert.Contains(completions, "add-alias");
    }

    [TestMethod]
    public async Task Complete_TopLevelCommands_IncludesDescriptions()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        // Assert
        Assert.AreEqual(0, exitCode);
        var lines = GetCompletionLines();

        // At least some lines should contain tab-separated descriptions
        var linesWithDescriptions = lines.Where(l => l.Contains('\t')).ToArray();
        Assert.IsTrue(linesWithDescriptions.Length > 0, "Completions should include descriptions");

        // Verify a known command has its description
        var initLine = linesWithDescriptions.FirstOrDefault(l => l.StartsWith("init\t", StringComparison.Ordinal));
        Assert.IsNotNull(initLine, "init command should have a description");
    }

    [TestMethod]
    public async Task CliSchema_DoesNotContainHiddenCommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--cli-schema" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode);
        using var jsonDoc = JsonDocument.Parse(TestAnsiConsole.Output);
        var root = jsonDoc.RootElement;

        Assert.IsTrue(root.TryGetProperty("subcommands", out var subcommands));
        Assert.IsFalse(subcommands.TryGetProperty("complete", out _),
            "Hidden 'complete' command should not appear in CLI schema");
    }
}
