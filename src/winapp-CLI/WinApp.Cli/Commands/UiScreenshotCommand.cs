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
        : base("screenshot", "Capture the target window or a specific element as a PNG image. " +
               "With --json, returns base64-encoded PNG inline. With --output, saves to file.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.ModeOption);
        Options.Add(SharedUiOptions.WindowOption);

        Options.Add(WinAppRootCommand.JsonOption);
        Options.Add(SharedUiOptions.OutputOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        IStatusService statusService,
        IAnsiConsole ansiConsole,
        ILogger<UiScreenshotCommand> logger) : AsynchronousCommandLineAction
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
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);

            return await statusService.ExecuteWithStatusAsync(
                "Capturing screenshot...",
                async (taskContext, ct) =>
                {
                    try
                    {
                        var session = await sessionService.ResolveSessionAsync(app, window, mode, ct);
                        var (pixels, width, height) = await uiAutomation.ScreenshotAsync(session, selector, ct);

                        // Encode raw BGRA pixels to PNG via SkiaSharp
                        var pngBytes = EncodePng(pixels, width, height);

                        // Determine save path: --output explicit path, or screenshot.png in cwd
                        var filePath = output ?? "screenshot.png";
                        await File.WriteAllBytesAsync(filePath, pngBytes, ct);
                        // Return absolute path for clarity
                        var absolutePath = Path.GetFullPath(filePath);

                        if (json)
                        {
                            var result = new UiScreenshotResult
                            {
                                ElementId = selector,
                                FilePath = absolutePath,
                                Width = width,
                                Height = height,
                                ProcessId = session.ProcessId,
                                WindowTitle = session.WindowTitle
                            };
                            ansiConsole.Profile.Out.Writer.WriteLine(
                                JsonSerializer.Serialize(result, UiJsonContext.Default.UiScreenshotResult));
                            return (0, "");
                        }

                        return (0, $"Screenshot of \"{session.WindowTitle}\" (PID {session.ProcessId}) saved to {absolutePath} ({width}x{height}, {pngBytes.Length / 1024}KB)");
                    }
                    catch (Exception ex)
                    {
                        taskContext.AddDebugMessage($"Stack trace: {ex.StackTrace}");
                        return (1, $"{UiSymbols.Error} {ex.Message}");
                    }
                },
                cancellationToken);
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
