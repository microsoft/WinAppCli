// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal sealed class RuntimePrepareCommand : Command, IShortDescription
{
    public string ShortDescription => "Resolve, preflight, install, or stage an exact Windows App SDK runtime";

    public static Option<string> VersionOption { get; } = new("--version")
    {
        Description = "Exact Microsoft.WindowsAppSDK NuGet version (for example, 1.8.250907003 or 2.2.0)",
        Required = true,
    };

    public static Option<RuntimeArchitecture> ArchOption { get; } = new("--arch")
    {
        Description = "Target application architecture: x64, arm64, or x86",
        Required = true,
    };

    public static Option<DirectoryInfo> OutputOption { get; } = new("--output")
    {
        Description = "App output directory where the architecture-specific bootstrap DLL will be staged",
        Required = true,
    };

    public static Option<bool> InstallOption { get; } = new("--install")
    {
        Description = "Install the matching framework-dependent runtime packages for the current user when preflight finds them missing",
    };

    public RuntimePrepareCommand()
        : base(
            "prepare",
            "Prepare an exact framework-dependent Windows App SDK runtime for an unpackaged app. Stages the bootstrap DLL and preflights the matching runtime; add --install to install it for the current user. Use --json for deterministic automation output.")
    {
        Options.Add(VersionOption);
        Options.Add(ArchOption);
        Options.Add(OutputOption);
        Options.Add(InstallOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public sealed class Handler(
        IWindowsAppRuntimeDeploymentService deploymentService,
        IStatusService statusService,
        IAnsiConsole console) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            var version = parseResult.GetRequiredValue(VersionOption);
            var architecture = parseResult.GetRequiredValue(ArchOption).ToString().ToLowerInvariant();
            var output = parseResult.GetRequiredValue(OutputOption);
            var install = parseResult.GetValue(InstallOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            WindowsAppRuntimePrepareResult? result = null;
            string? error = null;
            var exitCode = await statusService.ExecuteWithStatusAsync(
                "Preparing Windows App SDK runtime...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        result = await deploymentService.PrepareAsync(
                            version,
                            architecture,
                            output,
                            install,
                            taskContext,
                            ct);

                        taskContext.AddStatusMessage($"{UiSymbols.Check} Bootstrap DLL: {result.BootstrapDllPath}");
                        if (result.Ready)
                        {
                            taskContext.AddStatusMessage($"{UiSymbols.Check} Matching framework-dependent runtime is registered.");
                            return (0, "Windows App SDK framework-dependent runtime is ready.");
                        }

                        taskContext.AddStatusMessage($"{UiSymbols.Warning} {result.Guidance}");
                        return (2, "The matching Windows App Runtime is not installed.");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        taskContext.AddDebugMessage(ex.ToString());
                        return (1, error);
                    }
                },
                cancellationToken);

            if (json)
            {
                if (result is not null)
                {
                    console.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(
                            result,
                            WinAppJsonContext.Default.WindowsAppRuntimePrepareResult));
                }
                else if (error is not null)
                {
                    JsonErrorOutput.Write(console, error);
                }
            }
            else if (result is not null)
            {
                console.MarkupLine($"Deployment mode: [bold]{Markup.Escape(result.DeploymentMode)}[/]");
                console.MarkupLine($"Version:         [bold]{Markup.Escape(result.Version)}[/]");
                console.MarkupLine($"Runtime version: [bold]{Markup.Escape(result.RuntimeVersion)}[/]");
                console.MarkupLine($"Architecture:    [bold]{Markup.Escape(result.Architecture)}[/]");
                console.MarkupLine($"Output:          {Markup.Escape(result.OutputPath)}");
                console.MarkupLine($"Bootstrap DLL:   {Markup.Escape(result.BootstrapDllPath)}");
                if (!string.IsNullOrWhiteSpace(result.Guidance))
                {
                    console.MarkupLine($"[yellow]{Markup.Escape(result.Guidance)}[/]");
                }
            }

            return exitCode;
        }
    }
}
