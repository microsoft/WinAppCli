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

    internal static readonly Option<bool> CliSchemaOption = new("--cli-schema")
    {
        Description = "Output the complete CLI command structure as JSON for tooling, scripting, and LLM integration. Includes all commands, options, arguments, and their descriptions.",
        Arity = ArgumentArity.Zero,
        Recursive = true,
        Hidden = false,
        Action = new PrintCliSchemaAction()
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
        GetWinappPathCommand getWinappPathCommand,
        CertCommand certCommand,
        SignCommand signCommand,
        ToolCommand toolCommand,
        MSStoreCommand msStoreCommand,
        IAnsiConsole ansiConsole,
        CreateExternalCatalogCommand createExternalCatalogCommand) : base("CLI for Windows app development, including package identity, packaging, managing appxmanifest.xml, test certificates, Windows (App) SDK projections, and more. For use with any app framework targeting Windows")
    {
        Subcommands.Add(initCommand);
        Subcommands.Add(restoreCommand);
        Subcommands.Add(packageCommand);
        Subcommands.Add(manifestCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(createDebugIdentityCommand);
        Subcommands.Add(getWinappPathCommand);
        Subcommands.Add(certCommand);
        Subcommands.Add(signCommand);
        Subcommands.Add(toolCommand);
        Subcommands.Add(msStoreCommand);
        Subcommands.Add(createExternalCatalogCommand);

        Options.Add(CliSchemaOption);

        // Replace the default help with a custom categorized help screen
        var helpOption = Options.OfType<HelpOption>().First();
        helpOption.Action = new CustomHelpAction(this, ansiConsole,
            ("Setup", [typeof(InitCommand), typeof(RestoreCommand), typeof(UpdateCommand)]),
            ("Packaging & Signing", [typeof(PackageCommand), typeof(SignCommand), typeof(CertCommand), typeof(ManifestCommand), typeof(CreateExternalCatalogCommand)]),
            ("Development Tools", [typeof(CreateDebugIdentityCommand), typeof(MSStoreCommand), typeof(ToolCommand), typeof(GetWinappPathCommand)])
        );
    }
}
