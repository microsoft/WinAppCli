// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

// Tests for `node jsbindings generate` — read-only codegen against the
// existing winapp.yaml. Mirrors AddJsBindingsCommandTests structure.
// Codegen result is not asserted (no real NuGet cache); we assert yaml
// is NOT mutated and the npm-shim gate is enforced.
[TestClass]
[DoNotParallelize]
public class GenerateJsBindingsCommandTests : BaseCommandTests
{
    private string? _savedCaller;

    [TestInitialize]
    public void TestSetup()
    {
        _savedCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
    }

    [TestCleanup]
    public void TestTeardown()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _savedCaller);
    }

    private async Task<(string ConfigPath, string Content)> WriteYamlWithJsBindingsAsync(string output = "bindings/winrt")
    {
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        var content =
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + $"  output: {output}\n"
            + "  lang: js\n";
        await File.WriteAllTextAsync(configPath, content);
        return (configPath, content);
    }

    [TestMethod]
    public async Task Generate_WithoutNpmCaller_ExitsWithActionableError()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);
        await WriteYamlWithJsBindingsAsync();

        var cmd = GetRequiredService<GenerateJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(cmd, args);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "'node jsbindings generate' requires the @microsoft/winappcli npm package");
        StringAssert.Contains(stderr, "npx winapp node jsbindings generate");
    }

    [TestMethod]
    public async Task Generate_NoYaml_ReturnsErrorWithInitHint()
    {
        // No yaml at all → tell the user to init first.
        var cmd = GetRequiredService<GenerateJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(cmd, args);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "winapp.yaml not found");
    }

    [TestMethod]
    public async Task Generate_YamlWithoutJsBindingsBlock_FailsWithAddHint()
    {
        // yaml exists but has no jsBindings: block — point the user at
        // `node jsbindings add` to declare one first.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n");
        var originalContent = await File.ReadAllTextAsync(configPath);

        var cmd = GetRequiredService<GenerateJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(cmd, args);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "No jsBindings: block",
            "Error must call out the missing block.");
        StringAssert.Contains(stderr, "node jsbindings add",
            "Error must point users at the add command.");

        var after = await File.ReadAllTextAsync(configPath);
        Assert.AreEqual(originalContent, after,
            "Yaml must remain byte-identical when generate refuses.");
    }

    [TestMethod]
    public async Task Generate_WithExistingJsBindings_DoesNotMutateYaml()
    {
        // Happy-ish path: yaml has jsBindings block. Codegen will fail with
        // "no winmds" (no NuGet cache in test env), but we only assert
        // that the yaml is NOT mutated regardless of codegen outcome.
        var (configPath, originalContent) = await WriteYamlWithJsBindingsAsync("generated-js");

        var cmd = GetRequiredService<GenerateJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        await ParseAndInvokeWithCaptureAsync(cmd, args);

        var after = await File.ReadAllTextAsync(configPath);
        Assert.AreEqual(originalContent, after,
            "generate is read-only on yaml — file must be byte-identical.");
    }

    [TestMethod]
    public async Task Generate_RoutesViaWinAppRootCommand()
    {
        // Verify the actual command tree exposes `node jsbindings generate`.
        await WriteYamlWithJsBindingsAsync();

        var rootCmd = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "node", "jsbindings", "generate", _tempDirectory.FullName };

        var parseResult = rootCmd.Parse(args);
        Assert.AreEqual(0, parseResult.Errors.Count,
            "Parse errors: " + string.Join("; ", parseResult.Errors.Select(e => e.Message)));
        Assert.IsInstanceOfType<GenerateJsBindingsCommand>(parseResult.CommandResult.Command,
            "`node jsbindings generate` must route to GenerateJsBindingsCommand.");
    }
}
