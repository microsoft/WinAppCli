// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text;
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

    public class Handler(ICurrentDirectoryProvider currentDirectoryProvider, ILogger<MigrateAnalyzeCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var directory = parseResult.GetValue(DirectoryArgument) ?? currentDirectoryProvider.GetCurrentDirectoryInfo();
            var project = parseResult.GetValue(ProjectOption);

            var driver = ResolveDriver();
            if (driver is null)
            {
                logger.LogError("Analyzer driver 'winui-analyze' not found. Set WINAPP_MIGRATE_ANALYZER to its full path, or place it in the CLI 'tools' folder.");
                return 1;
            }

            var psi = new ProcessStartInfo
            {
                FileName = driver,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(directory.FullName);
            psi.ArgumentList.Add("--from-uwp");
            if (project is not null)
            {
                psi.ArgumentList.Add("--project");
                psi.ArgumentList.Add(project.FullName);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to launch analyzer driver '{Driver}': {Message}", driver, ex.Message);
                return 1;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            // Pass the driver's JSON straight through on stdout.
            Console.Out.Write(stdout.ToString());
            if (process.ExitCode != 0 && stderr.Length > 0)
            {
                logger.LogError("{Error}", stderr.ToString().TrimEnd());
            }
            return process.ExitCode;
        }

        private static string? ResolveDriver()
        {
            var env = Environment.GetEnvironmentVariable("WINAPP_MIGRATE_ANALYZER");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }

            var exeName = OperatingSystem.IsWindows() ? "winui-analyze.exe" : "winui-analyze";
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "tools", exeName),
                Path.Combine(AppContext.BaseDirectory, exeName),
            ];
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }
    }
}
