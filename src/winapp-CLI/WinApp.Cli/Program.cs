// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;
using WinApp.Cli.Telemetry;
using WinApp.Cli.Telemetry.Events;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace WinApp.Cli;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        // Hidden internal verb: the WinUI DbgEng triage pass runs in this isolated child process so
        // its modern dbgeng.dll is not poisoned by the system32 dbghelp.dll the parent already loaded.
        // Intercept before any host/service setup to keep the loader state clean and output noise-free.
        if (args.Length > 0 && args[0] == Services.XamlTriageRunner.InternalVerb)
        {
            return Services.XamlTriageRunner.Run(args);
        }

        // Ensure UTF-8 I/O for emoji-capable terminals; fall back silently if not supported
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // ignore
        }

        var minimumLogLevel = LogLevel.Information;
        bool quiet = false;
        bool verbose = false;
        bool json = false;

        // Pre-scan argv for the global logging-mode flags. The scan stops at the first
        // standalone '--' separator so that passthrough payload (e.g. `winapp run . --
        // --json`) is not misread as a winapp global flag.
        if (GlobalOptionPreScan.IsFlagPresent(args, WinAppRootCommand.VerboseOption.Name, WinAppRootCommand.VerboseOption.Aliases))
        {
            minimumLogLevel = LogLevel.Debug;
            verbose = true;
        }
        if (GlobalOptionPreScan.IsFlagPresent(args, WinAppRootCommand.QuietOption.Name, WinAppRootCommand.QuietOption.Aliases))
        {
            minimumLogLevel = LogLevel.Warning;
            quiet = true;
        }
        // Use value-aware scan for --json so that --json=true, --json true, --json false, and
        // --json=false are all handled correctly before a real parse is available (M1).
        json = GlobalOptionPreScan.GetBooleanFlagValue(args, WinAppRootCommand.JsonOption.Name, WinAppRootCommand.JsonOption.Aliases);
        if (json)
        {
            minimumLogLevel = LogLevel.None;
        }

        if (quiet && verbose)
        {
            Console.Error.WriteLine($"Cannot specify both --quiet and --verbose options together.");
            return 1;
        }
        else if (quiet && json)
        {
            Console.Error.WriteLine($"Cannot specify both --quiet and --json options together.");
            return 1;
        }
        else if (verbose && json)
        {
            Console.Error.WriteLine($"Cannot specify both --verbose and --json options together.");
            return 1;
        }

        // Check if --cli-schema is specified - this outputs machine-readable JSON
        // and should not display any interactive messages like first-run notices
        bool isCliSchemaMode = GlobalOptionPreScan.IsFlagPresent(
            args, WinAppRootCommand.CliSchemaOption.Name, []);

        // Check if this is a completion request - completions must be fast and silent
        bool isCompleteMode = args.Length > 0 && args[0] == "complete";

        var services = new ServiceCollection()
            .ConfigureServices()
            .ConfigureCommands()
            .AddLogging(b =>
            {
                b.ClearProviders();
                b.AddTextWriterLogger(Console.Out, Console.Error);
                b.SetMinimumLevel(minimumLogLevel);
            });

        using var serviceProvider = services.BuildServiceProvider();

        var rootCommand = serviceProvider.GetRequiredService<WinAppRootCommand>();
        System.CommandLine.ParseResult? parseResult = null;

        if (args.Length > 0)
        {
            parseResult = rootCommand.Parse(args, WinAppParserConfiguration.Default);

            // Set WINAPP_CLI_CALLER env var from --caller option so telemetry and update checks can use it
            var caller = parseResult.GetValue(WinAppRootCommand.CallerOption);
            if (!string.IsNullOrWhiteSpace(caller))
            {
                Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", caller);
            }
        }

        // Skip first-run notice for machine-readable output modes and completions
        var didShowFirstRunNotice = false;
        if (!isCliSchemaMode && !isCompleteMode && !json)
        {
            var firstRunService = serviceProvider.GetRequiredService<IFirstRunService>();
            didShowFirstRunNotice = firstRunService.CheckAndDisplayFirstRunNotice();

            // Check for CLI updates — shows cached notice instantly (no network),
            // and starts a background refresh if the cache is stale (fire-and-forget).
            if (!quiet)
            {
                var updateNotificationService = serviceProvider.GetRequiredService<IUpdateNotificationService>();
                updateNotificationService.CheckAndNotify();
            }
        }

        // If no arguments provided, display banner and show help
        if (args.Length == 0)
        {
            if (!didShowFirstRunNotice)
            {
                BannerHelper.DisplayBanner();
            }

            // Show help by invoking with --help
            await rootCommand.Parse(["--help"], WinAppParserConfiguration.Default).InvokeAsync();
            return 0;
        }

        var parsedArgs = parseResult!;

        // Derive the effective JSON mode from the SELECTED command's PARSED --json value (M1).
        // The pre-scan (json) is value-aware but still a heuristic; the parsed value is the truth.
        // Reading the parsed bool works even when a different option (e.g. --pressure) failed parse.
        bool effectiveJson = ResolveEffectiveJson(parsedArgs);

        // Catch single-dash typos like "-app" before invocation so the user gets a clear
        // "Did you mean --app?" message instead of System.CommandLine's confusing
        // "Unrecognized command or argument" pointing at the wrong token (issue #467).
        // Only run when parsing already failed — otherwise a command that legitimately
        // accepts a "-foo"-shaped positional value would get a false-positive typo error.
        if (parsedArgs.Errors.Count > 0)
        {
            var typo = OptionTypoValidator.FindLikelyLongOptionTypo(args, parsedArgs);
            if (typo is not null)
            {
                var suggested = "-" + typo;
                if (effectiveJson && IsUiDescendant(parsedArgs))
                {
                    // Gate on IsUiDescendant so that non-ui commands (e.g. cert info) fall through
                    // to default error handling instead of receiving the nested UI schema (M2).
                    UiJsonError.Emit(true, UiJsonError.CodeInvalidArguments,
                        $"Unknown option '{typo}'. Did you mean '{suggested}'?");
                }
                else
                {
                    Console.Error.WriteLine($"Unknown option '{typo}'. Did you mean '{suggested}'?");
                    Console.Error.WriteLine(
                        "(Single-dash flags are reserved for short aliases like '-a'. Long options use a double dash.)");
                }
                return 1;
            }
        }

        try
        {
            if (!isCompleteMode)
            {
                CommandInvokedEvent.Log(parsedArgs.CommandResult);
            }

            // Parse-error → JSON bridge: activated only when the SELECTED command exposes --json,
            // its parsed value is true (effectiveJson), AND the command is a ui descendant (M3).
            // Non-ui commands (e.g. cert info) use a flat {"error":"..."} schema — do not impose
            // the UI nested contract on them; let SCL's default parse-error handling run instead.
            // M3: emit CommandCompletedEvent before the early return to keep telemetry paired.
            if (effectiveJson && parsedArgs.Errors.Count > 0 && IsUiDescendant(parsedArgs))
            {
                var errorMsg = string.Join("; ", parsedArgs.Errors.Select(e => e.Message));
                UiJsonError.Emit(true, UiJsonError.CodeInvalidArguments, errorMsg);
                if (!isCompleteMode)
                {
                    CommandCompletedEvent.Log(parsedArgs.CommandResult, 1);
                }
                return 1;
            }

            var returnCode = await parsedArgs.InvokeAsync();

            if (!isCompleteMode)
            {
                CommandCompletedEvent.Log(parsedArgs.CommandResult, returnCode);
            }

            return returnCode;
        }
        catch (Exception ex)
        {
            TelemetryFactory.Get<ITelemetry>().LogException(parsedArgs.CommandResult.Command.Name, ex);
            Console.Error.WriteLine($"An unexpected error occurred: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Derives the effective JSON mode from the selected command's PARSED <c>--json</c> option.
    /// Reading the parsed value is reliable even when a different option caused a parse error,
    /// because System.CommandLine parses each option independently (M1).
    /// </summary>
    private static bool ResolveEffectiveJson(System.CommandLine.ParseResult parsedArgs)
    {
        // Only engage when the selected (innermost) command actually owns --json.
        var selectedCmd = parsedArgs.CommandResult.Command;
        if (!selectedCmd.Options.Contains(WinAppRootCommand.JsonOption))
        {
            return false;
        }
        try
        {
            return parsedArgs.GetValue(WinAppRootCommand.JsonOption);
        }
        catch
        {
            // If reading the parsed value fails for any reason, do not fire the bridge.
            return false;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the selected command is the <c>ui</c> command
    /// or any of its descendants. Used to scope the parse-error JSON bridge to ui commands
    /// only, leaving non-ui commands (e.g. <c>cert info</c>) with their own flat error schema (M3).
    /// </summary>
    private static bool IsUiDescendant(System.CommandLine.ParseResult parseResult)
    {
        var cmd = parseResult.CommandResult.Command;
        while (cmd is not null)
        {
            if (cmd.Name == "ui")
            {
                return true;
            }
            cmd = cmd.Parents.OfType<System.CommandLine.Command>().FirstOrDefault();
        }
        return false;
    }
}
