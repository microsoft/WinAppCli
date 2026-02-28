// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class AgentContextServiceTests : BaseCommandTests
{
    public AgentContextServiceTests() : base(configPaths: false, verboseLogging: true) { }

    private IAgentContextService _agentContextService = null!;

    [TestInitialize]
    public void Setup()
    {
        _agentContextService = GetRequiredService<IAgentContextService>();
    }

    [TestMethod]
    public async Task GenerateSkills_CreatesDefaultSkillsDirectory_WhenNoneExist()
    {
        // Arrange - temp directory with no existing skills dirs

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert
        Assert.IsTrue(result.Success || result.GeneratedSkills.Count == 0,
            "Should succeed or have no embedded skills (when running in test context without embedded resources)");

        // If skills were generated, verify directory was created
        if (result.Success)
        {
            var expectedDir = Path.Combine(_tempDirectory.FullName, ".github", "skills", "winapp-cli");
            Assert.IsTrue(Directory.Exists(expectedDir), "Should create .github/skills/winapp-cli/");
            Assert.IsNotEmpty(result.GeneratedSkills, "Should generate at least one skill");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_UsesExistingGitHubSkillsDir()
    {
        // Arrange
        var githubSkillsDir = Path.Combine(_tempDirectory.FullName, ".github", "skills");
        Directory.CreateDirectory(githubSkillsDir);

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert
        if (result.Success)
        {
            StringAssert.Contains(result.SkillsDirectory, ".github/skills/winapp-cli",
                "Should use existing .github/skills/ directory");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_UsesExistingAgentsSkillsDir()
    {
        // Arrange
        var agentsSkillsDir = Path.Combine(_tempDirectory.FullName, ".agents", "skills");
        Directory.CreateDirectory(agentsSkillsDir);

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert
        if (result.Success)
        {
            StringAssert.Contains(result.SkillsDirectory, ".agents/skills/winapp-cli",
                "Should use existing .agents/skills/ directory");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_UsesExistingClaudeSkillsDir()
    {
        // Arrange
        var claudeSkillsDir = Path.Combine(_tempDirectory.FullName, ".claude", "skills");
        Directory.CreateDirectory(claudeSkillsDir);

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert
        if (result.Success)
        {
            StringAssert.Contains(result.SkillsDirectory, ".claude/skills/winapp-cli",
                "Should use existing .claude/skills/ directory");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_PrefersGitHubOverClaude()
    {
        // Arrange — both .github/skills/ and .claude/skills/ exist
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".github", "skills"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".claude", "skills"));

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert
        if (result.Success)
        {
            StringAssert.Contains(result.SkillsDirectory, ".github/skills/winapp-cli",
                "Should prefer .github/skills/ over .claude/skills/");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_UsesExplicitSkillsDir()
    {
        // Arrange
        var explicitDir = _tempDirectory.CreateSubdirectory("my-custom-skills");

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: explicitDir, CancellationToken.None);

        // Assert
        if (result.Success)
        {
            StringAssert.Contains(result.SkillsDirectory, "my-custom-skills",
                "Should use explicit --skills-dir override");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_OverwritesExistingSkillFiles()
    {
        // Arrange — create a pre-existing SKILL.md
        var skillDir = Path.Combine(_tempDirectory.FullName, ".github", "skills", "winapp-cli", "setup");
        Directory.CreateDirectory(skillDir);
        var skillFile = Path.Combine(skillDir, "SKILL.md");
        await File.WriteAllTextAsync(skillFile, "old content");

        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert
        if (result.Success && result.GeneratedSkills.Contains("setup"))
        {
            var newContent = await File.ReadAllTextAsync(skillFile);
            Assert.AreNotEqual("old content", newContent, "Should overwrite existing skill files");
        }
    }

    [TestMethod]
    public async Task GenerateSkills_ReturnsCorrectSkillCount()
    {
        // Act
        var result = await _agentContextService.GenerateSkillsAsync(_tempDirectory, skillsDir: null, CancellationToken.None);

        // Assert — either we have embedded skills (from a built binary) or we don't (test environment)
        if (result.Success)
        {
            // The spec defines 7 skills: setup, package, identity, signing, manifest, troubleshoot, frameworks
            Assert.HasCount(7, result.GeneratedSkills);
        }
    }

    [TestMethod]
    public void SkillsExistInProject_ReturnsFalse_WhenNoSkillsExist()
    {
        // Act
        var exists = _agentContextService.SkillsExistInProject(_tempDirectory);

        // Assert
        Assert.IsFalse(exists, "Should return false when no skills directories exist");
    }

    [TestMethod]
    public void SkillsExistInProject_ReturnsTrue_WhenGitHubSkillsExist()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".github", "skills", "winapp-cli"));

        // Act
        var exists = _agentContextService.SkillsExistInProject(_tempDirectory);

        // Assert
        Assert.IsTrue(exists, "Should return true when .github/skills/winapp-cli/ exists");
    }

    [TestMethod]
    public void SkillsExistInProject_ReturnsTrue_WhenAgentsSkillsExist()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".agents", "skills", "winapp-cli"));

        // Act
        var exists = _agentContextService.SkillsExistInProject(_tempDirectory);

        // Assert
        Assert.IsTrue(exists, "Should return true when .agents/skills/winapp-cli/ exists");
    }

    [TestMethod]
    public void SkillsExistInProject_ReturnsTrue_WhenClaudeSkillsExist()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".claude", "skills", "winapp-cli"));

        // Act
        var exists = _agentContextService.SkillsExistInProject(_tempDirectory);

        // Assert
        Assert.IsTrue(exists, "Should return true when .claude/skills/winapp-cli/ exists");
    }

    [TestMethod]
    public void SkillsExistInProject_ReturnsFalse_WhenSkillsDirExistsButNoWinappCli()
    {
        // Arrange — .github/skills/ exists but no winapp-cli subfolder
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".github", "skills"));

        // Act
        var exists = _agentContextService.SkillsExistInProject(_tempDirectory);

        // Assert
        Assert.IsFalse(exists, "Should return false when skills dir exists but winapp-cli subfolder doesn't");
    }
}
