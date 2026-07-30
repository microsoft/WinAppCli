// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class MigrateCommand : Command, IShortDescription
{
    public string ShortDescription => "Migrate apps to WinUI 3 / Windows App SDK";

    public MigrateCommand(MigrateScaffoldCommand migrateScaffoldCommand, MigrateValidateCommand migrateValidateCommand)
        : base("migrate", "Migrate apps to WinUI 3 / Windows App SDK. Use 'migrate scaffold --from-uwp' to copy UWP source into a WinUI 3 project and apply mechanical transforms, and 'migrate validate --from-uwp' to gate a completed migration on residue / single-project / manifest checks.")
    {
        Subcommands.Add(migrateScaffoldCommand);
        Subcommands.Add(migrateValidateCommand);
    }
}
