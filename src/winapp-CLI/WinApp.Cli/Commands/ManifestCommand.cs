// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class ManifestCommand : Command
{
    public ManifestCommand(ManifestGenerateCommand manifestGenerateCommand, ManifestUpdateAssetsCommand manifestUpdateAssetsCommand, ManifestValidateCommand manifestValidateCommand)
        : base("manifest", "Create and modify appxmanifest.xml files for package identity and MSIX packaging. Use 'manifest generate' to create a new manifest, 'manifest update-assets' to regenerate app icons from a source image, or 'manifest validate' to check if a manifest is valid.")
    {
        Subcommands.Add(manifestGenerateCommand);
        Subcommands.Add(manifestUpdateAssetsCommand);
        Subcommands.Add(manifestValidateCommand);
    }
}
