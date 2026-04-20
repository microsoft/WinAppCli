// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Telemetry;

/// <summary>
/// Detects whether the CLI is being invoked by an AI coding agent, a CI system, or directly by a human.
/// Mirrors the pattern of <see cref="CIEnvironmentDetectorForTelemetry"/> for agent detection.
/// </summary>
internal sealed class AgentEnvironmentDetector
{
    /// <summary>
    /// Sender origin values for telemetry segmentation.
    /// </summary>
    internal static class SenderOrigins
    {
        public const string Direct = "direct";
        public const string Agent = "agent";
        public const string CI = "ci";
    }

    /// <summary>
    /// Generic environment variables that follow emerging community standards.
    /// Checked first — if set, the value is used as the agent name.
    /// </summary>
    /// <remarks>
    /// AI_AGENT: Vercel/detect-agent convention (e.g., AI_AGENT=claude-code)
    /// AGENT: AGENTS.md community convention modeled on CI=true (e.g., AGENT=goose)
    /// See: https://github.com/agentsmd/agents.md/issues/136
    /// </remarks>
    private static readonly string[] GenericAgentVariables =
    [
        "AI_AGENT",
        "AGENT",
    ];

    /// <summary>
    /// Tool-specific environment variables. Each entry maps an env var to a normalized agent name.
    /// Checked after generic variables as a fallback.
    /// </summary>
    private static readonly (string EnvVar, string AgentName)[] ToolSpecificAgentVariables =
    [
        // Claude Code - https://github.com/anthropics/claude-code
        ("CLAUDECODE", "claude-code"),
        ("CLAUDE_CODE_ENTRYPOINT", "claude-code"),

        // Cursor - https://forum.cursor.com
        ("CURSOR_AGENT", "cursor"),
        ("CURSOR_CLI", "cursor"),

        // OpenAI Codex CLI - https://github.com/openai/codex
        ("CODEX_CI", "codex"),

        // Goose (Block) - https://github.com/block/goose
        ("GOOSE_TERMINAL", "goose"),

        // GitHub Copilot CLI
        ("COPILOT_CLI", "copilot-cli"),

        // GitHub Copilot (VS Code)
        ("VSCODE_COPILOT_TERMINAL", "copilot-vscode"),

        // Generic Copilot fallback
        ("COPILOT_MODEL", "copilot"),

        // Cline - https://github.com/cline/cline
        ("CLINE_ACTIVE", "cline"),
    ];

    private static readonly Lock CacheLock = new();
    private static (string SenderOrigin, string? AgentName)? cachedResult;

    /// <summary>
    /// Detects the sender origin and agent name from environment variables.
    /// Results are cached for the lifetime of the process.
    /// </summary>
    /// <returns>
    /// A tuple of (SenderOrigin, AgentName) where SenderOrigin is one of "direct", "agent", or "ci",
    /// and AgentName is the normalized agent name when detected, or null otherwise.
    /// </returns>
    public static (string SenderOrigin, string? AgentName) Detect()
    {
        if (cachedResult.HasValue)
        {
            return cachedResult.Value;
        }

        lock (CacheLock)
        {
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            var result = DetectInternal();
            cachedResult = result;
            return result;
        }
    }

    /// <summary>
    /// Clears the cached detection result. Intended for unit testing only.
    /// </summary>
    internal static void ResetCache()
    {
        lock (CacheLock)
        {
            cachedResult = null;
        }
    }

    private static string? NormalizeAgentName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static (string SenderOrigin, string? AgentName) DetectInternal()
    {
        // 1. Check generic agent environment variables (emerging standards)
        foreach (string variable in GenericAgentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            var normalizedAgentName = NormalizeAgentName(value);
            if (normalizedAgentName is not null)
            {
                return (SenderOrigins.Agent, normalizedAgentName);
            }
        }

        // 2. Check tool-specific agent environment variables
        foreach (var (envVar, agentName) in ToolSpecificAgentVariables)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                return (SenderOrigins.Agent, agentName);
            }
        }

        // 3. Fall back to CI detection
        if (CIEnvironmentDetectorForTelemetry.IsCIEnvironment())
        {
            return (SenderOrigins.CI, null);
        }

        // 4. Default: direct human invocation
        return (SenderOrigins.Direct, null);
    }
}
