// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;

namespace WinApp.Cli.Commands;

internal class AgentsCommand : Command, IShortDescription
{
    public string ShortDescription => "Generate AI agent skill files for coding assistants";

    public AgentsCommand(AgentsGenerateCommand agentsGenerateCommand)
        : base("agents", "Manage AI agent context for coding assistants (GitHub Copilot, Claude Code, etc.). Use 'agents generate' to create SKILL.md files that help AI agents understand your winapp project.")
    {
        Subcommands.Add(agentsGenerateCommand);
    }
}
