// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UpgradeCommand : Command, IShortDescription
{
    public string ShortDescription => "Update the winapp CLI to the latest version";

    public static Option<bool> ForceOption { get; } = new("--force")
    {
        Description = "Force the upgrade even if the current version appears up to date"
    };

    public UpgradeCommand() : base("upgrade", "Check for and install the latest version of the winapp CLI. For MSIX installs, downloads and installs the latest MSIX. For standalone exe installs, downloads and swaps the executable. For npm or NuGet installs, shows instructions for using the package manager.")
    {
        Options.Add(ForceOption);
    }

    public class Handler(ICliUpgradeService cliUpgradeService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var force = parseResult.GetRequiredValue(ForceOption);
            return await cliUpgradeService.UpgradeAsync(force, cancellationToken);
        }
    }
}
