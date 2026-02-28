// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for generating agent context skill files in user projects.
/// Reads SKILL.md files embedded in the CLI binary and writes them to the
/// appropriate skills directory following a silent fallback chain.
/// </summary>
internal class AgentContextService(ILogger<AgentContextService> logger) : IAgentContextService
{
    /// <summary>
    /// Prefix for embedded skill resource names in the assembly.
    /// </summary>
    private const string EmbeddedSkillPrefix = "WinApp.Cli.Templates.AgentContext.skills.winapp_cli.";

    /// <summary>
    /// The subdirectory name under the skills directory where winapp skills are placed.
    /// </summary>
    private const string WinAppSkillsDirName = "winapp-cli";

    /// <summary>
    /// Ordered list of skills directory candidates for fallback resolution.
    /// The first existing directory wins; if none exist, the first entry is created.
    /// </summary>
    private static readonly string[] SkillsDirCandidates =
    [
        Path.Combine(".github", "skills"),
        Path.Combine(".agents", "skills"),
        Path.Combine(".claude", "skills"),
    ];

    /// <inheritdoc />
    public bool SkillsExistInProject(DirectoryInfo projectDirectory)
    {
        foreach (var candidate in SkillsDirCandidates)
        {
            var winappSkillsPath = Path.Combine(projectDirectory.FullName, candidate, WinAppSkillsDirName);
            if (Directory.Exists(winappSkillsPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<AgentContextResult> GenerateSkillsAsync(
        DirectoryInfo projectDirectory,
        DirectoryInfo? skillsDir,
        CancellationToken cancellationToken)
    {
        // Resolve skills directory
        var resolvedSkillsDir = ResolveSkillsDirectory(projectDirectory, skillsDir);
        var winappSkillsDir = Path.Combine(resolvedSkillsDir, WinAppSkillsDirName);

        logger.LogDebug("Skills directory: {SkillsDir}", resolvedSkillsDir);

        // Find all embedded skill resources
        var assembly = Assembly.GetExecutingAssembly();
        var skillResources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(EmbeddedSkillPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (skillResources.Count == 0)
        {
            logger.LogWarning("No embedded skill files found in CLI binary.");
            return new AgentContextResult(resolvedSkillsDir, [], false);
        }

        var generatedSkills = new List<string>();

        foreach (var resourceName in skillResources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Parse skill name from resource name
            // Resource name format: WinApp.Cli.Templates.AgentContext.skills.winapp_cli.<skillname>.SKILL.md
            var relativePath = resourceName[EmbeddedSkillPrefix.Length..];
            var skillName = ExtractSkillName(relativePath);

            if (string.IsNullOrEmpty(skillName))
            {
                logger.LogDebug("Skipping unrecognized resource: {Resource}", resourceName);
                continue;
            }

            // Create skill directory and write file
            var skillDir = Path.Combine(winappSkillsDir, skillName);
            Directory.CreateDirectory(skillDir);

            var outputPath = Path.Combine(skillDir, "SKILL.md");

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                logger.LogWarning("Could not read embedded resource: {Resource}", resourceName);
                continue;
            }

            await using var fileStream = File.Create(outputPath);
            await stream.CopyToAsync(fileStream, cancellationToken);

            generatedSkills.Add(skillName);
            logger.LogDebug("  {SkillName} - created", skillName);
        }

        var relativeSkillsDir = Path.GetRelativePath(projectDirectory.FullName, Path.Combine(resolvedSkillsDir, WinAppSkillsDirName));

        return new AgentContextResult(
            relativeSkillsDir.Replace('\\', '/') + "/",
            generatedSkills,
            generatedSkills.Count > 0);
    }

    /// <summary>
    /// Resolve skills directory using the silent fallback chain:
    /// 1. Explicit override (--skills-dir)
    /// 2. .github/skills/ (if exists)
    /// 3. .agents/skills/ (if exists)
    /// 4. .claude/skills/ (if exists)
    /// 5. None exist → create .github/skills/
    /// </summary>
    private static string ResolveSkillsDirectory(DirectoryInfo projectDirectory, DirectoryInfo? explicitSkillsDir)
    {
        if (explicitSkillsDir is not null)
        {
            return explicitSkillsDir.FullName;
        }

        foreach (var candidate in SkillsDirCandidates)
        {
            var candidatePath = Path.Combine(projectDirectory.FullName, candidate);
            if (Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        // Default: create .github/skills/
        var defaultPath = Path.Combine(projectDirectory.FullName, SkillsDirCandidates[0]);
        Directory.CreateDirectory(defaultPath);
        return defaultPath;
    }

    /// <summary>
    /// Extract the skill name from the embedded resource relative path.
    /// E.g., "setup.SKILL.md" → "setup", "package.SKILL.md" → "package"
    /// </summary>
    private static string? ExtractSkillName(string relativePath)
    {
        // Expected format: <skillname>.SKILL.md
        const string suffix = ".SKILL.md";
        if (relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return relativePath[..^suffix.Length];
        }

        return null;
    }
}
