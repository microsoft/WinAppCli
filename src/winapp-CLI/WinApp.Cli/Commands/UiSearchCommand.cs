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

internal class UiSearchCommand : Command, IShortDescription
{
    public string ShortDescription => "Find elements matching a selector";

    public UiSearchCommand()
        : base("search", "Search the element tree for elements matching a selector. Returns all matches with IDs.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.MaxResultsOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiSearchCommand> logger) : AsynchronousCommandLineAction
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
            var maxResults = parseResult.GetRequiredValue(SharedUiOptions.MaxResultsOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                logger.LogError("{Symbol} A selector is required for search.", UiSymbols.Error);
                return 1;
            }

            return await statusService.ExecuteWithStatusAsync(
                "Searching...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var session = await sessionService.ResolveSessionAsync(app, window, mode, ct);
                        var selector = selectorService.Parse(selectorStr);
                        var matches = await uiAutomation.SearchAsync(session, selector, maxResults + 1, ct);

                        var hasMore = matches.Length > maxResults;
                        if (hasMore)
                        {
                            matches = matches[..maxResults];
                        }

                        // Update element cache
                        // Replace element cache with current results (IDs are per-command)
                        session.Elements = new Dictionary<string, Models.CachedElement>();
                        foreach (var el in matches)
                        {
                            session.Elements[el.Id] = new Models.CachedElement
                            {
                                AutomationId = el.AutomationId,
                                Name = el.Name,
                                Type = el.Type,
                                X = el.X,
                                Y = el.Y
                            };

                            // Also cache invokable ancestors so they can be used with invoke
                            if (el.InvokableAncestor is { } ancestor)
                            {
                                session.Elements[ancestor.Id] = new Models.CachedElement
                                {
                                    AutomationId = ancestor.AutomationId,
                                    Name = ancestor.Name,
                                    Type = ancestor.Type,
                                    X = ancestor.X,
                                    Y = ancestor.Y
                                };
                            }
                        }
                        await sessionService.SaveSessionAsync(session, ct);

                        if (json)
                        {
                            var result = new UiSearchResult
                            {
                                Mode = session.Mode,
                                MatchCount = matches.Length,
                                HasMore = hasMore,
                                Matches = matches
                            };
                            ansiConsole.Profile.Out.Writer.WriteLine(
                                JsonSerializer.Serialize(result, UiJsonContext.Default.UiSearchResult));
                        }
                        else
                        {
                            foreach (var el in matches)
                            {
                                var name = el.Name is not null ? $" \"{el.Name}\"" : "";
                                var autoId = el.AutomationId is not null ? $" ${el.AutomationId}" : "";
                                var bounds = el.Width > 0 ? $" ({el.X},{el.Y} {el.Width}x{el.Height})" : "";
                                ansiConsole.WriteLine($"  {el.Id}  {el.Type}{name}{autoId}{bounds}");

                                if (el.InvokableAncestor is { } ancestor)
                                {
                                    var aName = ancestor.Name is not null ? $" \"{ancestor.Name}\"" : "";
                                    var aAutoId = ancestor.AutomationId is not null ? $" ${ancestor.AutomationId}" : "";
                                    ansiConsole.WriteLine($"        \u2191 invoke via: {ancestor.Id}  {ancestor.Type}{aName}{aAutoId}");
                                }
                            }
                        }

                        var moreText = hasMore ? $" (showing first {maxResults})" : "";
                        return (0, $"Found {matches.Length} matches{moreText}");
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
