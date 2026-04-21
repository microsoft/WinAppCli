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

internal class UiListWindowsCommand : Command, IShortDescription
{
    public string ShortDescription => "List all visible windows, optionally filtered by app";

    public UiListWindowsCommand()
        : base("list-windows", "List all visible windows with their HWND, title, process, and size. " +
               "Use -a to filter by app name. Use the HWND with -w to target a specific window.")
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiAutomationService uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiListWindowsCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            try
            {
                List<(nint Hwnd, int Pid, string Title)> windows;

                if (!string.IsNullOrWhiteSpace(app))
                {
                    // Try as PID first
                    if (int.TryParse(app, out var pid))
                    {
                        windows = uiAutomation.FindWindowsByPid(pid);
                    }
                    else
                    {
                        // Try process name match, then title match
                        var byName = System.Diagnostics.Process.GetProcessesByName(app);
                        if (byName.Length > 0)
                        {
                            windows = [];
                            foreach (var process in byName)
                            {
                                windows.AddRange(uiAutomation.FindWindowsByPid(process.Id));
                            }
                        }
                        else
                        {
                            // Partial process name
                            var partial = System.Diagnostics.Process.GetProcesses()
                                .Where(p =>
                                {
                                    try { return p.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase); }
                                    catch { return false; }
                                })
                                .ToArray();

                            if (partial.Length > 0)
                            {
                                windows = [];
                                foreach (var p in partial)
                                {
                                    windows.AddRange(uiAutomation.FindWindowsByPid(p.Id));
                                }
                            }
                            else
                            {
                                // Fall back to title search
                                windows = uiAutomation.FindWindowsByTitle(app);
                            }
                        }
                    }
                }
                else
                {
                    // No filter — list ALL visible windows
                    windows = uiAutomation.FindWindowsByTitle("");
                }

                if (json)
                {
                    var foregroundHwnd = (long)Windows.Win32.PInvoke.GetForegroundWindow();
                    var results = windows.Select(w =>
                    {
                        var info = UiSessionService.GetWindowInfo(w.Hwnd);
                        return new WindowInfo
                        {
                            Hwnd = w.Hwnd,
                            ProcessId = w.Pid,
                            ProcessName = GetProcessNameSafe(w.Pid),
                            Title = string.IsNullOrEmpty(w.Title) ? null : w.Title,
                            Label = info.Label,
                            Width = info.Width,
                            Height = info.Height,
                            OwnerHwnd = (long)info.OwnerHwnd,
                            ClassName = info.ClassName,
                            IsForeground = w.Hwnd == foregroundHwnd
                        };
                    }).ToArray();
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(results, UiJsonContext.Default.WindowInfoArray));
                    return 0;
                }

                // Human-readable output with metadata
                var fgHwnd = (nint)Windows.Win32.PInvoke.GetForegroundWindow();
                foreach (var w in windows)
                {
                    var procName = Markup.Escape(GetProcessNameSafe(w.Pid));
                    var title = string.IsNullOrEmpty(w.Title) ? "(no title)" : Markup.Escape(w.Title);
                    var info = UiSessionService.GetWindowInfo(w.Hwnd);
                    var label = Markup.Escape(info.Label);
                    var className = Markup.Escape(info.ClassName);
                    var fg = w.Hwnd == fgHwnd ? ", [green]foreground[/]" : "";
                    var owner = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                    ansiConsole.MarkupLine($"  HWND [cyan]{w.Hwnd}[/]: \"{title}\" [grey]({label}, {info.Width}x{info.Height}{fg}{owner}) [[{className}]] ({procName}, PID {w.Pid})[/]");
                }

                logger.LogInformation("Found {Count} windows", windows.Count);
                return 0;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex);
                return 1;
            }
        }

        private static string GetProcessNameSafe(int pid)
        {
            try { return System.Diagnostics.Process.GetProcessById(pid).ProcessName; }
            catch { return "Unknown"; }
        }
    }
}
