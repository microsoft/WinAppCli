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
                        WindowTitle = singleSession.WindowTitle,
                        Hwnd = singleSession.WindowHandle
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
            var filePath = output ?? "screenshot.png";

            // Sort: main window first (largest), then others
            var sorted = windows.OrderByDescending(w =>
            {
                var info = UiSessionService.GetWindowInfo(w.Hwnd);
                return (long)info.Width * info.Height;
            }).ToList();

            if (!json)
            {
                ansiConsole.MarkupLine($"[yellow]⚠  {windows.Count} windows detected. Compositing into single image.[/]");
            }

            // Capture each window
            var captures = new List<(byte[] Pixels, int Width, int Height, nint Hwnd, string Title, string Label)>();
            foreach (var w in sorted)
            {
                var info = UiSessionService.GetWindowInfo(w.Hwnd);
                var title = string.IsNullOrEmpty(w.Title) ? "(no title)" : w.Title;
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
                    captures.Add((pixels, width, height, w.Hwnd, title, info.Label));

                    if (!json)
                    {
                        var owner = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                        ansiConsole.MarkupLine($"  [green]✓[/] HWND [cyan]{w.Hwnd}[/]: \"{Markup.Escape(title)}\" [grey]({info.Label}, {width}x{height}{owner})[/]");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Failed to capture HWND {Hwnd}: {Error}", w.Hwnd, ex.Message);
                    if (!json)
                    {
                        ansiConsole.MarkupLine($"  [red]✗[/] HWND {w.Hwnd}: \"{Markup.Escape(title)}\" — {Markup.Escape(ex.Message)}");
                    }
                }
            }

            if (captures.Count == 0)
            {
                logger.LogError("No windows could be captured.");
                return 1;
            }

            // Compose all captures side-by-side into single image
            var pngBytes = ComposeSideBySide(captures);
            await File.WriteAllBytesAsync(filePath, pngBytes, ct);
            var absolutePath = Path.GetFullPath(filePath);

            // Calculate composite dimensions for JSON output
            var compositeWidth = captures.Sum(c => c.Width) + WindowGap * (captures.Count - 1);
            var compositeHeight = captures.Max(c => c.Height) + LabelBarHeight;

            if (!json)
            {
                ansiConsole.MarkupLine($"  [green]✓[/] Saved composite: {absolutePath}");
            }

            if (json)
            {
                var result = new UiScreenshotResult
                {
                    FilePath = absolutePath,
                    Width = compositeWidth,
                    Height = compositeHeight,
                    ProcessId = session.ProcessId,
                    WindowTitle = session.WindowTitle,
                    Hwnd = session.WindowHandle
                };
                ansiConsole.Profile.Out.Writer.WriteLine(
                    JsonSerializer.Serialize(result, UiJsonContext.Default.UiScreenshotResult));
            }

            return 0;
        }

        private const int LabelBarHeight = 28;
        private const int WindowGap = 8;

        private static byte[] ComposeSideBySide(List<(byte[] Pixels, int Width, int Height, nint Hwnd, string Title, string Label)> captures)
        {
            // Calculate composite dimensions
            var totalWidth = captures.Sum(c => c.Width) + WindowGap * (captures.Count - 1);
            var maxHeight = captures.Max(c => c.Height);
            var compositeHeight = maxHeight + LabelBarHeight;

            using var composite = new SKBitmap(totalWidth, compositeHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(composite);

            // Dark background
            canvas.Clear(new SKColor(30, 30, 30));

            using var labelPaint = new SKPaint
            {
                Color = SKColors.White,
                TextSize = 14,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal)
            };
            using var typeface = labelPaint.Typeface;
            using var labelBgPaint = new SKPaint { Color = new SKColor(50, 50, 50) };

            var x = 0;
            foreach (var (pixels, width, height, hwnd, title, label) in captures)
            {
                // Draw label bar
                canvas.DrawRect(x, 0, width, LabelBarHeight, labelBgPaint);
                var labelText = $"HWND {hwnd} ({label})  {title}";
                if (labelText.Length > 60) { labelText = labelText[..57] + "..."; }
                canvas.DrawText(labelText, x + 6, LabelBarHeight - 8, labelPaint);

                // Draw window capture
                using var windowBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    var ptr = (byte*)windowBitmap.GetPixels().ToPointer();
                    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, (nint)ptr, pixels.Length);
                }
                canvas.DrawBitmap(windowBitmap, x, LabelBarHeight);

                x += width + WindowGap;
            }

            using var image = SKImage.FromBitmap(composite);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

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
