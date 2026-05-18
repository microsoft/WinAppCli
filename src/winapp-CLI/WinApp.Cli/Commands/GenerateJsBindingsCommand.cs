// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

// `node jsbindings generate` — read-only codegen. Loads the existing
// jsBindings: block from winapp.yaml and runs dynwinrt-codegen against
// the workspace's already-restored packages. Does NOT mutate yaml.
internal class GenerateJsBindingsCommand : Command, IShortDescription
{
    private const string NpmShimCaller = "nodejs-package";

    public string ShortDescription => "Re-run codegen against the existing jsBindings: block";

    public static Argument<DirectoryInfo> BaseDirectoryArgument { get; }
    public static Option<DirectoryInfo> ConfigDirOption { get; }

    static GenerateJsBindingsCommand()
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
    }

    public GenerateJsBindingsCommand() : base(
        "generate",
        "Re-run dynwinrt-codegen against the existing jsBindings: block in winapp.yaml. "
        + "Does NOT modify the yaml — for that, use 'node jsbindings add'. "
        + "Errors if no jsBindings: block is declared. "
        + "Only available when invoked via the @microsoft/winappcli npm package "
        + "(npx winapp node jsbindings generate).")
    {
        Arguments.Add(BaseDirectoryArgument);
        Options.Add(ConfigDirOption);
    }

    public class Handler(IJsBindingsWorkspaceService jsBindingsWorkspaceService, ICurrentDirectoryProvider currentDirectoryProvider) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var baseDirectory = parseResult.GetValue(BaseDirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var configDir = parseResult.GetValue(ConfigDirOption) ?? baseDirectory;

            var caller = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
            if (!string.Equals(caller, NpmShimCaller, StringComparison.Ordinal))
            {
                var stderr = parseResult.InvocationConfiguration.Error;
                await stderr.WriteLineAsync(
                    "Error: 'node jsbindings generate' requires the @microsoft/winappcli npm package.");
                await stderr.WriteLineAsync(
                    "JS/TS bindings depend on @microsoft/dynwinrt-codegen, which is installed");
                await stderr.WriteLineAsync(
                    "as a transitive npm dependency. To use this command:");
                await stderr.WriteLineAsync("  npm i -D @microsoft/winappcli");
                await stderr.WriteLineAsync("  npx winapp node jsbindings generate");
                return 1;
            }

            var options = new GenerateJsBindingsOptions
            {
                BaseDirectory = baseDirectory,
                ConfigDir = configDir,
            };

            return await jsBindingsWorkspaceService.GenerateAsync(options, cancellationToken);
        }
    }
}
