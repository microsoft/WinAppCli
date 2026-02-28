// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class AgentsGenerateCommand : Command, IShortDescription
{
    public string ShortDescription => "Generate SKILL.md files for AI coding agents";

    public static Option<DirectoryInfo> SkillsDirOption { get; }
    public static Option<DirectoryInfo> DirectoryOption { get; }

    static AgentsGenerateCommand()
    {
        SkillsDirOption = new Option<DirectoryInfo>("--skills-dir")
        {
            Description = "Skills directory override (default: auto-detect from .github/skills, .agents/skills, or .claude/skills)"
        };

        DirectoryOption = new Option<DirectoryInfo>("--directory")
        {
            Description = "Project root directory (default: current directory)"
        };
        DirectoryOption.AcceptExistingOnly();
    }

    public AgentsGenerateCommand()
        : base("generate", "Generate SKILL.md files that help AI coding agents (GitHub Copilot, Claude Code, etc.) understand your winapp project. Skills are placed in the detected skills directory (.github/skills/, .agents/skills/, or .claude/skills/). Use --skills-dir to override.")
    {
        Options.Add(SkillsDirOption);
        Options.Add(DirectoryOption);
    }

    public class Handler(
        IAgentContextService agentContextService,
        ICurrentDirectoryProvider currentDirectoryProvider,
        IStatusService statusService) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var skillsDir = parseResult.GetValue(SkillsDirOption);
            var directory = parseResult.GetValue(DirectoryOption) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();

            return await statusService.ExecuteWithStatusAsync<string>(
                "Generating winapp agent skills...",
                async (context, ct) =>
                {
                    var result = await agentContextService.GenerateSkillsAsync(directory, skillsDir, ct);

                    if (!result.Success)
                    {
                        return (1, "Failed to generate agent skills.");
                    }

                    foreach (var skill in result.GeneratedSkills)
                    {
                        context.AddStatusMessage($"{UiSymbols.Check} {skill}");
                    }

                    var summary = $"{result.GeneratedSkills.Count} skills generated in [underline]{result.SkillsDirectory}[/]";
                    return (0, summary);
                },
                cancellationToken);
        }
    }
}
