// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Provides banner display functionality for the CLI.
/// ASCII art is pre-computed for optimal startup performance.
/// </summary>
internal static class BannerHelper
{
    // Stylized "winapp cli" text in block letters
    private static readonly string[] TitleBlockArt =
    {                                                              
        @"▄▄              ▀▀                                  ██ ▀▀  ",
        @" ▀█▄    ██   ██ ██  ████▄  ▀▀█▄ ████▄ ████▄   ▄████ ██ ██  ",
        @"  ▄█▀   ██ █ ██ ██  ██ ██ ▄█▀██ ██ ██ ██ ██   ██    ██ ██  ",
        @"▄█▀      ██▀██  ██▄ ██ ██ ▀█▄██ ████▀ ████▀   ▀████ ██ ██▄ ",
        @"                                ██    ██                   ",
    };

    // Simple ASCII fallback for the title
    private static readonly string[] TitleAsciiArt =
    {
        @"           _                                 _ _ ",
        @" __      _(_)_ __   __ _ _ __  _ __      ___| (_)",
        @" \ \ /\ / / | '_ \ / _` | '_ \| '_ \    / __| | |",
        @"  \ V  V /| | | | | (_| | |_) | |_) |  | (__| | |",
        @"   \_/\_/ |_|_| |_|\__,_| .__/| .__/    \___|_|_|",
        @"                        |_|   |_|                ",
    };

    // ANSI color codes for gradient effect (Blue -> Purple, Windows-themed)
    private static readonly string[] GradientColors =
    {
        "\x1b[38;5;33m",   // Blue
        "\x1b[38;5;63m",   // Blue-Purple
        "\x1b[38;5;99m",   // Purple
        "\x1b[38;5;135m",  // Light Purple
        "\x1b[38;5;141m",  // Lavender
    };

    private const string ResetColor = "\x1b[0m";

    private static bool? _useEmoji;
    public static bool UseEmoji => _useEmoji ??= Compute();

    private static bool Compute()
    {
        try
        {
            return ComputeUseEmoji(
                Console.OutputEncoding?.CodePage,
                Console.IsOutputRedirected,
                Environment.GetEnvironmentVariable("VSCODE_PID"),
                Environment.GetEnvironmentVariable("TERM_PROGRAM"),
                Environment.GetEnvironmentVariable("WT_SESSION"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pure decision core for <see cref="UseEmoji"/>: emoji/color output is used only in a UTF-8,
    /// non-redirected terminal that is either VS Code or Windows Terminal. Extracted so every branch
    /// can be unit tested without mutating the real console or process environment.
    /// </summary>
    internal static bool ComputeUseEmoji(int? outputCodePage, bool outputRedirected, string? vscodePid, string? termProgram, string? wtSession)
    {
        bool isUtf8 = outputCodePage == 65001;
        bool isVsCode = !string.IsNullOrEmpty(vscodePid) ||
                        string.Equals(termProgram, "vscode", StringComparison.OrdinalIgnoreCase);
        bool isWindowsTerminal = !string.IsNullOrEmpty(wtSession);
        bool notRedirected = !outputRedirected;
        return isUtf8 && notRedirected && (isVsCode || isWindowsTerminal);
    }

    /// <summary>
    /// Displays the CLI banner with version information.
    /// </summary>
    public static void DisplayBanner() => DisplayBanner(Console.Out, UseEmoji);

    /// <summary>
    /// Writes the CLI banner (including the version line) to <paramref name="writer"/>, using the
    /// ANSI color-gradient form when <paramref name="useColor"/> is true and the plain ASCII form
    /// otherwise. Exposed with an explicit writer so both rendering paths are unit testable.
    /// </summary>
    internal static void DisplayBanner(TextWriter writer, bool useColor)
    {
        var version = VersionHelper.GetVersionString();

        if (useColor)
        {
            DisplayColorBanner(writer, version);
        }
        else
        {
            DisplayPlainBanner(writer, version);
        }
    }

    private static void DisplayColorBanner(TextWriter writer, string version)
    {
        var titleLines = TitleBlockArt;
        writer.WriteLine();

        // Display each line with a gradient color
        for (int i = 0; i < titleLines.Length; i++)
        {
            var color = GradientColors[i % GradientColors.Length];
            writer.WriteLine($" {color}{titleLines[i]}{ResetColor}");
        }

        writer.WriteLine();
        writer.WriteLine($" \x1b[90mWindows App Development CLI · Version {version}{ResetColor}");
    }

    private static void DisplayPlainBanner(TextWriter writer, string version)
    {
        foreach (var line in TitleAsciiArt)
        {
            writer.WriteLine($" {line}");
        }

        writer.WriteLine($" Windows App Development CLI - Version {version}");
    }

}
