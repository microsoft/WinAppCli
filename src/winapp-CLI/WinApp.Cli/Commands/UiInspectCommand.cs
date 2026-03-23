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
                logger.LogError("{Symbol} Specify --app (name/title/PID) or --window (HWND).", UiSymbols.Error);
                return 1;
            }
            var depth = parseResult.GetRequiredValue(SharedUiOptions.DepthOption);
            var ancestors = parseResult.GetValue(AncestorsOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var interactive = parseResult.GetValue(SharedUiOptions.InteractiveOption);
            var hideDisabled = parseResult.GetValue(SharedUiOptions.HideDisabledOption);
            var hideOffscreen = parseResult.GetValue(SharedUiOptions.HideOffscreenOption);

            // --interactive bumps default depth to 8 (sparse tree after filtering)
            if (interactive && depth == 3)
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
                        var name = displayName is not null ? $" \"{displayName}\"" : "";
                        var value = el.Value is not null && el.Value != el.Name ? $" value=\"{el.Value}\"" : "";
                        var toggle = el.ToggleState is not null ? $" [{el.ToggleState}]" : "";
                        var expand = el.ExpandState is not null ? $" [{el.ExpandState}]" : "";
                        var scroll = el.ScrollDir is not null ? $" [scroll:{el.ScrollDir}]" : "";
                        var bounds = el.Width > 0 ? $" ({el.X},{el.Y} {el.Width}x{el.Height})" : "";
                        var disabled = el.IsEnabled ? "" : " [disabled]";
                        var offscreen = el.IsOffscreen ? " [offscreen]" : "";
                        ansiConsole.WriteLine($"{indent}{elSelector} {el.Type}{name}{value}{toggle}{expand}{scroll}{bounds}{disabled}{offscreen}");
                    }
                }

                logger.LogInformation("Found {Count} elements (depth {Depth})", elements.Length, depth);
                return 0;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                logger.LogError("Failed to access UI element — the element may no longer exist or the app may have navigated. Try re-running 'inspect'.");
                return 1;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
                logger.LogError("{Message}", ex.Message);
                return 1;
            }
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
