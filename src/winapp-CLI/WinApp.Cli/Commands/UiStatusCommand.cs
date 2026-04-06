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
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IAnsiConsole ansiConsole,
        ILogger<UiStatusCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger);
                return 1;
            }
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                if (json)
                {
                    var result = new UiStatusResult
                    {
                        ProcessId = session.ProcessId,
                        ProcessName = session.ProcessName,
                        WindowTitle = session.WindowTitle,
                        Hwnd = session.WindowHandle,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiStatusResult));
                }
                else
                {
                    ansiConsole.WriteLine($"Process: {session.ProcessName}");
                    ansiConsole.WriteLine($"PID: {session.ProcessId}");
                    ansiConsole.WriteLine($"Window: {session.WindowTitle ?? "(none)"}");
                    if (session.WindowHandle != 0)
                    {
                        ansiConsole.WriteLine($"HWND: {session.WindowHandle}");
                    }
                }

                logger.LogInformation("Connected to {ProcessName} (PID {ProcessId})", session.ProcessName, session.ProcessId);
                return 0;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex);
                return 1;
            }
        }
    }
}
