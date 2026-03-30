// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

[TestClass]
public class CompleteCommandTests : BaseCommandTests
{
    [TestMethod]
    public async Task CompleteCommandShouldReturnZeroExitCode()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "complete", "--word", "", "--commandline", "winapp ", "--position", "7" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "complete command should exit with 0");
    }

    [TestMethod]
    public async Task CompleteCommandShouldOutputTopLevelSubcommands()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "complete", "--word", "", "--commandline", "winapp ", "--position", "7" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("init"), $"Expected 'init' in completions, got:\n{output}");
        Assert.IsTrue(output.Contains("package"), $"Expected 'package' in completions, got:\n{output}");
        Assert.IsTrue(output.Contains("cert"), $"Expected 'cert' in completions, got:\n{output}");
    }

    [TestMethod]
    public async Task CompleteCommandShouldFilterByPartialWord()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        // Typing "winapp ce" — expect "cert" as a completion
        var args = new[] { "complete", "--word", "ce", "--commandline", "winapp ce", "--position", "9" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("cert"), $"Expected 'cert' in filtered completions, got:\n{output}");
    }

    [TestMethod]
    public async Task CompleteCommandShouldReturnSubcommandCompletions()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        // Typing "winapp cert " — expect cert subcommands
        var args = new[] { "complete", "--word", "", "--commandline", "winapp cert ", "--position", "12" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output;
        Assert.IsTrue(output.Contains("generate"), $"Expected 'generate' in cert subcommand completions, got:\n{output}");
        Assert.IsTrue(output.Contains("install"), $"Expected 'install' in cert subcommand completions, got:\n{output}");
        Assert.IsTrue(output.Contains("info"), $"Expected 'info' in cert subcommand completions, got:\n{output}");
    }

    [TestMethod]
    public async Task CompleteCommandShouldHandleEmptyCommandLine()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "complete", "--commandline", "winapp", "--position", "6" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert — should not throw, just return 0 with no output (or subcommand list)
        Assert.AreEqual(0, exitCode, "complete command should handle a bare program name gracefully");
    }

    [TestMethod]
    public async Task CompleteCommandShouldHandleMissingOptions()
    {
        // Arrange — invoke with no options at all
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "complete" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert — should succeed with exit code 0
        Assert.AreEqual(0, exitCode, "complete command should succeed even without options");
    }
}
