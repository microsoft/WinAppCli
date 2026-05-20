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

// Npm-caller bindings prompt — only fires under WINAPP_CLI_CALLER=nodejs-package,
// and only when there's no existing jsBindings: block to preserve. Default Both
// under --use-defaults, no prompt for native callers. [DoNotParallelize] because
// tests mutate that process-wide env var.
[TestClass]
[DoNotParallelize]
public class InitCommandBindingsPromptTests : BaseCommandTests
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
    public async Task InitCommand_NpmCallerWithUseDefaults_AddsJsBindingsBlock()
    {
        // Default for npm caller under --use-defaults is "Both", so jsBindings
        // must land in winapp.yaml without any explicit flag.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"my-app","version":"1.0.0"}""");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--use-defaults" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        var configContent = await File.ReadAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));
        StringAssert.Contains(configContent, "jsBindings:",
            "npm caller + --use-defaults defaults to Both, so jsBindings: must be added");
        Assert.IsFalse(configContent.Contains("cppProjections: false"),
            "Both mode keeps cppProjections at default (true); explicit false should not be written");
    }

    [TestMethod]
    public async Task InitCommand_NativeCallerWithUseDefaults_OmitsJsBindingsBlock()
    {
        // Standalone CLI (winget) keeps historical C++-only behavior even
        // under --use-defaults — no prompt, no jsBindings.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", null);

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--use-defaults" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        var configContent = await File.ReadAllTextAsync(Path.Combine(_tempDirectory.FullName, "winapp.yaml"));
        Assert.IsFalse(configContent.Contains("jsBindings:"),
            "Native caller must not gain a jsBindings block — bindings prompt only fires for npm caller");
    }

    [TestMethod]
    public async Task InitCommand_NpmCallerOnDotNetProject_SilentlyFallsBackToCppOnly()
    {
        // .NET projects can't host JS bindings; rather than asking a question
        // with only one valid answer (and tripping the .NET guard when
        // --use-defaults → Both), the prompt path silently downgrades to
        // CppOnly so dotnet sample tests / CI succeed without intervention.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        var csprojPath = Path.Combine(_tempDirectory.FullName, "Sample.csproj");
        await File.WriteAllTextAsync(csprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <PropertyGroup>\n"
            + "    <OutputType>Exe</OutputType>\n"
            + "    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>\n"
            + "  </PropertyGroup>\n"
            + "</Project>\n");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--use-defaults", "--config-only" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode,
            ".NET project + npm caller + --use-defaults must succeed (silently downgraded to CppOnly).");
        var combined = ConsoleStdErr.ToString() + ConsoleStdOut.ToString() + TestAnsiConsole.Output;
        Assert.IsFalse(
            combined.Contains("JS/TS bindings are not supported on .NET", StringComparison.OrdinalIgnoreCase),
            $".NET projects must not surface the JS-bindings rejection error — the prompt silently picks CppOnly. Combined output: {combined}");
        // winapp.yaml may or may not be written depending on the SDK install
        // mode chosen for .NET projects (which is typically None — .NET pulls
        // SDK via NuGet). The key invariant is that, if one is written, it
        // must NOT contain a jsBindings block.
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        if (File.Exists(configPath))
        {
            var configContent = await File.ReadAllTextAsync(configPath);
            Assert.IsFalse(configContent.Contains("jsBindings:"),
                ".NET project must not gain a jsBindings block — the prompt path silently picks CppOnly.");
        }
    }

    [TestMethod]
    public async Task InitCommand_NpmCallerOnDotNetProjectWithHandEditedJsBindings_RejectedWithActionableError()
    {
        // Defense-in-depth: if a user manually adds a jsBindings: block to a
        // .NET project's winapp.yaml, the .NET guard must still fire with an
        // actionable message rather than letting codegen produce junk.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        var csprojPath = Path.Combine(_tempDirectory.FullName, "Sample.csproj");
        await File.WriteAllTextAsync(csprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <PropertyGroup>\n"
            + "    <OutputType>Exe</OutputType>\n"
            + "    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>\n"
            + "  </PropertyGroup>\n"
            + "</Project>\n");
        var existing = """
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
              - name: Microsoft.Windows.SDK.BuildTools
                version: 10.0.26100.5040
            jsBindings:
              output: bindings/winrt
              lang: js
            """;
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, existing);

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--use-defaults" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(1, exitCode,
            "Hand-edited jsBindings: on a .NET project must be rejected by the .NET guard.");
        var combined = ConsoleStdErr.ToString() + ConsoleStdOut.ToString() + TestAnsiConsole.Output;
        Assert.IsTrue(
            combined.Contains(".NET", StringComparison.OrdinalIgnoreCase)
            && combined.Contains("not supported", StringComparison.OrdinalIgnoreCase),
            $"Error must call out the .NET-not-supported case. Combined output: {combined}");
    }

    [TestMethod]
    public async Task InitCommand_NpmCallerWithSetupSdksNone_RejectsBecauseBindingsNeedSdks()
    {
        // npm-caller + --use-defaults requests Both, which needs SDK packages
        // for the winmd source. --setup-sdks none conflicts → exit 1.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--use-defaults", "--setup-sdks", "none" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(1, exitCode,
            "--setup-sdks none + JS bindings (via npm caller default Both) must exit 1.");
        var combined = ConsoleStdErr.ToString() + ConsoleStdOut.ToString() + TestAnsiConsole.Output;
        Assert.IsTrue(
            combined.Contains("none", StringComparison.OrdinalIgnoreCase)
            && combined.Contains("SDK packages", StringComparison.OrdinalIgnoreCase),
            $"Error must call out the setup-sdks=none conflict. Combined output: {combined}");
    }

    [TestMethod]
    public async Task InitCommand_NpmCallerWithExistingJsBindings_PreservesUserChoice()
    {
        // Existing yaml that already declares jsBindings: must not be
        // re-prompted; the existing choice (here: JS-only via
        // cppProjections: false) round-trips through init.
        Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "nodejs-package");
        var existing = """
            cppProjections: false
            packages:
              - name: Microsoft.WindowsAppSDK
                version: 1.8.39
              - name: Microsoft.Windows.SDK.BuildTools
                version: 10.0.26100.5040
            jsBindings:
              output: bindings/winrt
              lang: js
            """;
        var configPath = Path.Combine(_tempDirectory.FullName, "winapp.yaml");
        await File.WriteAllTextAsync(configPath, existing);

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--use-defaults" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        var configContent = await File.ReadAllTextAsync(configPath);
        StringAssert.Contains(configContent, "jsBindings:",
            "Existing jsBindings block must survive re-init.");
        StringAssert.Contains(configContent, "Microsoft.WindowsAppSDK",
            "Existing pinned packages must survive re-init.");
    }
}

