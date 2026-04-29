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
    static async Task<int> Main(string[] args)
    {
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

        if (args.Contains(WinAppRootCommand.VerboseOption.Name) || args.Any(WinAppRootCommand.VerboseOption.Aliases.Contains))
        {
            minimumLogLevel = LogLevel.Debug;
            verbose = true;
        }
        if (args.Contains(WinAppRootCommand.QuietOption.Name) || args.Any(WinAppRootCommand.QuietOption.Aliases.Contains))
        {
            minimumLogLevel = LogLevel.Warning;
            quiet = true;
        }
        if (args.Contains(WinAppRootCommand.JsonOption.Name) || args.Any(WinAppRootCommand.JsonOption.Aliases.Contains))
        {
            minimumLogLevel = LogLevel.None;
            json = true;
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
        bool isCliSchemaMode = args.Contains(WinAppRootCommand.CliSchemaOption.Name);

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

        try
        {
            if (!isCompleteMode)
            {
                CommandInvokedEvent.Log(parsedArgs.CommandResult);
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
}
