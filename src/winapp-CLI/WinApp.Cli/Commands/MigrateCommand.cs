// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class MigrateCommand : Command, IShortDescription
{
    public string ShortDescription => "Migrate apps to WinUI 3 / Windows App SDK";

    public MigrateCommand(MigrateAnalyzeCommand migrateAnalyzeCommand)
        : base("migrate", "Migrate apps to WinUI 3 / Windows App SDK. Use 'migrate analyze --from-uwp' to produce a source-only migration plan (JSON) from UWP source without building the project.")
    {
        Subcommands.Add(migrateAnalyzeCommand);
    }
}
