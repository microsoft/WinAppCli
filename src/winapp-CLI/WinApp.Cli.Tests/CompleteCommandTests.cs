// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

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

    // --- Top-level command completions ---

    [TestMethod]
    public async Task Complete_TopLevelCommands_ReturnsAllCommands()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        Assert.IsTrue(completions.Length > 0, "Should return completions");
        CollectionAssert.Contains(completions, "init");
        CollectionAssert.Contains(completions, "cert");
        CollectionAssert.Contains(completions, "package");
        CollectionAssert.Contains(completions, "manifest");
        CollectionAssert.Contains(completions, "sign");
        CollectionAssert.Contains(completions, "run");
        CollectionAssert.Contains(completions, "ui");
    }

    [TestMethod]
    public async Task Complete_TopLevelCommands_IncludesDescriptions()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        Assert.AreEqual(0, exitCode);
        var lines = GetCompletionLines();
        var linesWithDescriptions = lines.Where(l => l.Contains('\t')).ToArray();
        Assert.IsTrue(linesWithDescriptions.Length > 0, "Completions should include descriptions");

        var initLine = linesWithDescriptions.FirstOrDefault(l => l.StartsWith("init\t", StringComparison.Ordinal));
        Assert.IsNotNull(initLine, "init command should have a description");
    }

    [TestMethod]
    public async Task Complete_HiddenCommands_NotReturned()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.DoesNotContain(completions, "complete");
    }

    // --- Prefix matching (not substring) ---

    [TestMethod]
    public async Task Complete_PartialCommand_ReturnsOnlyPrefixMatches()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp in", "--position", "9"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "init");
        // Substring matches should NOT appear -- "sign", "unregister" contain "in" but don't start with it
        CollectionAssert.DoesNotContain(completions, "sign");
        CollectionAssert.DoesNotContain(completions, "unregister");
    }

    [TestMethod]
    public async Task Complete_PartialOption_ReturnsOnlyPrefixMatches()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // "winapp init --c" should match --cli-schema, --config-dir, --config-only
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init --c", "--position", "15"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "--cli-schema");
        CollectionAssert.Contains(completions, "--config-dir");
        CollectionAssert.Contains(completions, "--config-only");
        // Non-matching options should be absent
        CollectionAssert.DoesNotContain(completions, "--setup-sdks");
        CollectionAssert.DoesNotContain(completions, "--verbose");
    }

    // --- Alias exclusion ---

    [TestMethod]
    public async Task Complete_AliasesAreExcluded()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "7"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        // "pack" is an alias for "package", "run-buildtool" is an alias for "tool"
        CollectionAssert.DoesNotContain(completions, "pack");
        CollectionAssert.DoesNotContain(completions, "run-buildtool");
        // Primary names should be present
        CollectionAssert.Contains(completions, "package");
        CollectionAssert.Contains(completions, "tool");
    }

    [TestMethod]
    public async Task Complete_ShortOptionAliasesAreExcluded()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Complete options for init -- should not include -h, -?, /h, /?
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init -", "--position", "13"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.DoesNotContain(completions, "-h");
        CollectionAssert.DoesNotContain(completions, "-?");
        CollectionAssert.DoesNotContain(completions, "/h");
        CollectionAssert.DoesNotContain(completions, "/?");
    }

    // --- Subcommand completions ---

    [TestMethod]
    public async Task Complete_Subcommands_ReturnsCertSubcommands()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp cert ", "--position", "12"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "generate");
        CollectionAssert.Contains(completions, "install");
        CollectionAssert.Contains(completions, "info");
    }

    [TestMethod]
    public async Task Complete_ManifestSubcommands_ReturnsExpected()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp manifest ", "--position", "16"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "generate");
        CollectionAssert.Contains(completions, "update-assets");
        CollectionAssert.Contains(completions, "add-alias");
    }

    // --- Options completions ---

    [TestMethod]
    public async Task Complete_Options_ReturnsInitOptions()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init --", "--position", "14"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "--setup-sdks");
        CollectionAssert.Contains(completions, "--config-dir");
        CollectionAssert.Contains(completions, "--use-defaults");
        CollectionAssert.Contains(completions, "--no-gitignore");
    }

    [TestMethod]
    public async Task Complete_NoSubcommands_FallsBackToOptions()
    {
        // "winapp init " has no subcommands, so completions should show options
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init ", "--position", "12"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        Assert.IsTrue(completions.Length > 0, "Should fall back to showing options");
        Assert.IsTrue(completions.Any(c => c.StartsWith("--", StringComparison.Ordinal)),
            "Should contain option flags when no subcommands exist");
    }

    // --- Position edge cases ---

    [TestMethod]
    public async Task Complete_PositionPastEnd_TreatsAsTrailingSpace()
    {
        // PowerShell sends position past the string length when there's a trailing space
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // "winapp cert" is 11 chars, position 12 means trailing space
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp cert", "--position", "12"]);

        Assert.AreEqual(0, exitCode);
        var completions = GetCompletionLabels();
        CollectionAssert.Contains(completions, "generate");
        CollectionAssert.Contains(completions, "install");
        CollectionAssert.Contains(completions, "info");
    }

    [TestMethod]
    public async Task Complete_NegativePosition_ClampsToZero()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp ", "--position", "-1"]);

        // Should not throw, should return 0
        Assert.AreEqual(0, exitCode);
    }

    // --- End-of-options marker ---

    [TestMethod]
    public async Task Complete_EndOfOptionsMarker_ReturnsEmpty()
    {
        // After "-- ", everything is positional -- no option completions
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init -- ", "--position", "15"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, GetOutput(), "Should produce no output after end-of-options marker");
    }

    // --- Path prefix fallthrough ---

    [TestMethod]
    public async Task Complete_PathPrefix_ReturnsEmpty()
    {
        // When user types a path like "./", completions should be empty
        // to let the shell's built-in file completion handle it
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "winapp init .", "--position", "13"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, GetOutput(), "Should produce no output for path prefixes");
    }

    // --- Empty/no commandline ---

    [TestMethod]
    public async Task Complete_EmptyCommandLine_ProducesNoOutput()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--commandline", "", "--position", "0"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, GetOutput(), "Empty commandline should produce no output");
    }

    [TestMethod]
    public async Task Complete_NoCommandLine_ProducesNoOutput()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete"]);

        Assert.AreEqual(0, exitCode);
    }

    // --- Setup scripts ---

    [TestMethod]
    public async Task Complete_SetupPowerShell_OutputsRegistrationScript()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "powershell"]);

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
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "bash"]);

        Assert.AreEqual(0, exitCode);
        var output = GetOutput();
        Assert.IsTrue(output.Contains("_winapp_completions"), "Should contain bash completion function");
        Assert.IsTrue(output.Contains("complete -o default -F"), "Should register with bash complete");
        Assert.IsTrue(output.Contains("--commandline"), "Should pass commandline");
        Assert.IsTrue(output.Contains("--position"), "Should pass position");
    }

    [TestMethod]
    public async Task Complete_SetupZsh_OutputsRegistrationScript()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "zsh"]);

        Assert.AreEqual(0, exitCode);
        var output = GetOutput();
        Assert.IsTrue(output.Contains("compdef _winapp winapp"), "Should contain zsh compdef");
        Assert.IsTrue(output.Contains("--commandline"), "Should pass commandline");
        Assert.IsTrue(output.Contains("--position"), "Should pass position");
    }

    [TestMethod]
    public async Task Complete_SetupUnknownShell_ReturnsError()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand,
            ["complete", "--setup", "fish"]);

        Assert.AreEqual(1, exitCode);
    }
}
