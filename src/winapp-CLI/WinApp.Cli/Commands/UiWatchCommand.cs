// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiWatchCommand : Command, IShortDescription
{
    public string ShortDescription => "Listen for UI events (focus, window open/close, invoke, ...)";

    public static Option<string[]> EventOption { get; }
    public static Option<int> DurationSecOption { get; }
    public static Option<int> MaxEventsOption { get; }

    static UiWatchCommand()
    {
        EventOption = new Option<string[]>("--event", "-e")
        {
            Description = "Event type to listen for (repeatable). Allowed: focus, window-open, window-close, invoke, " +
                          "selection, text-changed, property-changed, structure-changed, notification, live-region. " +
                          "Default: focus, window-open, window-close, invoke, selection.",
            AllowMultipleArgumentsPerToken = true,
        };

        DurationSecOption = new Option<int>("--duration-sec")
        {
            Description = "Seconds to listen. 0 = until Ctrl+C / cancellation.",
            DefaultValueFactory = _ => 0,
        };

        MaxEventsOption = new Option<int>("--max-events", "-n")
        {
            Description = "Stop after this many events. 0 = unlimited.",
            DefaultValueFactory = _ => 0,
        };
    }

    public UiWatchCommand()
        : base("watch", "Listen for UIA / WinEvent notifications from a running app and stream them as they occur. " +
               "With --json, emits NDJSON (one compact JSON object per event line) followed by a summary line.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(EventOption);
        Options.Add(SharedUiOptions.PropertyOption);
        Options.Add(DurationSecOption);
        Options.Add(MaxEventsOption);
        Options.Add(SharedUiOptions.OutputOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiEventWatcher watcher,
        ISelectorService selectorService,
        IUiAutomationService uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiWatchCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selectorStr = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            var requestedEvents = parseResult.GetValue(EventOption) ?? [];
            var property = parseResult.GetValue(SharedUiOptions.PropertyOption);
            var durationSec = parseResult.GetValue(DurationSecOption);
            var maxEvents = parseResult.GetValue(MaxEventsOption);
            var outputPath = parseResult.GetValue(SharedUiOptions.OutputOption);

            // Validate + normalize the event set.
            var events = new List<string>();
            foreach (var e in requestedEvents)
            {
                var normalized = e.Trim().ToLowerInvariant();
                if (!UiWatchEvents.All.Contains(normalized))
                {
                    var msg = $"Unknown event '{e}'. Allowed: {string.Join(", ", UiWatchEvents.All)}.";
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, msg);
                    return 1;
                }
                if (!events.Contains(normalized))
                {
                    events.Add(normalized);
                }
            }
            if (events.Count == 0)
            {
                events.AddRange(UiWatchEvents.Default);
            }

            // Validate --property up front against the supported set. Only Name/Value/ToggleState are
            // subscribed for property-changed; any other value would silently yield zero events.
            var normalizedProperty = property;
            if (!string.IsNullOrWhiteSpace(property))
            {
                var match = UiWatchEvents.SupportedProperties.FirstOrDefault(
                    p => string.Equals(p, property, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    var msg = $"Unsupported --property '{property}'. Supported: {string.Join(", ", UiWatchEvents.SupportedProperties)}.";
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, msg);
                    return 1;
                }
                normalizedProperty = match;
            }

            StreamWriter? logFile = null;
            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                var hasSelector = !string.IsNullOrWhiteSpace(selectorStr);

                // Element-scoped events require a UIA scope. Without a window handle there is no safe
                // scope (watching the desktop root would leak events from every process), so fail fast.
                if (UiWatchEvents.RequiresElementScope(events) && session.WindowHandle == 0)
                {
                    var msg = "Element-scoped events (focus, invoke, property-changed, ...) require a target window. " +
                              "Pass -w <HWND> or an --app that resolves to a window, or watch only window-open/window-close.";
                    logger.LogError("{Symbol} {Message}", UiSymbols.Error, msg);
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, msg);
                    return 1;
                }

                // Resolve the selector to a concrete element so the watcher can scope registration to
                // its subtree. A selector that cannot be resolved is a hard error (not a silent
                // whole-window watch).
                UiElement? scopeElement = null;
                if (hasSelector)
                {
                    var expression = selectorService.Parse(selectorStr!);
                    scopeElement = await uiAutomation.FindSingleElementAsync(session, expression, cancellationToken);
                    if (scopeElement is null)
                    {
                        UiErrors.ElementNotFound(logger, selectorStr!, json);
                        return 1;
                    }
                }

                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    logFile = new StreamWriter(outputPath, append: false, Encoding.UTF8);
                }

                if (!json)
                {
                    logger.LogInformation("Watching {App} for [{Events}] — press Ctrl+C to stop.",
                        app ?? $"hwnd:{window}", string.Join(", ", events));
                }

                var request = new UiWatchRequest
                {
                    Events = events,
                    Property = normalizedProperty,
                    Selector = hasSelector ? selectorStr : null,
                    ScopeElement = scopeElement,
                    MaxEvents = maxEvents,
                    DurationSec = durationSec,
                };

                var sw = Stopwatch.StartNew();

                void OnEvent(UiWatchEvent evt)
                {
                    var line = json ? FormatJson(evt) : FormatHuman(evt);
                    ansiConsole.Profile.Out.Writer.WriteLine(line);
                    logFile?.WriteLine(line);
                }

                var outcome = await watcher.WatchAsync(session, request, OnEvent, cancellationToken);
                sw.Stop();

                var durationMs = outcome.DurationMs > 0 ? outcome.DurationMs : sw.ElapsedMilliseconds;

                if (json)
                {
                    var summary = new UiWatchSummary { Events = outcome.Events, DurationMs = durationMs };
                    var summaryLine = JsonSerializer.Serialize(summary, UiWatchJsonContext.Default.UiWatchSummary);
                    ansiConsole.Profile.Out.Writer.WriteLine(summaryLine);
                    logFile?.WriteLine(summaryLine);
                }
                else
                {
                    logger.LogInformation("Observed {Count} event(s) in {Ms}ms.", outcome.Events, durationMs);
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C is the normal way to end an open-ended watch.
                return 0;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
            finally
            {
                logFile?.Dispose();
            }
        }

        private static string FormatJson(UiWatchEvent evt)
            => JsonSerializer.Serialize(evt, UiWatchJsonContext.Default.UiWatchEvent);

        private static string FormatHuman(UiWatchEvent evt)
        {
            var el = evt.Element;
            var label = el is null
                ? ""
                : $" {el.ControlType ?? "?"} '{el.Name ?? el.Selector ?? ""}'";
            var detail = string.IsNullOrEmpty(evt.Detail) ? "" : $" ({evt.Detail})";
            return $"{evt.Ts}  {evt.Event}{label}{detail}";
        }
    }
}
