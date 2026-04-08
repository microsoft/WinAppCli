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
    public string ShortDescription => "Find elements by text";

    public UiSearchCommand()
        : base("search", "Search the element tree for elements matching a text query. Returns all matches with semantic slugs.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.MaxResultsOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        ISelectorService selectorService,
        IAnsiConsole ansiConsole,
        ILogger<UiSearchCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger);
                return 1;
            }
            var maxResults = parseResult.GetRequiredValue(SharedUiOptions.MaxResultsOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            if (string.IsNullOrWhiteSpace(selectorStr))
            {
                UiErrors.MissingSelector(logger, "search");
                return 1;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var selector = selectorService.Parse(selectorStr);
                var matches = await uiAutomation.SearchAsync(session, selector, maxResults + 1, cancellationToken);

                var hasMore = matches.Length > maxResults;
                if (hasMore)
                {
                    matches = matches[..maxResults];
                }

                if (json)
                {
                    var result = new UiSearchResult
                    {
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
                        var elSelector = el.Selector ?? el.Id;
                        var displayName = el.Name ?? el.AutomationId;
                        var name = displayName is not null && displayName != elSelector
                            ? $" [green]\"{EscapeMarkup(displayName)}\"[/]" : "";
                        var value = el.Value is not null && el.Value != el.Name
                            ? $" [yellow]value=\"{EscapeMarkup(el.Value)}\"[/]" : "";
                        var toggle = el.ToggleState is not null ? $" [grey][[{el.ToggleState}]][/]" : "";
                        var expand = el.ExpandState is not null ? $" [grey][[{el.ExpandState}]][/]" : "";
                        var scroll = el.ScrollDir is not null ? $" [grey][[scroll:{el.ScrollDir}]][/]" : "";
                        var bounds = el.Width > 0 ? $" [grey]({el.X},{el.Y} {el.Width}x{el.Height})[/]" : "";
                        ansiConsole.MarkupLine($"  [bold cyan]{EscapeMarkup(elSelector)}[/] {el.Type}{name}{value}{toggle}{expand}{scroll}{bounds}");

                        if (el.InvokableAncestor is { } ancestor)
                        {
                            var ancestorSel = ancestor.Selector ?? ancestor.Id;
                            var aName = ancestor.Name is not null ? $" \"{ancestor.Name}\"" : "";
                            ansiConsole.MarkupLine($"        ^ invoke via: [bold cyan]{EscapeMarkup(ancestorSel)}[/]{aName}");
                        }
                    }
                }

                var moreText = hasMore ? $" (showing first {maxResults})" : "";
                logger.LogInformation("Found {Count} matches{MoreText}", matches.Length, moreText);
                return matches.Length > 0 ? 0 : 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex);
                return 1;
            }
        }

        private static string EscapeMarkup(string text) => Markup.Escape(text.Replace("\r\n", "↵").Replace("\r", "↵").Replace("\n", "↵").Replace("\t", "→"));
    }
}
