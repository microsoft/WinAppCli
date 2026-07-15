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
        ConfigureConsoleEncoding(static () =>
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = Encoding.UTF8;
        });

        var loggingMode = ResolveLoggingMode(args);
        if (loggingMode.ConflictError is not null)
        {
            Console.Error.WriteLine(loggingMode.ConflictError);
            return 1;
        }

        var minimumLogLevel = loggingMode.MinimumLevel;
        bool quiet = loggingMode.Quiet;
        bool json = loggingMode.Json;

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
                Console.Error.WriteLine($"Unknown option '{typo}'. Did you mean '{suggested}'?");
                Console.Error.WriteLine(
                    "(Single-dash flags are reserved for short aliases like '-a'. Long options use a double dash.)");
                return 1;
            }
        }

        return await RunWithTelemetryAsync(parsedArgs, isCompleteMode, () => parsedArgs.InvokeAsync());
    }

    /// <summary>
    /// Applies the given console-encoding mutation, swallowing the platform exception that occurs
    /// when the standard stream encoding cannot be changed (e.g. redirected or unsupported handles).
    /// UTF-8 I/O is a best-effort nicety, so a failure here must never abort startup.
    /// </summary>
    internal static void ConfigureConsoleEncoding(Action apply)
    {
        try
        {
            apply();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Runs the parsed command via <paramref name="invoke"/>, emitting command-invoked/-completed
    /// telemetry (unless in completion mode) and converting any unhandled exception into a logged
    /// error plus exit code 1. The <paramref name="invoke"/> seam lets tests exercise both the
    /// success and top-level-failure paths without needing a real, throwing command.
    /// </summary>
    internal static Task<int> RunWithTelemetryAsync(
        System.CommandLine.ParseResult parsedArgs, bool isCompleteMode, Func<Task<int>> invoke) =>
        RunWithTelemetryAsync(parsedArgs, isCompleteMode, invoke, CommandInvokedEvent.Log, CommandCompletedEvent.Log);

    /// <summary>
    /// Core of <see cref="RunWithTelemetryAsync(System.CommandLine.ParseResult, bool, Func{Task{int}})"/>
    /// with the telemetry sinks injected. <paramref name="logCommandInvoked"/> and
    /// <paramref name="logCommandCompleted"/> default (via the public overload) to the production
    /// <see cref="CommandInvokedEvent.Log"/>/<see cref="CommandCompletedEvent.Log"/> events; the seam
    /// lets tests assert that the invoked/completed events fire around the invocation — in order, with
    /// the parsed command result and real exit code, and only when not in completion mode.
    /// </summary>
    internal static async Task<int> RunWithTelemetryAsync(
        System.CommandLine.ParseResult parsedArgs, bool isCompleteMode, Func<Task<int>> invoke,
        Action<System.CommandLine.Parsing.CommandResult> logCommandInvoked,
        Action<System.CommandLine.Parsing.CommandResult, int> logCommandCompleted)
    {
        try
        {
            if (!isCompleteMode)
            {
                logCommandInvoked(parsedArgs.CommandResult);
            }

            var returnCode = await invoke();

            if (!isCompleteMode)
            {
                logCommandCompleted(parsedArgs.CommandResult, returnCode);
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
    /// The global logging mode resolved from argv: the minimum log level plus the individual
    /// <c>--quiet</c>/<c>--verbose</c>/<c>--json</c> flags, and a non-null <see cref="ConflictError"/>
    /// message when a mutually-exclusive combination was requested.
    /// </summary>
    internal readonly record struct LoggingMode(LogLevel MinimumLevel, bool Quiet, bool Verbose, bool Json, string? ConflictError);

    /// <summary>
    /// Pre-scans argv for the global logging-mode flags and resolves the effective
    /// <see cref="LoggingMode"/>. The scan (via <see cref="GlobalOptionPreScan"/>) stops at the first
    /// standalone <c>--</c> separator so passthrough payload (e.g. <c>winapp run . -- --json</c>) is
    /// not misread as a winapp global flag. Extracted from <see cref="Main"/> so the flag precedence
    /// and mutual-exclusion rules are unit testable without invoking the full entrypoint.
    /// </summary>
    internal static LoggingMode ResolveLoggingMode(string[] args)
    {
        var minimumLogLevel = LogLevel.Information;
        bool quiet = false;
        bool verbose = false;
        bool json = false;

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
        if (GlobalOptionPreScan.IsFlagPresent(args, WinAppRootCommand.JsonOption.Name, WinAppRootCommand.JsonOption.Aliases))
        {
            minimumLogLevel = LogLevel.None;
            json = true;
        }

        string? conflictError = null;
        if (quiet && verbose)
        {
            conflictError = "Cannot specify both --quiet and --verbose options together.";
        }
        else if (quiet && json)
        {
            conflictError = "Cannot specify both --quiet and --json options together.";
        }
        else if (verbose && json)
        {
            conflictError = "Cannot specify both --verbose and --json options together.";
        }

        return new LoggingMode(minimumLogLevel, quiet, verbose, json, conflictError);
    }
}
