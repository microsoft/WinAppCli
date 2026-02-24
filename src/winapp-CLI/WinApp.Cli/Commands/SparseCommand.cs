// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class SparseCommand : Command, IShortDescription
{
    public string ShortDescription => "Commands for working with MSIX sparse packages.";

    public SparseCommand(CreateExternalCatalogCommand createExternalCatalogCommand)
        : base("sparse", "Commands for working with MSIX sparse packages, including TrustedLaunch external catalog generation.")
    {
        Subcommands.Add(createExternalCatalogCommand);
    }
}
