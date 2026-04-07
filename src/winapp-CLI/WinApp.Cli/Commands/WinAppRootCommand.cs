// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Commands;

internal class WinAppRootCommand : RootCommand, IShortDescription
{
    public string ShortDescription => "Tools for Windows app development, package identity, packaging, and the Windows (App) SDK";
    internal static Option<bool> VerboseOption = new Option<bool>("--verbose", "-v")
    {
        Description = "Enable verbose output"
    };

    internal static Option<bool> QuietOption = new Option<bool>("--quiet", "-q")
    {
        Description = "Suppress progress messages"
    };

    internal static Option<bool> JsonOption = new Option<bool>("--json")
    {
        Description = "Format output as JSON"
    };

    internal static readonly Option<bool> CliSchemaOption = new("--cli-schema")
    {
        Description = "Output the complete CLI command structure as JSON for tooling, scripting, and LLM integration. Includes all commands, options, arguments, and their descriptions.",
        Arity = ArgumentArity.Zero,
        Recursive = true,
        Hidden = false,
        Action = new PrintCliSchemaAction()
    };

    internal static readonly Option<string?> CallerOption = new("--caller")
    {
        Description = "Identifies the caller (e.g., nuget-package, npm). Used for telemetry.",
        Recursive = true,
        Hidden = true
    };

    private class PrintCliSchemaAction : SynchronousCommandLineAction
    {
        public override bool Terminating => true;

        public override int Invoke(ParseResult parseResult)
        {
            CliSchema.PrintCliSchema(parseResult.CommandResult, parseResult.InvocationConfiguration.Output);
            return 0;
        }
    }

    public WinAppRootCommand(
        InitCommand initCommand,
        RestoreCommand restoreCommand,
        PackageCommand packageCommand,
        ManifestCommand manifestCommand,
        UpdateCommand updateCommand,
        CreateDebugIdentityCommand createDebugIdentityCommand,
        RunCommand runCommand,
        UnregisterCommand unregisterCommand,
        GetWinappPathCommand getWinappPathCommand,
        CertCommand certCommand,
        SignCommand signCommand,
        ToolCommand toolCommand,
        MSStoreCommand msStoreCommand,
        IAnsiConsole ansiConsole,
        CreateExternalCatalogCommand createExternalCatalogCommand,
        NewCommand newCommand) : base("CLI for Windows app development, including package identity, packaging, managing appxmanifest.xml, test certificates, Windows (App) SDK projections, and more. For use with any app framework targeting Windows")
    {
        Subcommands.Add(initCommand);
        Subcommands.Add(newCommand);
        Subcommands.Add(restoreCommand);
        Subcommands.Add(packageCommand);
        Subcommands.Add(manifestCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(createDebugIdentityCommand);
        Subcommands.Add(runCommand);
        Subcommands.Add(unregisterCommand);
        Subcommands.Add(getWinappPathCommand);
        Subcommands.Add(certCommand);
        Subcommands.Add(signCommand);
        Subcommands.Add(toolCommand);
        Subcommands.Add(msStoreCommand);
        Subcommands.Add(createExternalCatalogCommand);

        Options.Add(CliSchemaOption);
        Options.Add(CallerOption);

        // Replace the default help with a custom categorized help screen
        var helpOption = Options.OfType<HelpOption>().First();
        helpOption.Action = new CustomHelpAction(this, ansiConsole,
            ("Setup", [typeof(NewCommand), typeof(InitCommand), typeof(RestoreCommand), typeof(UpdateCommand)]),
            ("Packaging & Signing", [typeof(PackageCommand), typeof(SignCommand), typeof(CertCommand), typeof(ManifestCommand), typeof(CreateExternalCatalogCommand)]),
            ("Development Tools", [typeof(CreateDebugIdentityCommand), typeof(MSStoreCommand), typeof(ToolCommand), typeof(GetWinappPathCommand), typeof(RunCommand), typeof(UnregisterCommand)])
        );
    }
}
