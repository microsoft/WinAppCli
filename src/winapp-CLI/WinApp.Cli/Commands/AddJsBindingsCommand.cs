// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

// `node jsbindings add` — layered, non-destructive. Requires existing
// winapp.yaml; never touches packages: or installs SDK packages.
internal class AddJsBindingsCommand : Command, IShortDescription
{
    public string ShortDescription => "Add JS/TS bindings to an existing workspace";

    // Caller value emitted by the npm winapp shim; required to use this command.
    private const string NpmShimCaller = "nodejs-package";

    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }
    public static Option<string?> OutputOption { get; }
    public static Option<bool> ForceOption { get; }
    public static Option<bool> UseDefaultsOption { get; }

    // Per-preset --{preset} alias flags. Auto-populated from
    // JsBindingsPresets.KnownPresets.
    public static IReadOnlyDictionary<string, Option<bool>> PresetAliasOptions { get; }

    static AddJsBindingsCommand()
    {
        BaseDirectoryArgument = new Argument<DirectoryInfo>("base-directory")
        {
            Description = "Base/root directory for the winapp workspace (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne,
        };
        BaseDirectoryArgument.AcceptExistingOnly();

        ConfigDirOption = new Option<DirectoryInfo>("--config-dir")
        {
            Description = "Directory containing winapp.yaml (default: base-directory)",
        };
        ConfigDirOption.AcceptExistingOnly();

        OutputOption = new Option<string?>("--output")
        {
            Description = "Output directory for generated JS/TS bindings (relative to workspace, default 'bindings/winrt'). "
                + "Persisted to winapp.yaml's jsBindings.output field.",
            HelpName = "PATH",
        };

        ForceOption = new Option<bool>("--force")
        {
            Description = "Patch an existing jsBindings: block without prompting. "
                + "Overwrites only output and (when a preset like --ai is supplied) the packages list; "
                + "all other fields are preserved. Without --force the command refuses to clobber a "
                + "pre-existing block (interactive: prompts; non-interactive: errors).",
        };

        UseDefaultsOption = new Option<bool>("--use-defaults", "--no-prompt")
        {
            Description = "Do not prompt. When jsBindings: already exists in winapp.yaml, "
                + "preserve it and exit 0 (idempotent). Use --force instead if you want "
                + "the existing block patched non-interactively.",
        };

        var aliases = new Dictionary<string, Option<bool>>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in JsBindingsPresets.KnownPresets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var flag = JsBindingsPresets.AddAliasFlagName(preset);
            aliases[preset] = new Option<bool>(flag)
            {
                Description = $"Generate bindings for the '{preset}' slice of the SDK only. "
                    + "For a custom slice that no preset covers, edit winapp.yaml's jsBindings.packages "
                    + $"after adding. Known presets: {JsBindingsPresets.KnownPresetsDisplay()}.",
            };
        }
        PresetAliasOptions = aliases;
    }

    public AddJsBindingsCommand() : base(
        "add",
        "Add a jsBindings: block to winapp.yaml and run codegen. "
        + "Requires winapp.yaml (run 'winapp init' first). Never modifies the packages: section "
        + "or installs SDK packages — codegen runs against the workspace's already-restored packages. "
        + "Refuses to clobber an existing jsBindings: block unless --force is passed. "
        + "Only available when invoked via the @microsoft/winappcli npm package "
        + "(npx winapp node jsbindings add).")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
        Options.Add(OutputOption);
        Options.Add(ForceOption);
        Options.Add(UseDefaultsOption);
        foreach (var aliasOption in PresetAliasOptions.Values)
        {
            Options.Add(aliasOption);
        }
    }

    public class Handler(IJsBindingsWorkspaceService jsBindingsWorkspaceService, ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var configDir = parseResult.GetValue(ConfigDirOption) ?? baseDirectory;
            var output = parseResult.GetValue(OutputOption);
            var force = parseResult.GetValue(ForceOption);
            var useDefaults = parseResult.GetValue(UseDefaultsOption);

            if (force && useDefaults)
            {
                var stderr = parseResult.InvocationConfiguration.Error;
                await stderr.WriteLineAsync(
                    "Error: --force and --use-defaults are mutually exclusive. "
                    + "--force patches an existing jsBindings: block; --use-defaults preserves it. Pick one.");
                return 1;
            }

            // Iteration order is alphabetical (static ctor), so the prefix
            // union below is deterministic regardless of cmdline arg order.
            var enabledPresets = PresetAliasOptions
                .Where(kv => parseResult.GetValue(kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            // Gate behind the npm shim. Same rationale as InitCommand: the
            // codegen tool is an npm transitive dep of @microsoft/winappcli;
            // running this from any other entry point will fail at the
            // codegen invocation with a less actionable error.
            var caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
            if (!string.Equals(caller, NpmShimCaller, StringComparison.Ordinal))
            {
                var stderr = parseResult.InvocationConfiguration.Error;
                await stderr.WriteLineAsync(
                    "Error: 'node jsbindings add' requires the @microsoft/winappcli npm package.");
                await stderr.WriteLineAsync(
                    "JS/TS bindings depend on @microsoft/dynwinrt-codegen, which is installed");
                await stderr.WriteLineAsync(
                    "as a transitive npm dependency. To use this command:");
                await stderr.WriteLineAsync("  npm i -D @microsoft/winappcli");
                await stderr.WriteLineAsync("  npx winapp node jsbindings add");
                return 1;
            }

            var options = new AddJsBindingsOptions
            {
                BaseDirectory = baseDirectory,
                ConfigDir = configDir,
                Output = output,
                Presets = enabledPresets.Count > 0 ? enabledPresets : null,
                Force = force,
                UseDefaults = useDefaults,
            };

            return await jsBindingsWorkspaceService.AddAsync(options, cancellationToken);
        }
    }
}
