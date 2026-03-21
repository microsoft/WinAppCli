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

internal class UiInspectCommand : Command, IShortDescription
{
    public string ShortDescription => "View the element tree of a running app";

    public static Option<bool> AncestorsOption { get; }

    static UiInspectCommand()
    {
        AncestorsOption = new Option<bool>("--ancestors")
        {
            Description = "Walk up the tree from the specified element to the root"
        };
    }

    public UiInspectCommand()
        : base("inspect", "View the UI element tree. Shows ControlType, Name, AutomationId, and bounds for each element.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.DepthOption);
        Options.Add(AncestorsOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiInspectCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var mode = parseResult.GetValue(SharedUiOptions.ModeOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                logger.LogError("{Symbol} Specify --app (name/title/PID) or --window (HWND).", UiSymbols.Error);
                return 1;
            }
            var depth = parseResult.GetRequiredValue(SharedUiOptions.DepthOption);
            var ancestors = parseResult.GetValue(AncestorsOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            return await statusService.ExecuteWithStatusAsync(
                "Inspecting element tree...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var session = await sessionService.ResolveSessionAsync(app, window, mode, ct);
                        Models.UiElement[] elements;

                        if (ancestors && selector is not null)
                        {
                            var parsed = selectorService.Parse(selector);
                            elements = await uiAutomation.InspectAncestorsAsync(session, parsed.ElementId ?? selector, ct);
                        }
                        else
                        {
                            elements = await uiAutomation.InspectAsync(session, selector, depth, ct);
                        }

                        // Update element cache
                        // Replace element cache with current results (IDs are per-command)
                        session.Elements = new Dictionary<string, Models.CachedElement>();
                        foreach (var el in elements)
                        {
                            session.Elements[el.Id] = new Models.CachedElement
                            {
                                AutomationId = el.AutomationId,
                                Name = el.Name,
                                Type = el.Type,
                                X = el.X,
                                Y = el.Y
                            };
                        }
                        await sessionService.SaveSessionAsync(session, ct);

                        if (json)
                        {
                            var result = new UiInspectResult { Mode = session.Mode, Elements = elements };
                            ansiConsole.Profile.Out.Writer.WriteLine(
                                JsonSerializer.Serialize(result, UiJsonContext.Default.UiInspectResult));
                        }
                        else
                        {
                            foreach (var el in elements)
                            {
                                var name = el.Name is not null ? $" \"{el.Name}\"" : "";
                                var autoId = el.AutomationId is not null ? $" ${el.AutomationId}" : "";
                                var bounds = el.Width > 0 ? $" ({el.X},{el.Y} {el.Width}x{el.Height})" : "";
                                var disabled = el.IsEnabled ? "" : " (disabled)";
                                ansiConsole.WriteLine($"  {el.Id}  {el.Type}{name}{autoId}{bounds}{disabled}");
                            }
                        }

                        return (0, $"Found {elements.Length} elements ({session.Mode} mode)");
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
