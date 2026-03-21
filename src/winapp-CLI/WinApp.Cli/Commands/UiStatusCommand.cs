// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiStatusCommand : Command, IShortDescription
{
    public string ShortDescription => "Connect to a running app and show connection info";

    public UiStatusCommand()
        : base("status", "Connect to a target app, auto-detect mode (UIA or DevTools), and display connection info.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiStatusCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var mode = parseResult.GetValue(SharedUiOptions.ModeOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                logger.LogError("{Symbol} Specify --app (name/title/PID) or --window (HWND).", UiSymbols.Error);
                return 1;
            }
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            return await statusService.ExecuteWithStatusAsync(
                "Connecting to app...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var session = await sessionService.ResolveSessionAsync(app, window, mode, ct);

                        if (json)
                        {
                            var result = new UiStatusResult
                            {
                                ProcessId = session.ProcessId,
                                ProcessName = session.ProcessName,
                                WindowTitle = session.WindowTitle,
                                Mode = session.Mode,
                            };
                            ansiConsole.Profile.Out.Writer.WriteLine(
                                JsonSerializer.Serialize(result, UiJsonContext.Default.UiStatusResult));
                        }
                        else
                        {
                            var table = new Spectre.Console.Table()
                                .AddColumn("Property")
                                .AddColumn("Value")
                                .AddRow("Process", session.ProcessName)
                                .AddRow("PID", session.ProcessId.ToString())
                                .AddRow("Window", session.WindowTitle ?? "(none)")
                                .AddRow("Mode", session.Mode);
                            ansiConsole.Write(table);
                        }

                        return (0, $"Connected to {session.ProcessName} (PID {session.ProcessId}) in {session.Mode} mode");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} {ex.Message}");
                    }
                },
                cancellationToken);
        }
    }
}
