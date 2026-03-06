// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class ManifestCommand : Command, IShortDescription
{
    public string ShortDescription => "Create and modify appxmanifest.xml files";

    public ManifestCommand(ManifestGenerateCommand manifestGenerateCommand, ManifestUpdateAssetsCommand manifestUpdateAssetsCommand, ManifestAddAliasCommand manifestAddAliasCommand)
        : base("manifest", "Create and modify appxmanifest.xml files for package identity and MSIX packaging. Use 'manifest generate' to create a new manifest, 'manifest update-assets' to regenerate app icons, or 'manifest add-alias' to add an execution alias.")
    {
        Subcommands.Add(manifestGenerateCommand);
        Subcommands.Add(manifestUpdateAssetsCommand);
        Subcommands.Add(manifestAddAliasCommand);
    }
}
