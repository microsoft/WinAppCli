// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class InitCommand : Command, IShortDescription
{
    public string ShortDescription => "Initialize existing project with manifest and/or SDK packages";

    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    public static Option<SdkInstallMode?> SetupSdksOption { get; }
    public static Option<bool> IgnoreConfigOption { get; }
    public static Option<bool> NoGitignoreOption { get; }
    public static Option<bool> UseDefaults { get; }
    public static Option<bool> ConfigOnlyOption { get; }
    public static Option<bool> JsBindingsOption { get; }
    public static Option<string?> JsBindingsOutputOption { get; }
    public static Option<string?> JsBindingsLangOption { get; }

    // Per-preset alias flags (e.g. --js-bindings-ai). Auto-populated from
    // JsBindingsPresets.KnownPresets; each implies --js-bindings, and
    // multiple combine via union.
    public static IReadOnlyDictionary<string, Option<bool>> JsBindingsPresetAliasOptions { get; }

    // WINAPP_CLI_CALLER emitted by the npm shim. --js-bindings requires this
    // caller because codegen ships as an npm transitive dep.
    private const string NpmShimCaller = "nodejs-package";

    static InitCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winapp workspace, for consumption or installation.",
            Arity = ArgumentArity.ZeroOrOne
        };
        BaseDirectoryArgument.AcceptExistingOnly();
        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory to read/store configuration (default: current directory)"
        };
        ConfigDirOption.AcceptExistingOnly();
        SetupSdksOption = new Option<SdkInstallMode?>("--setup-sdks")
        {
            Description = "SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation)",
            HelpName = "stable|preview|experimental|none"
        };
        IgnoreConfigOption = new Option<bool>("--ignore-config", "--no-config")
        {
            Description = "Don't use configuration file for version management"
        };
        NoGitignoreOption = new Option<bool>("--no-gitignore")
        {
            Description = "Don't update .gitignore file"
        };
        UseDefaults = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Do not prompt, and use default of all prompts"
        };
        ConfigOnlyOption = new Option<bool>("--config-only")
        {
            Description = "Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps."
        };
        JsBindingsOption = new Option<bool>("--js-bindings")
        {
            Description = "Generate JS/TS bindings via dynwinrt-codegen on top of the standard init flow. "
                + "Adds a 'jsBindings:' block to winapp.yaml so the binding generator runs as part of init/restore. "
                + "Only available when invoked via the @microsoft/winappcli npm package (npx winapp init --js-bindings)."
        };
        JsBindingsOutputOption = new Option<string?>("--js-bindings-output")
        {
            Description = "Override the output directory for generated JS/TS bindings (relative to workspace, default 'bindings/winrt'). "
                + "Only takes effect together with --js-bindings on a fresh init; ignored on re-init when winapp.yaml already declares jsBindings:.",
            HelpName = "PATH",
        };
        JsBindingsLangOption = new Option<string?>("--js-bindings-lang")
        {
            Description = "Override the JS bindings language. Currently only 'js' is supported (which emits both .js and .d.ts). "
                + "Reserved for forward-compat; see --js-bindings-output for activation rules.",
            HelpName = "js",
        };
        JsBindingsLangOption.AcceptOnlyFromAmong("js");

        // One --js-bindings-{preset} flag per preset, alphabetised.
        var aliases = new Dictionary<string, Option<bool>>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in JsBindingsPresets.KnownPresets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var flag = JsBindingsPresets.AliasFlagName(preset);
            aliases[preset] = new Option<bool>(flag)
            {
                Description = $"Generate bindings for the '{preset}' slice of the SDK. "
                    + "Implies --js-bindings (no need to pass it separately). "
                    + "For a custom slice that no preset covers, edit winapp.yaml "
                    + "and write your own packages: list under jsBindings. "
                    + $"Known presets: {JsBindingsPresets.KnownPresetsDisplay()}.",
            };
        }
        JsBindingsPresetAliasOptions = aliases;
    }

    public InitCommand() : base("init", "Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, downloads Windows SDK and Windows App SDK packages, and generates projections. When SDK packages are managed (--setup-sdks stable/preview/experimental), also creates winapp.yaml to pin versions for 'restore'/'update'; with --setup-sdks none (e.g., for Rust/Tauri projects that bring their own SDK bindings), no winapp.yaml is created. Interactive by default (use --use-defaults to skip prompts). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(SetupSdksOption);
        Options.Add(IgnoreConfigOption);
        Options.Add(NoGitignoreOption);
        Options.Add(UseDefaults);
        Options.Add(ConfigOnlyOption);
        Options.Add(JsBindingsOption);
        Options.Add(JsBindingsOutputOption);
        Options.Add(JsBindingsLangOption);
        foreach (var aliasOption in JsBindingsPresetAliasOptions.Values)
        {
            Options.Add(aliasOption);
        }
    }

    public class Handler(IWorkspaceSetupService workspaceSetupService, ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument);
            var configDir = parseResult.GetValue(ConfigDirOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var setupSdks = parseResult.GetValue(SetupSdksOption);
            var ignoreConfig = parseResult.GetValue(IgnoreConfigOption);
            var noGitignore = parseResult.GetValue(NoGitignoreOption);
            var useDefaults = parseResult.GetValue(UseDefaults);
            var configOnly = parseResult.GetValue(ConfigOnlyOption);
            var jsBindings = parseResult.GetValue(JsBindingsOption);
            var jsBindingsOutput = parseResult.GetValue(JsBindingsOutputOption);
            var jsBindingsLang = parseResult.GetValue(JsBindingsLangOption);

            // Iteration order is alphabetical(registration order), so the
            // resulting prefix union is deterministic.
            var enabledAliases = JsBindingsPresetAliasOptions
                .Where(kv => parseResult.GetValue(kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            // Aliases imply --js-bindings (the whole point of the shorthand).
            // Promote BEFORE the warning check below.
            if (enabledAliases.Count > 0 && !jsBindings)
            {
                jsBindings = true;
            }

            if (!jsBindings &&
                (!string.IsNullOrWhiteSpace(jsBindingsOutput)
                 || !string.IsNullOrWhiteSpace(jsBindingsLang)))
            {
                var stderr = parseResult.InvocationConfiguration.Error;
                await stderr.WriteLineAsync(
                    "Error: --js-bindings-output / --js-bindings-lang require --js-bindings. "
                    + "Add --js-bindings (or one of the alias flags like --js-bindings-ai) to enable bindings generation.");
                return 1;
            }

            // Gate --js-bindings behind the npm shim — codegen ships as an
            // npm transitive dep of @microsoft/winappcli, so non-npm callers
            // would silently produce a broken workspace.
            if (jsBindings)
            {
                var caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
                if (!string.Equals(caller, NpmShimCaller, StringComparison.Ordinal))
                {
                    var stderr = parseResult.InvocationConfiguration.Error;
                    await stderr.WriteLineAsync(
                        "Error: --js-bindings requires the @microsoft/winappcli npm package.");
                    await stderr.WriteLineAsync(
                        "JS/TS bindings depend on @microsoft/dynwinrt-codegen, which is installed");
                    await stderr.WriteLineAsync(
                        "as a transitive npm dependency. To use this flag:");
                    await stderr.WriteLineAsync("  npm i -D @microsoft/winappcli");
                    await stderr.WriteLineAsync("  npx winapp init --js-bindings");
                    return 1;
                }
            }

            var options = new WorkspaceSetupOptions
            {
                BaseDirectory = baseDirectory ?? currentDirectoryProvider.GetCurrentDirectoryInfo(),
                ConfigDir = configDir,
                SdkInstallMode = setupSdks,
                IgnoreConfig = ignoreConfig,
                NoGitignore = noGitignore,
                UseDefaults = useDefaults,
                RequireExistingConfig = false,
                ForceLatestBuildTools = true,
                ConfigOnly = configOnly,
                AddJsBindings = jsBindings,
                JsBindingsOutputOverride = jsBindings ? jsBindingsOutput : null,
                JsBindingsLangOverride = jsBindings ? jsBindingsLang : null,
                JsBindingsPresets = jsBindings && enabledAliases.Count > 0 ? enabledAliases : null,
            };

            return await workspaceSetupService.SetupWorkspaceAsync(options, cancellationToken);
        }
    }
}
