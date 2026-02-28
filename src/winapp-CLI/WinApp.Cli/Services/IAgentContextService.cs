// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Service for generating agent context skill files in user projects.
/// Reads embedded SKILL.md files and writes them to the appropriate skills directory.
/// </summary>
internal interface IAgentContextService
{
    /// <summary>
    /// Generate agent skill files in the target project directory.
    /// </summary>
    /// <param name="projectDirectory">Root directory of the user's project</param>
    /// <param name="skillsDir">Optional explicit skills directory override</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the skills directory used and skills generated</returns>
    Task<AgentContextResult> GenerateSkillsAsync(DirectoryInfo projectDirectory, DirectoryInfo? skillsDir, CancellationToken cancellationToken);

    /// <summary>
    /// Check whether winapp skills already exist in the project.
    /// Returns true if any known skills directory contains a winapp-cli subfolder.
    /// </summary>
    bool SkillsExistInProject(DirectoryInfo projectDirectory);
}

/// <summary>
/// Result of agent skills generation
/// </summary>
internal record AgentContextResult(
    string SkillsDirectory,
    IReadOnlyList<string> GeneratedSkills,
    bool Success);
