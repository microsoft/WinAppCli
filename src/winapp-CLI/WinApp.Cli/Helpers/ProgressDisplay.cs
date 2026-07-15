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
        // Reuse the existing telemetry-side detection so CI/agent classification stays
        // in sync with how we already report sender origin.
        var (senderOrigin, _) = AgentEnvironmentDetector.Detect();

        // Spectre's own probe is the most reliable terminal capability check: it covers
        // NO_COLOR, TERM=dumb, and stream redirection consistently.
        var profile = ansiConsole.Profile;

        return ShouldUseLiveSpinner(
            infoEnabled: logger.IsEnabled(LogLevel.Information),
            senderOrigin: senderOrigin,
            interactiveCapability: profile.Capabilities.Interactive,
            ansiCapability: profile.Capabilities.Ansi,
            outputRedirected: Console.IsOutputRedirected,
            userInteractive: Environment.UserInteractive);
    }

    /// <summary>
    /// Pure decision core for <see cref="ShouldUseLiveSpinner(IAnsiConsole, ILogger)"/>, expressed
    /// over already-probed inputs so every branch can be unit tested without a real console or
    /// environment. Returns <c>false</c> when info output is suppressed, when running under a CI
    /// or AI-agent origin, when the terminal lacks interactive/ANSI capability, or when output is
    /// redirected; otherwise defers to whether the process is attached to an interactive user.
    /// </summary>
    internal static bool ShouldUseLiveSpinner(
        bool infoEnabled,
        string senderOrigin,
        bool interactiveCapability,
        bool ansiCapability,
        bool outputRedirected,
        bool userInteractive)
    {
        // Nothing to show when info-level output is suppressed (--quiet, --json).
        if (!infoEnabled)
        {
            return false;
        }

        if (senderOrigin == AgentEnvironmentDetector.SenderOrigins.Agent ||
            senderOrigin == AgentEnvironmentDetector.SenderOrigins.CI)
        {
            return false;
        }

        if (!interactiveCapability || !ansiCapability)
        {
            return false;
        }

        if (outputRedirected)
        {
            return false;
        }

        return userInteractive;
    }
}

