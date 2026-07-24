// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Behavioral tests for <see cref="NewCommand"/>'s handler: prerequisite checks, template-pack
/// installation, injection-safe argument construction, exit codes, and JSON output. The handler's
/// <c>dotnet</c> calls are intercepted with a scripted <see cref="FakeDotNetService"/> so no real
/// scaffolding or network access occurs.
/// </summary>
[TestClass]
public class NewCommandHandlerTests : BaseCommandTests
{
    private readonly FakeDotNetService _dotnet = new();

    protected override IServiceCollection ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IDotNetService>(_dotnet);

    /// <summary>Default happy-path responder: SDK present, pack absent-then-installed, scaffold ok.</summary>
    private void ScriptHappyPath(string sdkVersion = "9.0.100")
    {
        _dotnet.RunDotnetArgumentListHandler = args =>
        {
            if (args.Count >= 1 && args[0] == "--version")
            {
                return (0, sdkVersion + "\n", string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "uninstall")
            {
                return (0, string.Empty, string.Empty); // nothing installed yet
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "install")
            {
                return (0, "Success: installed.", string.Empty);
            }
            return (0, "The template was created successfully.", string.Empty); // scaffold
        };
    }

    private IReadOnlyList<string>? ScaffoldInvocation()
        => _dotnet.ArgumentListInvocations
            .FirstOrDefault(a => a.Count >= 2 && a[0] == "new" && a[1] != "install" && a[1] != "uninstall");

    private static JsonElement ParseJson(string output)
    {
        var start = output.IndexOf('{');
        Assert.IsTrue(start >= 0, $"Expected JSON in output but got: {output}");
        try
        {
            return JsonDocument.Parse(output[start..]).RootElement;
        }
        catch (JsonException ex)
        {
            Assert.Fail($"Failed to parse JSON ({ex.Message}). Raw output:\n<<<{output}>>>");
            throw;
        }
    }

    [TestMethod]
    public async Task Handler_HappyPathJson_ReturnsSuccessAndScaffolds()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", "MyApp"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var json = ParseJson(TestAnsiConsole.Output);
        Assert.IsTrue(json.GetProperty("Created").GetBoolean());
        Assert.AreEqual("winui", json.GetProperty("Template").GetString());
        Assert.AreEqual("MyApp", json.GetProperty("Name").GetString());

        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold, "A dotnet new scaffold command should have run.");
        CollectionAssert.Contains(scaffold.ToArray(), "winui");
    }

    [TestMethod]
    public async Task Handler_NameWithInjectionPayload_IsRejected()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();
        const string malicious = "Evil\" --force -o \"C:\\pwned";

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", malicious]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode,
            "A name containing quotes/separators must be rejected outright, not passed to dotnet new.");
        Assert.AreEqual(0, _dotnet.ArgumentListInvocations.Count,
            "An invalid name must fail fast before any dotnet call runs.");
    }

    [TestMethod]
    [DataRow("--force")]
    [DataRow("-o")]
    [DataRow("--dotnet-version")]
    public async Task Handler_OptionShapedName_IsRejected(string optionShapedName)
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", optionShapedName]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode,
            "A leading '-' name would be parsed by dotnet new as one of its own switches, so it must be rejected.");
        Assert.AreEqual(0, _dotnet.ArgumentListInvocations.Count,
            "An option-shaped name must fail fast before any dotnet call runs.");
    }

    [TestMethod]
    public async Task Handler_OutputPathWithSpaces_PassesPathAsSingleToken()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();
        var outDir = Path.Join(_tempDirectory.FullName, "My App Dir");

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--output", outDir]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold);
        var tokens = scaffold.ToArray();
        var outIdx = Array.IndexOf(tokens, "-o");
        Assert.IsTrue(outIdx >= 0 && outIdx + 1 < tokens.Length, "Scaffold should pass -o <path>.");
        Assert.AreEqual(outDir, tokens[outIdx + 1],
            "A path with spaces must be a single verbatim token — proving ArgumentList prevents splitting/injection.");
        CollectionAssert.DoesNotContain(tokens, "--force");
    }

    [TestMethod]
    public async Task Handler_ForceFlag_ForwardsForceToScaffold()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--force"]);

        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold);
        CollectionAssert.Contains(scaffold.ToArray(), "--force");
    }

    [TestMethod]
    public async Task Handler_TemplatePackList_ForcesEnglishLocale()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", "MyApp"]);

        // The pack-list call parses the localized "Version:" label, so it must force English output.
        var listIndex = _dotnet.ArgumentListInvocations
            .Select((args, i) => (args, i))
            .First(x => x.args.Count >= 2 && x.args[0] == "new" && x.args[1] == "uninstall").i;
        var env = _dotnet.ArgumentListEnvironmentInvocations[listIndex];
        Assert.IsNotNull(env, "The pack-list call must pass an environment override to force locale-independent output.");
        Assert.IsTrue(env.TryGetValue("DOTNET_CLI_UI_LANGUAGE", out var lang) && lang == "en",
            "DOTNET_CLI_UI_LANGUAGE must be forced to 'en' so the parsed 'Version:' label is stable across locales.");
    }

    [TestMethod]
    public async Task Handler_OutputDir_DerivesNameFromOutput()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();
        var outDir = Path.Join(_tempDirectory.FullName, "DerivedName");

        await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--output", outDir]);

        var json = ParseJson(TestAnsiConsole.Output);
        Assert.AreEqual("DerivedName", json.GetProperty("Name").GetString());
    }

    [TestMethod]
    public async Task Handler_DotnetNotOnPath_ReturnsSdkMissing()
    {
        _dotnet.RunDotnetArgumentListHandler = args =>
            args.Count >= 1 && args[0] == "--version"
                ? throw new Win32Exception("dotnet not found")
                : (0, string.Empty, string.Empty);
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json"]);

        Assert.AreEqual(NewCommand.ExitSdkMissing, exitCode);
    }

    [TestMethod]
    public async Task Handler_OldSdk_ReturnsSdkMissing()
    {
        ScriptHappyPath(sdkVersion: "8.0.99");
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json"]);

        Assert.AreEqual(NewCommand.ExitSdkMissing, exitCode);
    }

    [TestMethod]
    public async Task Handler_OldSdk_Json_ReportsUpdateReason()
    {
        ScriptHappyPath(sdkVersion: "8.0.99");
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", "MyApp"]);

        Assert.AreEqual(NewCommand.ExitSdkMissing, exitCode);
        var json = ParseJson(TestAnsiConsole.Output);
        Assert.IsFalse(json.GetProperty("Created").GetBoolean());
        var error = json.GetProperty("Error").GetString();
        Assert.IsTrue(error is not null && error.Contains("or newer is required", StringComparison.Ordinal),
            $"An installed-but-too-old SDK must tell JSON callers to UPDATE, not that the SDK is simply missing. Got: {error}");
    }

    [TestMethod]
    public async Task Handler_UnparseableSdk_Json_ReportsDiagnosis()
    {
        ScriptHappyPath(sdkVersion: "not-a-version");
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", "MyApp"]);

        Assert.AreEqual(NewCommand.ExitSdkMissing, exitCode);
        var json = ParseJson(TestAnsiConsole.Output);
        var error = json.GetProperty("Error").GetString();
        Assert.IsTrue(error is not null && error.Contains("Could not determine", StringComparison.Ordinal),
            $"Unparseable SDK output must surface the specific diagnosis in JSON, not a generic 'SDK required'. Got: {error}");
    }

    [TestMethod]
    public async Task Handler_UnparseableSdkVersion_ReturnsSdkMissing()
    {
        ScriptHappyPath(sdkVersion: "not-a-version");
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json"]);

        Assert.AreEqual(NewCommand.ExitSdkMissing, exitCode,
            "Unparseable 'dotnet --version' output must fail the prerequisite, not pass optimistically.");
    }

    [TestMethod]
    public async Task Handler_InvalidTemplateVersion_ReturnsInvalidArgs()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--use-defaults", "--json", "--template-version", "1.0 --add-source http://evil"]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode);
        CollectionAssert.DoesNotContain(
            _dotnet.ArgumentListInvocations.SelectMany(a => a).ToArray(), "--version",
            "Invalid --template-version should fail fast before any dotnet call.");
    }

    [TestMethod]
    public async Task Handler_JsonWithInteractiveConsole_DoesNotPromptAndEmitsCleanJson()
    {
        // TestConsole is interactive (Capabilities.Interactive = true). Without --use-defaults, the
        // handler would normally prompt; under --json those Spectre prompt bytes would precede the JSON
        // and break JSON.parse. JSON mode must imply the default (no-prompt) path.
        Assert.IsTrue(TestAnsiConsole.Profile.Capabilities.Interactive,
            "Test precondition: the console must be interactive to exercise the prompt-suppression path.");
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        // The entire stdout payload must be parseable JSON (no leading prompt text).
        var trimmed = TestAnsiConsole.Output.TrimStart();
        Assert.IsTrue(trimmed.StartsWith('{'),
            $"Under --json the output must begin with JSON, with no interactive prompt text before it. Got:\n{TestAnsiConsole.Output}");
        var json = ParseJson(TestAnsiConsole.Output);
        Assert.IsTrue(json.GetProperty("Created").GetBoolean());
        Assert.AreEqual("WinUIApp", json.GetProperty("Name").GetString(),
            "JSON mode must fall back to the default name instead of prompting.");
    }

    [TestMethod]
    public async Task Handler_TemplatePackInstallFails_ReturnsTemplatePackFailed()
    {
        _dotnet.RunDotnetArgumentListHandler = args =>
        {
            if (args.Count >= 1 && args[0] == "--version")
            {
                return (0, "9.0.100\n", string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "uninstall")
            {
                return (0, string.Empty, string.Empty); // not installed
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "install")
            {
                return (1, string.Empty, "NU1101: package not found"); // install fails
            }
            return (0, string.Empty, string.Empty);
        };
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json"]);

        Assert.AreEqual(NewCommand.ExitTemplatePackFailed, exitCode);
        Assert.IsNull(ScaffoldInvocation(), "Scaffold must not run when the template pack fails to install.");

        // The JSON error must preserve the actual dotnet diagnostic (exit code + stderr) so an agent can
        // tell an unavailable version apart from a feed/network/configuration failure.
        var json = ParseJson(TestAnsiConsole.Output);
        var error = json.GetProperty("Error").GetString();
        Assert.IsTrue(error is not null && error.Contains("NU1101", StringComparison.Ordinal),
            $"The install failure detail (NU1101) must be surfaced in the JSON Error, not a generic message. Got: {error}");
        Assert.IsTrue(error.Contains("exit code 1", StringComparison.Ordinal),
            $"The install exit code must be preserved in the JSON Error. Got: {error}");
    }

    [TestMethod]
    public async Task Handler_ScaffoldFails_ReturnsScaffoldFailed()
    {
        _dotnet.RunDotnetArgumentListHandler = args =>
        {
            if (args.Count >= 1 && args[0] == "--version")
            {
                return (0, "9.0.100\n", string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "uninstall")
            {
                return (0, string.Empty, string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "install")
            {
                return (0, "Success", string.Empty);
            }
            return (1, string.Empty, "template error"); // scaffold fails
        };
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json"]);

        Assert.AreEqual(NewCommand.ExitScaffoldFailed, exitCode);
    }

    [TestMethod]
    public async Task Handler_PackAlreadyInstalledAtRequestedVersion_SkipsInstall()
    {
        _dotnet.RunDotnetArgumentListHandler = args =>
        {
            if (args.Count >= 1 && args[0] == "--version")
            {
                return (0, "9.0.100\n", string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "uninstall")
            {
                return (0, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n   Version: 0.0.6-alpha\n", string.Empty);
            }
            return (0, "created", string.Empty); // scaffold
        };
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        Assert.IsFalse(
            _dotnet.ArgumentListInvocations.Any(a => a.Count >= 2 && a[0] == "new" && a[1] == "install"),
            "A matching installed template-pack version should not be re-installed.");
    }

    [TestMethod]
    public async Task Handler_QuietSuppressesHumanOutput()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--quiet", "--name", "QuietApp"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        Assert.IsFalse(TestAnsiConsole.Output.Contains("QuietApp", StringComparison.Ordinal),
            "--quiet should suppress the human-readable progress and completion output.");
        // The happy path takes the pack-install branch (nothing installed yet), so this also guards the
        // informational "Installing WinUI template pack..." log against leaking under --quiet.
        Assert.IsFalse(TestAnsiConsole.Output.Contains("Installing", StringComparison.Ordinal),
            "--quiet should suppress the template-pack install progress message.");
    }

    [TestMethod]
    public async Task Handler_NameEscapingCurrentDirectory_ReturnsInvalidArgs()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--use-defaults", "--json", "--name", @"..\Escaped"]);

        Assert.AreEqual(NewCommand.ExitInvalidArgs, exitCode,
            "A name containing path separators must be rejected before it becomes the output directory.");
        Assert.AreEqual(0, _dotnet.ArgumentListInvocations.Count,
            "An invalid name must fail fast before any dotnet call runs.");
        Assert.IsNull(ScaffoldInvocation());
    }

    [TestMethod]
    public async Task Handler_AppTemplate_PrintsWinappRunNextStep()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--name", "MyApp"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        Assert.IsTrue(TestAnsiConsole.Output.Contains("winapp run", StringComparison.Ordinal),
            $"App templates should suggest 'winapp run' as the next step. Output:\n{TestAnsiConsole.Output}");
    }

    [TestMethod]
    public async Task Handler_LibTemplate_PrintsReferenceNextStep()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--template", "lib", "--name", "MyLib"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        Assert.IsTrue(TestAnsiConsole.Output.Contains("reference", StringComparison.OrdinalIgnoreCase),
            $"The lib template should not suggest 'winapp run'. Output:\n{TestAnsiConsole.Output}");
        Assert.IsFalse(TestAnsiConsole.Output.Contains("winapp run", StringComparison.Ordinal),
            "A class library is not runnable, so 'winapp run' must not be suggested.");
    }

    [TestMethod]
    public async Task Handler_LibTemplate_PrintsAbsoluteReferencePath()
    {
        ScriptHappyPath();
        // Widen the console so the printed absolute path isn't word-wrapped across lines.
        TestAnsiConsole.Profile.Width = 500;
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--template", "lib", "--name", "MyLib"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var expectedPath = Path.Join(_tempDirectory.FullName, "MyLib", "MyLib.csproj");
        Assert.IsTrue(TestAnsiConsole.Output.Contains(expectedPath, StringComparison.Ordinal),
            $"The lib next step must print the library's absolute csproj path so 'dotnet add reference' resolves from any app project directory (not a sibling-relative path). Expected '{expectedPath}'. Output:\n{TestAnsiConsole.Output}");
    }

    [TestMethod]
    public async Task Handler_UnitTestTemplate_PrintsPackagedRunNextStep()
    {
        ScriptHappyPath();
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--template", "unittest", "--name", "MyTests"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        Assert.IsTrue(TestAnsiConsole.Output.Contains("winapp run", StringComparison.Ordinal),
            $"The unittest template is a packaged app; its tests run when launched with 'winapp run'. Output:\n{TestAnsiConsole.Output}");
        Assert.IsFalse(TestAnsiConsole.Output.Contains("dotnet test", StringComparison.OrdinalIgnoreCase)
            && !TestAnsiConsole.Output.Contains("not via", StringComparison.OrdinalIgnoreCase),
            "The unittest next step must not recommend 'dotnet test' — the packaged MSTest app runs its tests on launch.");
    }

    [TestMethod]
    public async Task Handler_ScaffoldPinsTargetFrameworkToInstalledSdk()
    {
        ScriptHappyPath(sdkVersion: "8.0.400");
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", "MyApp"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold);
        var tokens = scaffold.ToArray();
        var idx = Array.IndexOf(tokens, "--dotnet-version");
        Assert.IsTrue(idx >= 0 && idx + 1 < tokens.Length,
            "An accepted .NET 8 SDK must pin the scaffold to its own target framework, not the template's net10.0 default.");
        Assert.AreEqual("net8.0", tokens[idx + 1],
            "The scaffolded project must target the installed SDK's framework so it can be built.");
    }

    [TestMethod]
    public async Task Handler_ScaffoldOmitsFrameworkPinForNewerSdk()
    {
        ScriptHappyPath(sdkVersion: "11.0.100");
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--use-defaults", "--json", "--name", "MyApp"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold);
        CollectionAssert.DoesNotContain(scaffold.ToArray(), "--dotnet-version",
            "For an SDK newer than the templates' choices, omit --dotnet-version and let the template auto-detect.");
    }

    [TestMethod]
    public async Task Handler_OlderPackInstalled_InstallsRequestedVersionBeforeScaffold()
    {
        _dotnet.RunDotnetArgumentListHandler = args =>
        {
            if (args.Count >= 1 && args[0] == "--version")
            {
                return (0, "9.0.100\n", string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "uninstall")
            {
                // An OLDER version is installed than the one requested below.
                return (0, "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n   Version: 0.0.5-alpha\n", string.Empty);
            }
            if (args.Count >= 2 && args[0] == "new" && args[1] == "install")
            {
                return (0, "Success", string.Empty);
            }
            return (0, "created", string.Empty); // scaffold
        };
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["--use-defaults", "--json", "--template-version", "0.0.6-alpha"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var install = _dotnet.ArgumentListInvocations
            .FirstOrDefault(a => a.Count >= 3 && a[0] == "new" && a[1] == "install");
        Assert.IsNotNull(install, "An older installed pack must be upgraded to the requested version.");
        Assert.AreEqual($"{NewCommand.TemplatePackageId}::0.0.6-alpha", install[2],
            "The requested (newer) template-pack version must be the one installed.");
    }

    [TestMethod]
    public async Task Handler_NonInteractiveWithoutUseDefaults_FallsBackToDefaults()
    {
        ScriptHappyPath();
        TestAnsiConsole.Profile.Capabilities.Interactive = false;
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["--json"]);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var json = ParseJson(TestAnsiConsole.Output);
        Assert.AreEqual("WinUIApp", json.GetProperty("Name").GetString(),
            "A non-interactive host must default the name without prompting.");
        Assert.AreEqual("winui", json.GetProperty("Template").GetString());
    }

    [TestMethod]
    public async Task Handler_InteractivePrompts_UsePromptedValues()
    {
        ScriptHappyPath();
        // The test console is interactive by default (BaseCommandTests). Accept the first template
        // (blank) and type a name so we can prove prompted values flow into the dotnet new args.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);        // select first template (blank)
        TestAnsiConsole.Input.PushTextWithEnter("PromptedApp"); // name prompt
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold);
        var tokens = scaffold.ToArray();
        CollectionAssert.Contains(tokens, "winui");
        var nameIdx = Array.IndexOf(tokens, "-n");
        Assert.IsTrue(nameIdx >= 0 && nameIdx + 1 < tokens.Length);
        Assert.AreEqual("PromptedApp", tokens[nameIdx + 1],
            "The prompted name must be passed to dotnet new.");
    }

    [TestMethod]
    public async Task Handler_InteractiveInvalidName_Reprompts_ThenUsesValidName()
    {
        ScriptHappyPath();
        // The name prompt reuses IsValidProjectName, so an invalid interactive entry must be rejected
        // in place (re-prompt) rather than accepted and then failing the whole wizard afterwards.
        TestAnsiConsole.Input.PushKey(ConsoleKey.Enter);            // select first template (blank)
        TestAnsiConsole.Input.PushTextWithEnter(@"..\Escaped");    // invalid: path separators / traversal
        TestAnsiConsole.Input.PushTextWithEnter("CON");            // invalid: reserved device name
        TestAnsiConsole.Input.PushTextWithEnter("ValidApp");       // valid: accepted, ends the prompt
        var command = GetRequiredService<NewCommand>();

        var exitCode = await ParseAndInvokeWithCaptureAsync(command, []);

        Assert.AreEqual(NewCommand.ExitSuccess, exitCode);
        var scaffold = ScaffoldInvocation();
        Assert.IsNotNull(scaffold, "Scaffolding should run once a valid name is entered.");
        var tokens = scaffold.ToArray();
        var nameIdx = Array.IndexOf(tokens, "-n");
        Assert.IsTrue(nameIdx >= 0 && nameIdx + 1 < tokens.Length);
        Assert.AreEqual("ValidApp", tokens[nameIdx + 1],
            "Only the corrected, valid name should reach dotnet new — the invalid entries must be re-prompted.");
    }
}
