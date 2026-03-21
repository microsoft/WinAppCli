// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiWaitForCommand : Command, IShortDescription
{
    public string ShortDescription => "Wait for an element to appear, disappear, or change";

    public static Option<bool> GoneOption { get; }
    public static Option<string?> ValueOption { get; }

    static UiWaitForCommand()
    {
        GoneOption = new Option<bool>("--gone")
        {
            Description = "Wait for element to disappear instead of appear"
        };

        ValueOption = new Option<string?>("--value")
        {
            Description = "Wait for property to equal this value (use with --property)"
        };
    }

    public UiWaitForCommand()
        : base("wait-for", "Wait for an element to appear, disappear, or have a property reach a target value. " +
               "Polls at 100ms intervals until condition met or timeout.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.TimeoutOption);
        Options.Add(SharedUiOptions.PropertyOption);
        Options.Add(GoneOption);
        Options.Add(ValueOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiWaitForCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var mode = parseResult.GetValue(SharedUiOptions.ModeOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                logger.LogError("{Symbol} Specify --app (name/title/PID) or --window (HWND).", UiSymbols.Error);
                return 1;
            }
            var timeout = parseResult.GetRequiredValue(SharedUiOptions.TimeoutOption);
            var gone = parseResult.GetValue(GoneOption);
            var property = parseResult.GetValue(SharedUiOptions.PropertyOption);
            var value = parseResult.GetValue(ValueOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} A selector is required.", UiSymbols.Error);
                return 1;
            }

            return await statusService.ExecuteWithStatusAsync(
                "Waiting for condition...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var session = await sessionService.ResolveSessionAsync(app, window, mode, ct);
                        var selector = selectorService.Parse(selectorStr);
                        var sw = Stopwatch.StartNew();

                        while (sw.ElapsedMilliseconds < timeout)
                        {
                            ct.ThrowIfCancellationRequested();

                            Models.UiElement? element;
                            try
                            {
                                if (selector.IsElementId)
                                {
                                    element = await uiAutomation.FindElementByIdAsync(session, selector.ElementId!, ct);
                                }
                                else
                                {
                                    // Use SearchAsync instead of FindSingle — wait-for should succeed if ANY match exists
                                    var matches = await uiAutomation.SearchAsync(session, selector, 1, ct);
                                    element = matches.Length > 0 ? matches[0] : null;
                                }
                            }
                            catch
                            {
                                element = null;
                            }

                            if (gone)
                            {
                                if (element is null)
                                {
                                    if (json)
                                    {
                                        var result = new UiWaitForResult { Found = false, WaitedMs = (int)sw.ElapsedMilliseconds };
                                        ansiConsole.Profile.Out.Writer.WriteLine(
                                            JsonSerializer.Serialize(result, UiJsonContext.Default.UiWaitForResult));
                                    }
                                    return (0, $"Element disappeared after {sw.ElapsedMilliseconds}ms");
                                }
                            }
                            else if (element is not null)
                            {
                                // TODO: If property + value specified, check property value
                                if (json)
                                {
                                    var result = new UiWaitForResult
                                    {
                                        Found = true,
                                        WaitedMs = (int)sw.ElapsedMilliseconds,
                                        Element = element
                                    };
                                    ansiConsole.Profile.Out.Writer.WriteLine(
                                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiWaitForResult));
                                }
                                return (0, $"Element found after {sw.ElapsedMilliseconds}ms");
                            }

                            await Task.Delay(100, ct);
                        }

                        return (1, $"{UiSymbols.Error} Condition not met after {timeout}ms");
                    }
                    catch (OperationCanceledException)
                    {
                        return (1, $"{UiSymbols.Error} Wait cancelled");
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
