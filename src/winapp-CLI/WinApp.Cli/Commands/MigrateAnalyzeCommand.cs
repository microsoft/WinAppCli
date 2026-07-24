// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class MigrateAnalyzeCommand : Command, IShortDescription
{
    public string ShortDescription => "Analyze UWP source and emit a migration plan (JSON)";

    public static Argument<DirectoryInfo> DirectoryArgument { get; }
    public static Option<bool> FromUwpOption { get; }
    public static Option<FileInfo?> ProjectOption { get; }

    static MigrateAnalyzeCommand()
    {
        DirectoryArgument = new Argument<DirectoryInfo>("directory")
        {
            Description = "UWP project directory to analyze (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne
        };
        DirectoryArgument.AcceptExistingOnly();

        FromUwpOption = new Option<bool>("--from-uwp")
        {
            Description = "Analyze UWP source (currently the only supported migration source)."
        };

        ProjectOption = new Option<FileInfo?>("--project")
        {
            Description = "Target a specific .csproj (default: scan the whole directory)."
        };
    }

    public MigrateAnalyzeCommand()
        : base("analyze", "Analyze UWP source (C#/XAML/manifest) pre-build, source-only (no restore or build), and emit a stable JSON migration plan to stdout: per-file disposition + per-line findings + severity + fix refs + feature area. The analysis runs out-of-process via the bundled analyzer driver.")
    {
        Arguments.Add(DirectoryArgument);
        Options.Add(FromUwpOption);
        Options.Add(ProjectOption);
    }

    public class Handler(ICurrentDirectoryProvider currentDirectoryProvider, IMigrateAnalyzerDriver driver, ILogger<MigrateAnalyzeCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var directory = parseResult.GetValue(DirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var project = parseResult.GetValue(ProjectOption);

            MigrateAnalyzerRun run;
            try
            {
                run = await driver.RunAsync(directory, project, fromUwp: true, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to launch analyzer driver: {Message}", ex.Message);
                return 1;
            }

            if (!run.DriverFound)
            {
                logger.LogError("Analyzer driver 'winui-analyze' not found. Set WINAPP_MIGRATE_ANALYZER to its full path, or place it in the CLI 'tools' folder.");
                return 1;
            }

            // Pass the driver's JSON straight through on stdout.
            Console.Out.Write(run.StdOut);
            if (run.ExitCode != 0 && run.StdErr.Length > 0)
            {
                logger.LogError("{Error}", run.StdErr.TrimEnd());
            }
            return run.ExitCode;
        }
    }
}
