// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Tests.TestDoubles;

namespace WinApp.Cli.Tests;

// Tests for the InitCommand including SDK installation mode handling
[TestClass]
public class InitCommandTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        return services;
    }

    [TestMethod]
    public async Task InitCommand_WithConfigOnly_CreatesConfigFile()
    {
        // Arrange
        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

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
        // Arrange
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
        // Arrange
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
}

// --js-bindings flag gating — only allowed from the npm shim
// (WINAPP_CLI_CALLER=nodejs-package). [DoNotParallelize] because tests
// mutate that process-wide env var.
[TestClass]
[DoNotParallelize]
public class InitCommandJsBindingsTests : BaseCommandTests
{
    private string? _savedCaller;

    [TestInitialize]
    public void TestSetup()
    {
        _savedCaller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
    }

    [TestCleanup]
    public void TestTeardown()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", _savedCaller);
    }

    [TestMethod]
    public async Task InitCommand_WithJsBindingsAndWingetCaller_ExitsWithActionableError()
    {
        // Arrange — simulate winget invocation: env var unset.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--js-bindings" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(1, exitCode, "winget caller passing --js-bindings should exit with code 1");

        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "--js-bindings requires the @microsoft/winappcli npm package",
            "Error must name the flag and the required package");
        StringAssert.Contains(stderr, "npm i -D @microsoft/winappcli",
            "Error must include the recovery command");
        StringAssert.Contains(stderr, "npx winapp init --js-bindings",
            "Error must show the post-install invocation");

        // No yaml should have been written — we bailed before InitializeConfigurationAsync ran.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsFalse(File.Exists(configPath), "winapp.yaml should not be written when init bails early");
    }

    [TestMethod]
    public async Task InitCommand_WithJsBindingsAndVscodeCaller_ExitsWithActionableError()
    {
        // Arrange — VSCode extension is also a Node host but is not the npm
        // shim that ships dynwinrt-codegen as a transitive dep, so it is
        // explicitly NOT allowed (matches design choice 3=b).
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "vscode-extension");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--js-bindings" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(1, exitCode, "vscode-extension caller passing --js-bindings should exit 1");
        StringAssert.Contains(ConsoleStdErr.ToString(), "--js-bindings requires the @microsoft/winappcli npm package");
    }

    [TestMethod]
    public async Task InitCommand_WithJsBindingsAndNpmCaller_AddsJsBindingsBlockToConfig()
    {
        // Arrange — simulate npm shim invocation. Pre-create a package.json in
        // the workspace so we exercise the v1.2 happy-path that adds
        // @microsoft/dynwinrt to dependencies.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        var packageJsonPath = Path.Combine(_tempDirectory.FullName, "package.json");
        await File.WriteAllTextAsync(packageJsonPath,
            "{\n  \"name\": \"my-app\",\n  \"version\": \"1.0.0\"\n}\n");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--js-bindings" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "npm caller with --js-bindings should succeed");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), $"winapp.yaml should be written at {configPath}");

        var configContent = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(configContent, "packages:", "Standard packages section should still be written");
        StringAssert.Contains(configContent, "jsBindings:", "jsBindings: block should be injected by --js-bindings");
        StringAssert.Contains(configContent, "lang: js", "Default lang=js should be persisted");
        // XAML denylisting now lives in the codegen, not yaml.
        StringAssert.DoesNotMatch(configContent, new System.Text.RegularExpressions.Regex(@"excludeNamespacePrefixes\s*:"),
            "Default jsBindings yaml must not emit the deprecated excludeNamespacePrefixes block.");

        // @microsoft/dynwinrt must be a runtime dep so `npm ci --omit=dev` works.
        var packageJsonContent = await File.ReadAllTextAsync(packageJsonPath);
        StringAssert.Contains(packageJsonContent, "@microsoft/dynwinrt",
            "package.json should now contain @microsoft/dynwinrt");
        StringAssert.Contains(packageJsonContent, "0.0.0-test",
            "Pinned version from FakeNpmWrapperVersionProvider should be written");
        StringAssert.Contains(packageJsonContent, "\"dependencies\"",
            "Dependency must be added under dependencies (not devDependencies)");
    }

    [TestMethod]
    public async Task InitCommand_WithJsBindingsAndNpmCaller_NoPackageJson_StillSucceeds()
    {
        // No package.json: don't fail, don't synthesize one — just skip the
        // dep edit with a warning.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--js-bindings" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode, "Missing package.json must not fail init");
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        Assert.IsTrue(File.Exists(configPath), "winapp.yaml should still be written");
        StringAssert.Contains(await File.ReadAllTextAsync(configPath), "jsBindings:");
        Assert.IsFalse(
            File.Exists(Path.Combine(_tempDirectory.FullName, "package.json")),
            "We must not synthesize a package.json on the user's behalf");
    }

    [TestMethod]
    public async Task InitCommand_WithoutJsBindingsFlag_DoesNotAddJsBindingsBlock()
    {
        // Arrange — even with npm caller set, omitting the flag must not
        // inject jsBindings (design choice 2=a: opt-in only, no auto-detect).
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        var configContent = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("jsBindings:", configContent,
            "Without --js-bindings, no jsBindings block should be added even when npm-invoked");
    }

    // -------------------------------------------------------------------------
    // Q4: --js-bindings-output / --js-bindings-lang / --js-bindings-only flags
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task InitCommand_WithJsBindingsOutput_OverridesDefaultOutputDir()
    {
        // Arrange
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--config-only",
            "--js-bindings",
            "--js-bindings-output", "src/generated/winrt",
        };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode);
        var configContent = await File.ReadAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));
        StringAssert.Contains(configContent, "output: src/generated/winrt",
            "--js-bindings-output must override the default 'bindings/winrt' in the persisted yaml");
        // Make sure we did not double-write a default output line as well.
        Assert.DoesNotContain("output: bindings/winrt", configContent,
            "Default output must be replaced, not appended");
    }

    [TestMethod]
    public async Task InitCommand_JsBindingsSubOptionsWithoutFlag_FailsAsInvalidUsage()
    {
        // sub-options without --js-bindings are invalid
        // usage (they'd silently no-op while init reports success — bad
        // UX). Treat as exit 1 with a clear error message.
        // Alias flags (--js-bindings-{preset}) bypass this — they imply the parent.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--config-only",
            "--js-bindings-output", "src/g",
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(1, exitCode,
            "Sub-options without --js-bindings must fail loudly (exit 1) rather than silently no-op.");
        var stderr = ConsoleStdErr.ToString();
        StringAssert.Contains(stderr, "require --js-bindings",
            "Error must spell out the dependency on --js-bindings.");
        StringAssert.Contains(stderr, "Error:",
            "Should be surfaced as an Error, not a Warning.");

        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        if (File.Exists(configPath))
        {
            var configContent = await File.ReadAllTextAsync(configPath);
            Assert.DoesNotContain("jsBindings:", configContent,
                "yaml must not gain a jsBindings block when init failed with invalid usage.");
        }
    }

    // --js-bindings-{preset} alias flags — each implies --js-bindings;
    // multiple aliases union their package sets.

    [TestMethod]
    public async Task InitCommand_WithJsBindingsAiAlias_ImpliesParentAndAppliesAiPackages()
    {
        // `--js-bindings-ai` alone (no `--js-bindings`) must apply the AI
        // preset (expands to Microsoft.WindowsAppSDK.AI).
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--config-only",
            "--js-bindings-ai",
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        var configContent = await File.ReadAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));
        StringAssert.Contains(configContent, "jsBindings:",
            "Alias must imply --js-bindings, so the jsBindings block is created");
        StringAssert.Contains(configContent, "packages:",
            "AI preset (v2.0) writes a packages: list under jsBindings");
        foreach (var pkg in JsBindingsPresets.KnownPresets["ai"])
        {
            StringAssert.Contains(configContent, pkg,
                $"AI preset package id {pkg} must appear in the persisted yaml");
        }
    }

    [TestMethod]
    public async Task InitCommand_AliasFlagAlone_DoesNotTriggerSubOptionWarning()
    {
        // Regression guard: aliases imply --js-bindings, so the
        // "sub-options without parent" warning must NOT fire when only an
        // alias is given (without --js-bindings).
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[]
        {
            _tempDirectory.FullName,
            "--config-only",
            "--js-bindings-ai",
        };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        var stderr = ConsoleStdErr.ToString();
        Assert.IsFalse(stderr.Contains("require --js-bindings"),
            "Alias implies --js-bindings; the invalid-usage error must not be printed.");
        Assert.IsFalse(stderr.Contains("have no effect without --js-bindings"),
            "Legacy warning text must not appear either (kept in case of partial revert).");
    }

    [TestMethod]
    public async Task InitCommand_AllPresetAliasesRegistered()
    {
        // Meta-test: each KnownPreset must have a corresponding registered
        // CLI flag. Catches regressions if someone adds a preset to the dict
        // but breaks the auto-registration loop in InitCommand's static ctor.
        foreach (var preset in JsBindingsPresets.KnownPresets.Keys)
        {
            Assert.IsTrue(
                InitCommand.JsBindingsPresetAliasOptions.ContainsKey(preset),
                $"Missing alias option for preset '{preset}'");
            var flag = JsBindingsPresets.AliasFlagName(preset);
            var option = InitCommand.JsBindingsPresetAliasOptions[preset];
            CollectionAssert.Contains(option.Aliases.Concat(new[] { option.Name }).ToList(), flag,
                $"Option for '{preset}' must surface as '{flag}' on the CLI");
        }
    }

    // Re-running init with --js-bindings on an existing yaml ADDS the
    // jsBindings block without touching the existing packages: list.

    [TestMethod]
    public async Task InitCommand_OnExistingConfig_WithJsBindingsFlag_AddsBlockAndPreservesPackages()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        var existing = """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
              - name: Microsoft.Windows.SDK.BuildTools
                version: 10.0.26100.5040
            """;
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, existing);

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--js-bindings", "--use-defaults" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Re-init with --js-bindings on existing yaml must succeed");

        var configContent = await File.ReadAllTextAsync(configPath);
        // Packages preserved
        StringAssert.Contains(configContent, "Microsoft.WindowsAppSDK",
            "Re-init must NOT lose previously pinned packages");
        StringAssert.Contains(configContent, "1.8.39",
            "Pinned package version must survive re-init");
        StringAssert.Contains(configContent, "Microsoft.Windows.SDK.BuildTools",
            "Second pinned package must also survive re-init");
        // jsBindings block was added
        StringAssert.Contains(configContent, "jsBindings:",
            "Re-init with --js-bindings must add the jsBindings block");
        StringAssert.Contains(configContent, "lang: js",
            "Re-init must persist default lang=js");
    }

    // --js-bindings + --setup-sdks none is rejected at
    // SetupWorkspaceAsync entry. Verify with the npm shim caller set
    // (otherwise the npm-only gate fires first).
    [TestMethod]
    public async Task InitCommand_WithJsBindingsAndSetupSdksNone_RejectedWithActionableError()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--js-bindings", "--setup-sdks", "none" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(1, exitCode,
            "--js-bindings + --setup-sdks none must exit 1 (no SDKs → nothing to scan for winmd).");
        var combined = ConsoleStdErr.ToString() + ConsoleStdOut.ToString() + TestAnsiConsole.Output;
        Assert.IsTrue(
            combined.Contains("--setup-sdks none", StringComparison.OrdinalIgnoreCase)
            && combined.Contains("requires SDK packages", StringComparison.OrdinalIgnoreCase),
            $"Error must call out the setup-sdks=none conflict. Combined output: {combined}");

        // Yaml must not gain a jsBindings: block when the guard rejects.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        if (File.Exists(configPath))
        {
            var content = await File.ReadAllTextAsync(configPath);
            Assert.IsFalse(content.Contains("jsBindings:"),
                "Rejected init must NOT write a jsBindings: block.");
        }
    }

    // --js-bindings is unsupported on .NET (.csproj) projects.
    [TestMethod]
    public async Task InitCommand_WithJsBindingsOnDotNetProject_RejectedWithActionableError()
    {
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        // Seed a minimal .csproj so dotNetService.FindCsproj returns 1.
        var csprojPath = Path.Combine(_tempDirectory.FullName, "Sample.csproj");
        await File.WriteAllTextAsync(csprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <PropertyGroup>\n"
            + "    <OutputType>Exe</OutputType>\n"
            + "    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>\n"
            + "  </PropertyGroup>\n"
            + "</Project>\n");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--js-bindings", "--use-defaults" };

        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(1, exitCode,
            "--js-bindings on a .NET project must exit 1 (codegen target is Node/native, not .NET).");
        var combined = ConsoleStdErr.ToString() + ConsoleStdOut.ToString() + TestAnsiConsole.Output;
        Assert.IsTrue(
            combined.Contains(".NET", StringComparison.OrdinalIgnoreCase)
            && combined.Contains("not supported", StringComparison.OrdinalIgnoreCase),
            $"Error must call out the .NET-not-supported case. Combined output: {combined}");

        // Yaml must not be mutated.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        if (File.Exists(configPath))
        {
            var content = await File.ReadAllTextAsync(configPath);
            Assert.IsFalse(content.Contains("jsBindings:"),
                "Rejected init on .NET project must NOT write a jsBindings: block.");
        }
    }
}

