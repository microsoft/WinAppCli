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
using WinApp.Cli.Services.InteractiveDesktop;

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
        Options.Add(SharedUiOptions.FocusOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        IOwnedWindowFinder ownedWindowFinder,
        ISystemUiQuery systemQuery,
        IAnsiConsole ansiConsole,
        IInteractiveDesktopLock desktopLock,
        ILogger<UiScreenshotCommand> logger) : UiCoordinatedAction(desktopLock, logger)
    {
        protected override string Operation => "ui screenshot";

        /// <remarks>
        /// Spec §6.1/§6.5: a plain screenshot is a background capture and stays an observation.
        /// <c>--focus</c> and <c>--capture-screen</c> need the foreground, so they are desktop-exclusive
        /// from the start. An observation that turns out to need restore or foreground escalates the
        /// whole invocation at run time.
        /// </remarks>
        protected override UiTurnMode ResolveMode(ParseResult parseResult)
            => parseResult.GetValue(SharedUiOptions.FocusOption) || parseResult.GetValue(SharedUiOptions.CaptureScreenOption)
                ? UiTurnMode.DesktopExclusive
                : UiTurnMode.Observe;

        protected override int? Preflight(ParseResult parseResult)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            if (output is not null)
            {
                try
                {
                    _ = Path.GetFullPath(output);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"Invalid output path: {ex.Message}");
                    logger.LogError("{Symbol} Invalid output path: {Message}", UiSymbols.Error, ex.Message);
                    return 1;
                }
            }

            return null;
        }

        protected override async Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);
            var focus = parseResult.GetValue(SharedUiOptions.FocusOption);

            try
            {
                // Spec §6.5: the invocation runs its capture pass observationally first. If ANY target
                // turns out to need restore or foreground, every buffered capture is discarded, the whole
                // invocation escalates to DesktopExclusive, and the pass runs again from the beginning —
                // rediscovering windows and revalidating each one — so the published image never mixes
                // pre- and post-escalation pixels.
                var observeOnly = turn.Mode == UiTurnMode.Observe;
                while (true)
                {
                    try
                    {
                        return await CapturePassAsync(
                            parseResult, turn, selector, app, window, output, json, captureScreen, focus,
                            observeOnly, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DesktopEscalationRequiredException escalation) when (observeOnly)
                    {
                        logger.LogDebug(
                            "Screenshot escalating to an exclusive desktop turn: {Reason}", escalation.Reason);
                        await turn.EscalateToDesktopExclusiveAsync(cancellationToken).ConfigureAwait(false);
                        observeOnly = false;
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.StaleElement(logger, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }

        /// <summary>
        /// One complete capture pass. Buffers pixels and human progress and writes nothing until every
        /// target succeeded, so an escalation partway through discards a consistent set.
        /// </summary>
        private async Task<int> CapturePassAsync(
            ParseResult parseResult, IUiTurn turn, string? selector, string? app, long? window,
            string? output, bool json, bool captureScreen, bool focus, bool observeOnly,
            CancellationToken cancellationToken)
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
                    var multiSession = await sessionService.ResolveSessionAsync(null, main.Hwnd, cancellationToken);
                    return await CaptureMultipleWindows(
                        allWindows, multiSession, turn, output, json, captureScreen, focus, observeOnly, cancellationToken);
                }
            }

            // Single window capture (or element crop)
            var singleSession = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

            // Even for single-window session, check for owned dialogs
            if (selector is null)
            {
                var sessionHwnd = (nint)singleSession.WindowHandle;
                var ownedWindows = ownedWindowFinder.FindOwnedWindows([(sessionHwnd, singleSession.ProcessId, singleSession.WindowTitle ?? "")]);
                if (ownedWindows.Count > 0)
                {
                    var allWindows = new List<(nint Hwnd, int Pid, string Title)>
                    {
                        (sessionHwnd, singleSession.ProcessId, singleSession.WindowTitle ?? "")
                    };
                    allWindows.AddRange(ownedWindows);
                    return await CaptureMultipleWindows(
                        allWindows, singleSession, turn, output, json, captureScreen, focus, observeOnly, cancellationToken);
                }
            }

            var (pixels, w, h) = await uiAutomation.ScreenshotAsync(
                singleSession, selector, captureScreen, focus, turn, observeOnly, cancellationToken);
            var pngBytes = EncodePng(pixels, w, h);

            var filePath = output ?? "screenshot.png";
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
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

        private async Task<int> CaptureMultipleWindows(
            List<(nint Hwnd, int Pid, string Title)> windows,
            UiSessionInfo session,
            IUiTurn turn,
            string? output,
            bool json,
            bool captureScreen,
            bool focus,
            bool observeOnly,
            CancellationToken ct)
        {
            var filePath = output ?? "screenshot.png";

            // Sort: main window first (largest), then others
            var sorted = windows.OrderByDescending(w =>
            {
                var info = UiSessionService.GetWindowInfo(w.Hwnd);
                return (long)info.Width * info.Height;
            }).ToList();

            // Buffered so an escalation partway through this loop discards everything rather than
            // publishing a composite of pre- and post-escalation captures (spec §6.5).
            var progress = new List<string>
            {
                $"[yellow]⚠  {windows.Count} windows detected. Compositing into single image.[/]"
            };

            // Capture each window
            var captures = new List<(byte[] Pixels, int Width, int Height, nint Hwnd, string Title, string Label)>();
            var windowDetails = new List<UiScreenshotWindowInfo>();
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
                    var (pixels, width, height) = await uiAutomation.ScreenshotAsync(
                        windowSession, null, captureScreen, focus, turn, observeOnly, ct);
                    captures.Add((pixels, width, height, w.Hwnd, title, info.Label));
                    windowDetails.Add(new UiScreenshotWindowInfo
                    {
                        Hwnd = w.Hwnd,
                        Title = string.IsNullOrEmpty(w.Title) ? null : w.Title,
                        Label = info.Label,
                        Width = width,
                        Height = height,
                        Captured = true,
                    });

                    var owner = info.OwnerHwnd != 0 ? $", owner: HWND {info.OwnerHwnd}" : "";
                    progress.Add($"  [green]✓[/] HWND [cyan]{w.Hwnd}[/]: \"{Markup.Escape(title)}\" [grey]({info.Label}, {width}x{height}{owner})[/]");
                }
                catch (DesktopEscalationRequiredException)
                {
                    // Propagate so the whole invocation escalates and recaptures from the beginning;
                    // recording a per-window failure here would publish a partially observational image.
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Failed to capture HWND {Hwnd}: {Error}", w.Hwnd, ex.Message);
                    windowDetails.Add(new UiScreenshotWindowInfo
                    {
                        Hwnd = w.Hwnd,
                        Title = string.IsNullOrEmpty(w.Title) ? null : w.Title,
                        Label = info.Label,
                        Captured = false,
                        Error = ex.Message,
                    });
                    progress.Add($"  [red]✗[/] HWND {w.Hwnd}: \"{Markup.Escape(title)}\" — {Markup.Escape(ex.Message)}");
                }
            }

            if (captures.Count == 0)
            {
                // Every window failed for a non-escalation reason, so there is no image to publish — but
                // the buffered per-window diagnostics are exactly what explains the failure.
                if (!json)
                {
                    foreach (var line in progress)
                    {
                        ansiConsole.MarkupLine(line);
                    }
                }

                logger.LogError("No windows could be captured.");
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, "No windows could be captured.");
                return 1;
            }

            // Compose all captures side-by-side into single image
            var pngBytes = ComposeSideBySide(captures);
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            await File.WriteAllBytesAsync(filePath, pngBytes, ct);
            var absolutePath = Path.GetFullPath(filePath);

            // Calculate composite dimensions for JSON output
            var compositeWidth = captures.Sum(c => c.Width) + WindowGap * (captures.Count - 1);
            var compositeHeight = captures.Max(c => c.Height) + LabelBarHeight;

            if (!json)
            {
                // Only now that the image is published does the buffered progress reach the user.
                foreach (var line in progress)
                {
                    ansiConsole.MarkupLine(line);
                }
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
                    Hwnd = session.WindowHandle,
                    Windows = windowDetails.ToArray(),
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
                // Direct HWND — only find windows owned by THIS window (not all process windows).
                // The PID/title reads route through ISystemUiQuery so a valid live handle can be
                // supplied by a fake (real handles resolve identically through the seam).
                var hwndVal = (nint)window.Value;
                uint pid = systemQuery.GetProcessIdForWindow(window.Value);
                if (pid == 0) { return null; }

                var title = systemQuery.GetWindowText(window.Value) ?? "";
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
            var ownedWindows = ownedWindowFinder.FindOwnedWindows(appWindows);
            appWindows.AddRange(ownedWindows);

            return appWindows.Count > 1 ? appWindows : null;
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
