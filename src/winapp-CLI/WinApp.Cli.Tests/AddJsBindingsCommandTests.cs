// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

// Tests for AddJsBindingsCommand — focus on yaml mutations + npm-shim gate.
// Codegen result is not asserted (no real NuGet cache → exit 1 from "no
// winmds"); yaml is mutated before codegen so state assertions still hold.
// [DoNotParallelize] because tests mutate WINAPP_CLI_CALLER process-wide.
[TestClass]
[DoNotParallelize]
public class AddJsBindingsCommandTests : BaseCommandTests
{
    private string? _savedCaller;

    [TestInitialize]
    public void TestSetup()
    {
        _savedCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        // Default to the npm-shim caller; tests that need to assert the gate
        // override this explicitly.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
    }

    [TestCleanup]
    public void TestTeardown()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _savedCaller);
    }

    // Helper: write a minimal valid winapp.yaml (packages: only) so the
    // add command sees a "post-init" workspace. Returns the absolute path.
    private async Task<string> WriteMinimalYamlAsync()
    {
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n");
        return configPath;
    }

    // Helper: write a winapp.yaml that already has a jsBindings: block.
    // Used to exercise the "existing block" branches (force / non-force /
    // non-interactive).
    private async Task<string> WriteYamlWithJsBindingsAsync(string output = "old/output")
    {
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + $"  output: {output}\n"
            + "  lang: js\n");
        return configPath;
    }

    [TestMethod]
    public async Task AddJsBindings_WithoutNpmCaller_ExitsWithActionableError()
    {
        // Same npm-shim gating as InitCommand --js-bindings.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(1, exitCode, "Non-npm caller must exit 1");
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "'node jsbindings add' requires the @microsoft/winappcli npm package",
            "Error must name the command and the required package");
        StringAssert.Contains(stderr, "npx winapp node jsbindings add",
            "Error must include the recovery invocation");

        // No yaml mutation should occur — bailed before service ran.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(configPath),
            "winapp.yaml must not be touched when the npm-shim gate fails");
    }

    [TestMethod]
    public async Task AddJsBindings_NoYaml_ReturnsErrorWithInitHint()
    {
        // No init was ever run → there's no winapp.yaml. add jsbindings is a
        // layered command and must refuse instead of silently bootstrapping.
        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(1, exitCode, "Missing winapp.yaml must surface an error");
        // Failure goes to stderr (via ILogger.LogError); stdout stays clean
        // so non-interactive consumers can rely on it for success payloads.
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "winapp.yaml not found",
            "Error must explain the missing yaml precondition (routed to stderr)");
        StringAssert.Contains(stderr, "winapp init",
            "Error must point users at the bootstrap command");
    }

    [TestMethod]
    public async Task AddJsBindings_FreshWorkspace_AddsJsBindingsBlock()
    {
        // Yaml exists, no jsBindings block → inject defaults + persist.
        var configPath = await WriteMinimalYamlAsync();

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "jsBindings:",
            "jsBindings: block must be injected by add jsbindings");
        StringAssert.Contains(content, "lang: js",
            "Default lang=js must be persisted");
        StringAssert.Contains(content, "bindings/winrt",
            "Default output dir must be persisted");
        StringAssert.Contains(content, "packages:",
            "Existing packages section must be preserved (non-destructive)");
        StringAssert.Contains(content, "Microsoft.WindowsAppSDK",
            "Pre-existing package pin must survive the add");
    }

    [TestMethod]
    public async Task AddJsBindings_WithOutput_PersistsCustomOutputDir()
    {
        // --output should override the default 'bindings/winrt' and land
        // verbatim in the yaml's jsBindings.output field.
        var configPath = await WriteMinimalYamlAsync();

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--output", "src/generated/winrt" };

        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "output: src/generated/winrt",
            "--output must override the default and persist to yaml");
    }

    [TestMethod]
    public async Task AddJsBindings_AiAlias_PopulatesPackages()
    {
        // --ai populates jsBindings.packages with the preset's NuGet IDs.
        var configPath = await WriteMinimalYamlAsync();
        var aiPackages = JsBindingsPresets.KnownPresets["ai"];
        Assert.IsTrue(aiPackages.Count > 0, "Test precondition: ai preset must declare package IDs");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--ai" };

        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "packages:",
            "--ai must produce a packages: yaml field under jsBindings");
        foreach (var pkg in aiPackages)
        {
            StringAssert.Contains(content, pkg,
                $"AI preset package id {pkg} must appear in the persisted yaml");
        }
    }

    [TestMethod]
    public async Task AddJsBindings_ExistingBlockNoForce_NonInteractive_ReturnsError()
    {
        // Non-interactive runtime → prompt throws → we surface the --force
        // hint instead of clobbering.
        var configPath = await WriteYamlWithJsBindingsAsync("custom/old");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(1, exitCode,
            "Existing block + no --force + non-interactive must exit 1");
        // --force hint goes to stderr via ILogger.LogError (M11 fix).
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "--force",
            "Error must point users at --force to bypass the prompt in CI (routed to stderr)");

        // Yaml must NOT be mutated — the original output should still be there.
        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "custom/old",
            "Original jsBindings block must be preserved when the prompt rejects");
    }

    [TestMethod]
    public async Task AddJsBindings_ExistingBlockWithForce_ReplacesBlock()
    {
        // --force bypasses the prompt entirely (silent replace). The new
        // block should overwrite the old one with the CLI-supplied output.
        var configPath = await WriteYamlWithJsBindingsAsync("custom/old");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--force", "--output", "fresh/output" };

        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "fresh/output",
            "--force must replace the existing jsBindings block with the new one");
        Assert.IsFalse(content.Contains("custom/old"),
            "Old jsBindings block must be gone after --force replace");
    }

    // scripted callers (CI / build steps) need a safe
    // non-interactive no-op that preserves an existing jsBindings: block
    // without prompting and without overwriting.
    [TestMethod]
    public async Task AddJsBindings_ExistingBlockWithUseDefaults_PreservesAndExitsZero()
    {
        var configPath = await WriteYamlWithJsBindingsAsync("custom/old");
        var originalContent = await File.ReadAllTextAsync(configPath);

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--use-defaults", "--output", "should-be-ignored" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(0, exitCode,
            "--use-defaults must exit 0 (idempotent no-op) when jsBindings already exists.");

        var content = await File.ReadAllTextAsync(configPath);
        Assert.AreEqual(originalContent, content,
            "File on disk must be byte-identical — --use-defaults preserves, does NOT mutate.");
        Assert.IsFalse(content.Contains("should-be-ignored"),
            "--output override must be ignored when --use-defaults preserves the existing block.");
    }

    [TestMethod]
    public async Task AddJsBindings_NoExistingBlockWithUseDefaults_NormalAddFlow()
    {
        // --use-defaults is a no-op marker for the existing-block case only.
        // When there's NO block yet, the command proceeds normally (adds).
        await WriteMinimalYamlAsync();

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--use-defaults", "--output", "bindings/winrt" };

        // Exit may be 1 (no NuGet cache → "No .winmd files found") OR 0
        // (somehow finds metadata); we assert the yaml mutation, not the
        // codegen result. Either way, the yaml MUST have been patched
        // because --use-defaults shouldn't short-circuit on a fresh add.
        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "jsBindings:",
            "Fresh add (no existing block) with --use-defaults must still write the block.");
    }

    [TestMethod]
    public async Task AddJsBindings_ForceAndUseDefaultsTogether_RejectedAsMutuallyExclusive()
    {
        await WriteYamlWithJsBindingsAsync("custom/old");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--force", "--use-defaults" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(1, exitCode);
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "mutually exclusive",
            "Error must call out the conflict so users pick one.");
    }

    // interactive overwrite prompt — "No" answer.
    [TestMethod]
    public async Task AddJsBindings_ExistingBlockNoForce_PromptNo_PreservesYamlAndSkipsCodegen()
    {
        var configPath = await WriteYamlWithJsBindingsAsync("custom/old");
        var originalContent = await File.ReadAllTextAsync(configPath);

        // Drive the ConfirmationPrompt with "n" + Enter. The default for
        // Spectre's ConfirmationPrompt is "Yes", so we have to explicitly
        // type N to override.
        TestAnsiConsole.Input.PushTextWithEnter("n");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName };

        var exitCode = await ParseAndInvokeWithCaptureAsync(addCmd, args);

        Assert.AreEqual(0, exitCode,
            "Prompt 'No' must exit 0 (user chose to preserve).");

        var content = await File.ReadAllTextAsync(configPath);
        Assert.AreEqual(originalContent, content,
            "Yaml on disk must be unchanged after prompt 'No'.");
    }

    // interactive overwrite prompt — "Yes" answer patches.
    [TestMethod]
    public async Task AddJsBindings_ExistingBlockNoForce_PromptYes_PatchesYamlAndProceedsToCodegen()
    {
        var configPath = await WriteYamlWithJsBindingsAsync("custom/old");
        TestAnsiConsole.Input.PushTextWithEnter("y");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[] { _tempDirectory.FullName, "--output", "fresh/output" };

        // Exit may be 1 because the test env has no NuGet cache → codegen
        // discovery returns 0 winmds → "No .winmd files found" → 1.
        // We assert YAML mutation, not codegen result; the YAML patch
        // happens BEFORE codegen runs so it's observable either way.
        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var content = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(content, "fresh/output",
            "Prompt 'Yes' must patch the existing block with the new output.");
        Assert.IsFalse(content.Contains("custom/old"),
            "Old output must be gone after 'Yes' patch.");
    }

    [TestMethod]
    public async Task AddJsBindings_ExistingBlockWithForce_PreservesUserCustomizedFields()
    {
        // --force is a PATCH (not replace): CLI fields overwrite; everything
        // else (extraTypes / additionalWinmds/Refs / skip+refOnly+emit
        // overrides) must survive.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n"
            + "jsBindings:\n"
            + "  output: custom/old\n"
            + "  lang: js\n"
            + "  packages:\n"
            + "    - Microsoft.WindowsAppSDK.OldPreset\n"
            + "  additionalWinmds:\n"
            + "    - vendor/MyCo.Foo.winmd\n"
            + "  additionalRefs:\n"
            + "    - vendor/BigSDK.winmd\n"
            + "  skipPackages:\n"
            + "    - Custom.SkipMe.Package\n"
            + "  refOnlyPackages:\n"
            + "    - Custom.RefOnlyMe.Package\n"
            + "  emitPackages:\n"
            + "    - Microsoft.WindowsAppSDK.WinUI\n"
            + "  extraTypes:\n"
            + "    - namespace: Windows.Foundation\n"
            + "      classes:\n"
            + "        - Uri\n"
            + "        - Calendar\n");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        // --ai overrides packages: with the AI preset, --output overrides output:;
        // every other field above must survive.
        var args = new[] { _tempDirectory.FullName, "--force", "--ai", "--output", "fresh/output" };

        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var content = await File.ReadAllTextAsync(configPath);

        // CLI-touched fields: replaced.
        StringAssert.Contains(content, "fresh/output",
            "--output must overwrite jsBindings.output");
        StringAssert.Contains(content, "Microsoft.WindowsAppSDK.AI",
            "--ai must overwrite jsBindings.packages with the preset's IDs");
        Assert.IsFalse(content.Contains("custom/old"), "Old output must be gone");
        Assert.IsFalse(content.Contains("OldPreset"), "Old packages list must be gone");

        // Untouched user fields: preserved.
        StringAssert.Contains(content, "vendor/MyCo.Foo.winmd",
            "additionalWinmds entries must survive --force patch");
        StringAssert.Contains(content, "vendor/BigSDK.winmd",
            "additionalRefs entries must survive --force patch");
        StringAssert.Contains(content, "Custom.SkipMe.Package",
            "skipPackages overrides must survive --force patch");
        StringAssert.Contains(content, "Custom.RefOnlyMe.Package",
            "refOnlyPackages overrides must survive --force patch");
        StringAssert.Contains(content, "Microsoft.WindowsAppSDK.WinUI",
            "emitPackages overrides must survive --force patch");
        StringAssert.Contains(content, "Windows.Foundation",
            "extraTypes namespace must survive --force patch");
        StringAssert.Contains(content, "Uri",
            "extraTypes classes must survive --force patch");
        StringAssert.Contains(content, "Calendar",
            "extraTypes classes must survive --force patch (2nd entry)");
    }

    [TestMethod]
    public async Task AddJsBindings_KebabCaseAlias_RoutesToSameHandler()
    {
        // `node js-bindings add` (kebab-case alias on parent) routes correctly.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n");

        var rootCmd = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "node", "js-bindings", "add", _tempDirectory.FullName };

        var parseResult = rootCmd.Parse(args);
        Assert.AreEqual(0, parseResult.Errors.Count,
            "Kebab-case alias must parse without errors: "
            + string.Join("; ", parseResult.Errors.Select(e => e.Message)));

        Assert.IsInstanceOfType<AddJsBindingsCommand>(parseResult.CommandResult.Command,
            "`node js-bindings add` must route to AddJsBindingsCommand via the parent alias.");
    }

    [TestMethod]
    public async Task AddJsBindings_WithConfigDirSeparateFromWorkspace_PatchesIntendedYaml()
    {
        // --config-dir lets the user point at a different directory containing
        // winapp.yaml while keeping the workspace (binding-output anchor) elsewhere.
        var configDir = _tempDirectory.CreateSubdirectory("config-dir");
        var workspaceDir = _tempDirectory.CreateSubdirectory("workspace");

        var configPath = Path.Combine(configDir.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath,
            "packages:\n"
            + "  - name: Microsoft.WindowsAppSDK\n"
            + "    version: 1.8.39\n");

        // A decoy yaml in the workspace should NOT be touched.
        var decoyPath = Path.Combine(workspaceDir.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(decoyPath, "# decoy — must not be modified\n");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        var args = new[]
        {
            workspaceDir.FullName,
            "--config-dir", configDir.FullName,
            "--output", "bindings/winrt",
        };

        await ParseAndInvokeWithCaptureAsync(addCmd, args);

        var actualConfig = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(actualConfig, "jsBindings:",
            "--config-dir target yaml must be patched");

        var decoy = await File.ReadAllTextAsync(decoyPath);
        StringAssert.Contains(decoy, "# decoy",
            "Workspace-directory yaml must NOT be touched when --config-dir is set");
        Assert.IsFalse(decoy.Contains("jsBindings:"),
            "Workspace yaml must remain unpatched.");
    }
}
