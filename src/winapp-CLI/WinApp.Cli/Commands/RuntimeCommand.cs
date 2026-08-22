// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal sealed class RuntimeCommand : Command, IShortDescription
{
    public string ShortDescription => "Prepare the Windows App SDK runtime for unpackaged apps";

    public RuntimeCommand(RuntimePrepareCommand prepareCommand)
        : base(
            "runtime",
            "Resolve and prepare an exact framework-dependent Windows App SDK runtime for an unpackaged desktop app. Use 'runtime prepare' to stage the bootstrap DLL and preflight or install the matching runtime.")
    {
        Subcommands.Add(prepareCommand);
    }
}