// Verifies the init → orchestration wiring delivers the runtime-dep
// injection call once the npm-caller prompt opts into JS bindings.
[TestClass]
[DoNotParallelize]
public class InitCommandBindingsWiringTests : BaseCommandTests
{
    private FakeJsBindingsWorkspaceService _fakeJsBindings = null!;
    private string? _savedCaller;

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
    {
        _fakeJsBindings = new FakeJsBindingsWorkspaceService();
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IJsBindingsWorkspaceService));
        if (existing is not null)
        {
            services.Remove(existing);
        }
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
    public async Task InitCommand_NpmCallerWithUseDefaults_InvokesEnsureRuntimeDependency()
    {
        // npm caller + --use-defaults → Both → must wire @microsoft/dynwinrt.
        File.WriteAllText(
            Path.Combine(_tempDirectory.FullName, "package.json"),
            """{"name":"app","version":"1.0.0","dependencies":{}}""");

        var initCommand = GetRequiredService<InitCommand>();
        var args = new[] { _tempDirectory.FullName, "--config-only", "--use-defaults" };
        var exitCode = await ParseAndInvokeWithCaptureAsync(initCommand, args);

        Assert.AreEqual(0, exitCode);
        Assert.IsTrue(_fakeJsBindings.EnsureRuntimeDependencyCalled,
            "Default Both for npm caller must call IJsBindingsWorkspaceService.EnsureRuntimeDependencyAndPrintHint.");
    }
}
