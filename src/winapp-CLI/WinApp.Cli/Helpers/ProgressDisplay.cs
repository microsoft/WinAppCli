// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Telemetry;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Centralised detection for whether the CLI should drive an animated, redraw-based
/// progress display (Spectre <c>Live</c> spinner) or fall back to plain output.
///
/// Animated spinners are great in real interactive terminals, but in CI logs and inside
/// AI-agent terminal captures the cursor-up/erase ANSI sequences are typically stripped,
/// which causes every spinner frame to be retained as a new line of output and quickly
/// floods the surrounding context.
/// </summary>
internal static class ProgressDisplay
{
    /// <summary>
    /// Returns <c>true</c> when the live, animated spinner display should be used.
    /// Returns <c>false</c> when we should fall back to plain output (CI, AI agents,
    /// redirected output, <c>--quiet</c>, <c>--json</c>, <c>NO_COLOR</c>, <c>TERM=dumb</c>, etc.).
    /// </summary>
    public static bool ShouldUseLiveSpinner(IAnsiConsole ansiConsole, ILogger logger)
    {
        // Nothing to show when info-level output is suppressed (--quiet, --json).
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return false;
        }

        // Reuse the existing telemetry-side detection so CI/agent classification stays
        // in sync with how we already report sender origin.
        var (senderOrigin, _) = AgentEnvironmentDetector.Detect();
        if (senderOrigin == AgentEnvironmentDetector.SenderOrigins.Agent ||
            senderOrigin == AgentEnvironmentDetector.SenderOrigins.CI)
        {
            return false;
        }

        // Spectre's own probe is the most reliable terminal capability check: it covers
        // NO_COLOR, TERM=dumb, and stream redirection consistently.
        var profile = ansiConsole.Profile;
        if (!profile.Capabilities.Interactive || !profile.Capabilities.Ansi)
        {
            return false;
        }

        if (Console.IsOutputRedirected)
        {
            return false;
        }

        return Environment.UserInteractive;
    }
}

