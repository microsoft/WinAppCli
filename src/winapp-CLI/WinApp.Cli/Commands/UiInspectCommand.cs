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
        : base("inspect", "View the UI element tree with semantic slugs, element types, names, and bounds.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.DepthOption);
        Options.Add(AncestorsOption);
        Options.Add(SharedUiOptions.InteractiveOption);
        Options.Add(SharedUiOptions.HideDisabledOption);
        Options.Add(SharedUiOptions.HideOffscreenOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiInspectCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger);
                return 1;
            }
            var depth = parseResult.GetRequiredValue(SharedUiOptions.DepthOption);
            var ancestors = parseResult.GetValue(AncestorsOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var interactive = parseResult.GetValue(SharedUiOptions.InteractiveOption);
            var hideDisabled = parseResult.GetValue(SharedUiOptions.HideDisabledOption);
            var hideOffscreen = parseResult.GetValue(SharedUiOptions.HideOffscreenOption);

            // --interactive bumps default depth to 8 (sparse tree after filtering)
            if (interactive && depth == 5)
            {
                depth = 8;
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                Models.UiElement[] elements;

                if (ancestors && selector is not null)
                {
                    elements = await uiAutomation.InspectAncestorsAsync(session, selector, cancellationToken);
                }
                else
                {
                    elements = await uiAutomation.InspectAsync(session, selector, depth, cancellationToken);
                }

                // Apply filters
                if (interactive)
                {
                    elements = elements.Where(IsInteractiveType).ToArray();
                }
                if (hideDisabled)
                {
                    elements = elements.Where(e => e.IsEnabled).ToArray();
                }
                if (hideOffscreen)
                {
                    elements = elements.Where(e => !e.IsOffscreen).ToArray();
                }

                if (json)
                {
                    var result = new UiInspectResult { Elements = elements };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiInspectResult));
                }
                else
                {
                    foreach (var el in elements)
                    {
                        var indent = new string(' ', el.Depth * 2);
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
                        var disabled = el.IsEnabled ? "" : " [grey][[disabled]][/]";
                        var offscreen = el.IsOffscreen ? " [grey][[offscreen]][/]" : "";
                        ansiConsole.MarkupLine($"{indent}[bold cyan]{EscapeMarkup(elSelector)}[/] {el.Type}{name}{value}{toggle}{expand}{scroll}{bounds}{disabled}{offscreen}");
                    }

                    // Footer with example using first interactive element or first element
                    var example = elements.FirstOrDefault(IsInteractiveType) ?? elements.FirstOrDefault();
                    var exampleSelector = example?.Selector ?? example?.Id;
                    var exampleHint = exampleSelector is not null
                        ? $" Use the [bold cyan]first token[/] as selector, e.g.: [grey]winapp ui invoke {EscapeMarkup(exampleSelector)} -a <app>[/]"
                        : "";
                    ansiConsole.MarkupLine($"[grey]Found {elements.Length} elements (--depth {depth}).{exampleHint}[/]");
                }

                logger.LogDebug("Inspect returned {Count} elements at depth {Depth}", elements.Length, depth);
                return 0;
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

        private static string EscapeMarkup(string text) => Markup.Escape(SanitizeForDisplay(text));

        /// <summary>Replace control characters (newlines, tabs, carriage returns) with visual representations for single-line display.</summary>
        private static string SanitizeForDisplay(string text)
        {
            if (text.AsSpan().IndexOfAny('\r', '\n', '\t') < 0)
            {
                return text;
            }
            return text.Replace("\r\n", "↵").Replace("\r", "↵").Replace("\n", "↵").Replace("\t", "→");
        }

        private static readonly HashSet<string> InteractiveTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Button", "CheckBox", "ComboBox", "Edit", "TextBox", "Hyperlink",
            "ListItem", "MenuItem", "RadioButton", "Tab", "TabItem", "SplitButton",
            "TreeItem", "DataItem", "Slider"
        };

        private static bool IsInteractiveType(Models.UiElement el) => InteractiveTypes.Contains(el.Type);
    }
}
