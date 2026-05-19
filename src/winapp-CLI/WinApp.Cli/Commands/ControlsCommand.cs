// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class ControlsCommand : Command, IShortDescription, ISuppressesStartupNotices
{
    public string ShortDescription => "Search WinUI 3 controls and Community Toolkit samples";

    public ControlsCommand(
        ControlsSearchCommand searchCommand,
        ControlsGetCommand getCommand,
        ControlsListCommand listCommand,
        ControlsRefreshCommand refreshCommand)
        : base("controls",
            "Search the WinUI 3 Gallery, Windows Community Toolkit, and curated platform patterns " +
            "for grounded XAML and C# code samples. Designed for agent and developer lookup while " +
            "authoring WinUI 3 apps. Data is fetched from GitHub on first use and cached for 7 days.")
    {
        Subcommands.Add(searchCommand);
        Subcommands.Add(getCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(refreshCommand);
    }
}
