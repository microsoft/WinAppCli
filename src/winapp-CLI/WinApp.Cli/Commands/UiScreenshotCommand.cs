// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiScreenshotCommand : Command, IShortDescription
{
    public string ShortDescription => "Capture a screenshot of a window or element";

    public UiScreenshotCommand()
        : base("screenshot", "Capture the target window or element as a PNG image. " +
               "When multiple windows exist (e.g., dialogs), captures each to a separate file. " +
               "With --json, returns file path and dimensions. Use --capture-screen for popup overlays.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(SharedUiOptions.CaptureScreenOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiScreenshotCommand> logger) : AsynchronousCommandLineAction
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
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);

            try
            {
                // Screenshot handles multi-window discovery itself (avoids duplicate warning from session resolution)
                if (selector is null)
                {
                    var allWindows = DiscoverAllWindows(app, window);
                    if (allWindows is not null && allWindows.Count > 1)
                    {
                        // Resolve session using the largest window's HWND (suppresses session multi-window warning)
                        var main = allWindows.OrderByDescending(w =>
                        {
                            var info = UiSessionService.GetWindowInfo(w.Hwnd);
                            return (long)info.Width * info.Height;
                        }).First();
                        var session = await sessionService.ResolveSessionAsync(null, main.Hwnd, cancellationToken);
                        return await CaptureMultipleWindows(allWindows, session, output, json, captureScreen, cancellationToken);
                    }
                }

                // Single window capture (or element crop)
                var singleSession = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                // Even for single-window session, check for owned dialogs
                if (selector is null)
                {
                    var sessionHwnd = (nint)singleSession.WindowHandle;
                    var ownedWindows = FindOwnedWindows([(sessionHwnd, singleSession.ProcessId, singleSession.WindowTitle ?? "")]);
                    if (ownedWindows.Count > 0)
                    {
                        var allWindows = new List<(nint Hwnd, int Pid, string Title)>
                        {
                            (sessionHwnd, singleSession.ProcessId, singleSession.WindowTitle ?? "")
                        };
                        allWindows.AddRange(ownedWindows);
                        return await CaptureMultipleWindows(allWindows, singleSession, output, json, captureScreen, cancellationToken);
                    }
                }

                var (pixels, w, h) = await uiAutomation.ScreenshotAsync(singleSession, selector, captureScreen, cancellationToken);
                var pngBytes = EncodePng(pixels, w, h);

                var filePath = output ?? "screenshot.png";
                await File.WriteAllBytesAsync(filePath, pngBytes, cancellationToken);
                var absolutePath = Path.GetFullPath(filePath);

                if (json)
                {
                    var result = new UiScreenshotResult
                    {
                        ElementId = selector,
                        FilePath = absolutePath,
                        Width = w,
                        Height = h,
                        ProcessId = singleSession.ProcessId,
                        WindowTitle = singleSession.WindowTitle
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(result, UiJsonContext.Default.UiScreenshotResult));
                    return 0;
                }

                logger.LogInformation("Screenshot of \"{WindowTitle}\" (PID {ProcessId}) saved to {Path} ({Width}x{Height}, {Size}KB)", singleSession.WindowTitle, singleSession.ProcessId, absolutePath, w, h, pngBytes.Length / 1024);
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

        private async Task<int> CaptureMultipleWindows(
            List<(nint Hwnd, int Pid, string Title)> windows,
            UiSessionInfo session,
            string? output,
            bool json,
            bool captureScreen,
            CancellationToken ct)
        {
            var basePath = output ?? "screenshot.png";
            var ext = Path.GetExtension(basePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
            var dir = Path.GetDirectoryName(basePath) ?? ".";

            var results = new List<UiScreenshotResult>();

            // Sort: main window first (largest), then others
            var sorted = windows.OrderByDescending(w =>
            {
                var info = UiSessionService.GetWindowInfo(w.Hwnd);
                return (long)info.Width * info.Height;
            }).ToList();

            ansiConsole.MarkupLine($"[yellow]⚠  {windows.Count} windows detected. Capturing each separately.[/]");

            for (var i = 0; i < sorted.Count; i++)
            {
                var w = sorted[i];
                var info = UiSessionService.GetWindowInfo(w.Hwnd);
                var title = string.IsNullOrEmpty(w.Title) ? "(no title)" : w.Title;

                // File naming: screenshot.png for first, screenshot.HWND-type.png for others
                var filePath = i == 0
                    ? basePath
                    : Path.Combine(dir, $"{nameWithoutExt}.{w.Hwnd}-{info.Label}{ext}");

                try
                {
                    var windowSession = new UiSessionInfo
                    {
                        ProcessId = w.Pid,
                        ProcessName = session.ProcessName,
                        WindowTitle = title,
                        WindowHandle = w.Hwnd
                    };
                    var (pixels, width, height) = await uiAutomation.ScreenshotAsync(windowSession, null, captureScreen, ct);
                    var pngBytes = EncodePng(pixels, width, height);
                    await File.WriteAllBytesAsync(filePath, pngBytes, ct);
                    var absolutePath = Path.GetFullPath(filePath);

                    var owner = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                    ansiConsole.MarkupLine($"  [green]✓[/] {absolutePath} — [grey]HWND [cyan]{w.Hwnd}[/]: \"{Markup.Escape(title)}\" ({info.Label}, {width}x{height}{owner})[/]");

                    results.Add(new UiScreenshotResult
                    {
                        FilePath = absolutePath,
                        Width = width,
                        Height = height,
                        ProcessId = w.Pid,
                        WindowTitle = title
                    });
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Failed to capture HWND {Hwnd}: {Error}", w.Hwnd, ex.Message);
                    ansiConsole.MarkupLine($"  [red]✗[/] HWND {w.Hwnd}: \"{Markup.Escape(title)}\" — {Markup.Escape(ex.Message)}");
                }
            }

            if (json)
            {
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(results.ToArray(), UiJsonContext.Default.UiScreenshotResultArray));
            }

            return 0;
        }

        /// <summary>Find windows from other processes that are owned by any of the given windows.</summary>
        /// <summary>
        /// Discover all windows for the target app, including cross-process owned windows.
        /// Returns null if we can't determine the app's windows (e.g., no --app provided).
        /// </summary>
        private List<(nint Hwnd, int Pid, string Title)>? DiscoverAllWindows(string? app, long? window)
        {
            List<(nint Hwnd, int Pid, string Title)> appWindows;

            if (window is not null and > 0)
            {
                // Direct HWND — only find windows owned by THIS window (not all process windows)
                var hwndVal = (nint)window.Value;
                uint pid = 0;
                unsafe
                {
                    Windows.Win32.PInvoke.GetWindowThreadProcessId(
                        new Windows.Win32.Foundation.HWND(hwndVal), &pid);
                }
                if (pid == 0) { return null; }

                // Get title for this window
                var titleChars = new char[512];
                string title;
                unsafe
                {
                    fixed (char* buffer = titleChars)
                    {
                        var len = Windows.Win32.PInvoke.GetWindowText(
                            new Windows.Win32.Foundation.HWND(hwndVal), buffer, 512);
                        title = len > 0 ? new string(buffer, 0, len) : "";
                    }
                }

                appWindows = [(hwndVal, (int)pid, title)];
            }
            else if (!string.IsNullOrWhiteSpace(app))
            {
                // Find by app name — get all windows for matching processes
                if (int.TryParse(app, out var pid))
                {
                    appWindows = uiAutomation.FindWindowsByPid(pid);
                }
                else
                {
                    var processes = System.Diagnostics.Process.GetProcessesByName(app);
                    if (processes.Length == 0)
                    {
                        // Try partial match
                        processes = System.Diagnostics.Process.GetProcesses()
                            .Where(p => { try { return p.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase); } catch { return false; } })
                            .ToArray();
                    }
                    appWindows = [];
                    foreach (var p in processes)
                    {
                        appWindows.AddRange(uiAutomation.FindWindowsByPid(p.Id));
                    }
                }
            }
            else
            {
                return null;
            }

            // Also find cross-process owned windows
            var ownedWindows = FindOwnedWindows(appWindows);
            appWindows.AddRange(ownedWindows);

            return appWindows.Count > 1 ? appWindows : null;
        }

        private static List<(nint Hwnd, int Pid, string Title)> FindOwnedWindows(List<(nint Hwnd, int Pid, string Title)> appWindows)
        {
            var appHwnds = new HashSet<nint>(appWindows.Select(w => w.Hwnd));
            var owned = new List<(nint Hwnd, int Pid, string Title)>();

            // Enumerate all visible windows and check ownership
            var hwnd = Windows.Win32.Foundation.HWND.Null;
            while (true)
            {
                hwnd = Windows.Win32.PInvoke.FindWindowEx(
                    Windows.Win32.Foundation.HWND.Null, hwnd, null, (string?)null);
                if (hwnd.IsNull) { break; }
                if (!Windows.Win32.PInvoke.IsWindowVisible(hwnd)) { continue; }

                // Skip windows already in the list
                if (appHwnds.Contains((nint)hwnd)) { continue; }

                // Check if this window is owned by one of our app windows
                var owner = Windows.Win32.PInvoke.GetWindow(hwnd,
                    Windows.Win32.UI.WindowsAndMessaging.GET_WINDOW_CMD.GW_OWNER);
                if (!owner.IsNull && appHwnds.Contains((nint)owner))
                {
                    unsafe
                    {
                        uint pid = 0;
                        Windows.Win32.PInvoke.GetWindowThreadProcessId(hwnd, &pid);
                        var titleChars = new char[512];
                        fixed (char* buffer = titleChars)
                        {
                            var len = Windows.Win32.PInvoke.GetWindowText(hwnd, buffer, 512);
                            var title = len > 0 ? new string(buffer, 0, len) : "";
                            owned.Add(((nint)hwnd, (int)pid, title));
                        }
                    }
                }
            }

            return owned;
        }

        private static byte[] EncodePng(byte[] bgraPixels, int width, int height)
        {
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            unsafe
            {
                var ptr = (byte*)bitmap.GetPixels().ToPointer();
                System.Runtime.InteropServices.Marshal.Copy(bgraPixels, 0, (nint)ptr, bgraPixels.Length);
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
