// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Telemetry;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests must not run in parallel because they modify process-wide environment variables
/// and the static AgentEnvironmentDetector cache.
/// </summary>
[TestClass]
[DoNotParallelize]
public class AgentEnvironmentDetectorTests
{
    // All env vars that the detector checks — must be cleaned between tests
    private static readonly string[] AllAgentEnvVars =
    [
        "AI_AGENT",
        "AGENT",
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "CURSOR_AGENT",
        "CURSOR_CLI",
        "CODEX_CI",
        "GOOSE_TERMINAL",
        "COPILOT_CLI",
        "VSCODE_COPILOT_TERMINAL",
        "COPILOT_MODEL",
        "CLINE_ACTIVE",
    ];

    // CI env vars that CIEnvironmentDetectorForTelemetry checks (must stay in sync)
    private static readonly string[] CIEnvVars =
    [
        // BooleanVariables
        "TF_BUILD",
        "GITHUB_ACTIONS",
        "APPVEYOR",
        "CI",
        "TRAVIS",
        "CIRCLECI",
        // AllNotNullVariables
        "CODEBUILD_BUILD_ID",
        "AWS_REGION",
        "BUILD_ID",
        "BUILD_URL",
        "PROJECT_ID",
        // IfNonNullVariables
        "TEAMCITY_VERSION",
        "JB_SPACE_API_URL",
    ];

    [TestInitialize]
    public void ClearEnvironment()
    {
        foreach (var v in AllAgentEnvVars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        foreach (var v in CIEnvVars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        AgentEnvironmentDetector.ResetCache();
    }

    [TestCleanup]
    public void RestoreEnvironment()
    {
        // Ensure env vars are cleaned even if a test fails
        ClearEnvironment();
    }

    [TestMethod]
    public void Detect_NoEnvVars_ReturnsDirect()
    {
        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("direct", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_ReturnsAgentWithName()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "claude-code");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_AGENT_ReturnsAgentWithName()
    {
        Environment.SetEnvironmentVariable("AGENT", "goose");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("goose", agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_NormalizesToLowercase()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "Claude-Code");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_AI_AGENT_TrimsWhitespace()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "  claude-code  ");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_GenericVarTakesPriorityOverToolSpecific()
    {
        Environment.SetEnvironmentVariable("AI_AGENT", "my-custom-agent");
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual("my-custom-agent", agentName, "Generic AI_AGENT should take priority over tool-specific CLAUDECODE");
    }

    [TestMethod]
    [DataRow("CLAUDECODE", "claude-code")]
    [DataRow("CLAUDE_CODE_ENTRYPOINT", "claude-code")]
    [DataRow("CURSOR_AGENT", "cursor")]
    [DataRow("CURSOR_CLI", "cursor")]
    [DataRow("CODEX_CI", "codex")]
    [DataRow("GOOSE_TERMINAL", "goose")]
    [DataRow("COPILOT_CLI", "copilot-cli")]
    [DataRow("VSCODE_COPILOT_TERMINAL", "copilot-vscode")]
    [DataRow("COPILOT_MODEL", "copilot")]
    [DataRow("CLINE_ACTIVE", "cline")]
    public void Detect_ToolSpecificEnvVar_ReturnsExpectedAgent(string envVar, string expectedAgentName)
    {
        Environment.SetEnvironmentVariable(envVar, "1");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin);
        Assert.AreEqual(expectedAgentName, agentName);
    }

    [TestMethod]
    public void Detect_CIEnvironment_ReturnsCi()
    {
        Environment.SetEnvironmentVariable("TF_BUILD", "true");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("ci", senderOrigin);
        Assert.IsNull(agentName);
    }

    [TestMethod]
    public void Detect_AgentTakesPriorityOverCI()
    {
        Environment.SetEnvironmentVariable("CLAUDECODE", "1");
        Environment.SetEnvironmentVariable("TF_BUILD", "true");

        var (senderOrigin, agentName) = AgentEnvironmentDetector.Detect();

        Assert.AreEqual("agent", senderOrigin, "Agent detection should take priority over CI");
        Assert.AreEqual("claude-code", agentName);
    }

    [TestMethod]
    public void Detect_CachesResult()
    {
        var first = AgentEnvironmentDetector.Detect();

        // Set an env var after first detection — should still return cached result
        Environment.SetEnvironmentVariable("AI_AGENT", "late-agent");
        var second = AgentEnvironmentDetector.Detect();

        Assert.AreEqual(first.SenderOrigin, second.SenderOrigin);
        Assert.AreEqual(first.AgentName, second.AgentName);
    }

    [TestMethod]
    public void Detect_ResetCacheThenDetectsNewValue()
    {
        var first = AgentEnvironmentDetector.Detect();
        Assert.AreEqual("direct", first.SenderOrigin);

        Environment.SetEnvironmentVariable("AI_AGENT", "new-agent");
        AgentEnvironmentDetector.ResetCache();

        var second = AgentEnvironmentDetector.Detect();
        Assert.AreEqual("agent", second.SenderOrigin);
        Assert.AreEqual("new-agent", second.AgentName);
    }
}