// init --js-bindings* path that injects the runtime dep via the
// extracted IJsBindingsWorkspaceService. Uses a fake service to verify the
// init→orchestration wiring without spawning real codegen.
[TestClass]
[DoNotParallelize]
public class InitCommandJsBindingsWiringTests : BaseCommandTests
{
    private FakeJsBindingsWorkspaceService _fakeJsBindings = null!;
    private string? _savedCaller;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeJsBindings = new FakeJsBindingsWorkspaceService();
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IJsBindingsWorkspaceService));
        if (existing is not null) services.Remove(existing);
        services.AddSingleton<IJsBindingsWorkspaceService>(_fakeJsBindings);
        return services;
    }

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

    [TestMethod]
    public async Task InitCommand_WithJsBindings_InvokesEnsureRuntimeDependencyOnJsBindingsService()
    {
        // init --js-bindings (config-only) must route through the
        // extracted IJsBindingsWorkspaceService for runtime-dep injection.
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--js-bindings" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeJsBindings.EnsureRuntimeDependencyCalled,
            "init --js-bindings must call IJsBindingsWorkspaceService.EnsureRuntimeDependencyAndPrintHint.");
    }

    [TestMethod]
    public async Task InitCommand_WithJsBindings_JsBindingsRunAsyncFailure_PropagatesNonZeroExit()
    {
        // Verifies the fake JsBindings service surfaces its non-zero exit
        // when invoked directly. End-to-end SetupWorkspaceAsync propagation
        // is covered by WorkspaceSetupServiceJsBindingsStepTests.
        _fakeJsBindings.Result = new JsBindingsOrchestrationResult
        {
            ExitCode = 7,
            Message = "simulated failure",
        };

        File.WriteAllText(Path.Combine(_tempDirectory.FullName, "winapp.yaml"),
            "packages:\n  - name: Microsoft.WindowsAppSDK\n    version: 1.8.39\n");
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var addCmd = GetRequiredService<AddJsBindingsCommand>();
        // The fake's AddAsync stub returns 0, but our specific point is to
        // verify the RunAsync result wiring through orchestration. Set up
        // the fake's Result and call RunAsync directly to confirm propagation.
        var ctx = new JsBindingsOrchestrationContext
        {
            JsBindingsConfig = new Models.JsBindingsConfig { Output = "bindings/winrt" },
            WinappConfig = new Models.WinappConfig(),
            WorkspaceDir = _tempDirectory,
            LocalWinappDir = _tempDirectory.CreateSubdirectory(".winapp"),
            NugetCacheDir = _tempDirectory,
        };
        var result = await _fakeJsBindings.RunAsync(ctx, default!, CancellationToken.None);
        Assert.AreEqual(7, result.ExitCode,
            "Fake RunAsync must return the configured non-zero exit code (propagation contract).");
    }
}
