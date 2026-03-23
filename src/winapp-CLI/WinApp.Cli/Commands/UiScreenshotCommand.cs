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
                logger.LogError("{Symbol} Specify --app (name/title/PID) or --window (HWND).", UiSymbols.Error);
                return 1;
            }
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);
                var (pixels, width, height) = await uiAutomation.ScreenshotAsync(session, selector, captureScreen, cancellationToken);

                // Encode raw BGRA pixels to PNG via SkiaSharp
                var pngBytes = EncodePng(pixels, width, height);

                // Determine save path: --output explicit path, or screenshot.png in cwd
                var filePath = output ?? "screenshot.png";
                await File.WriteAllBytesAsync(filePath, pngBytes, cancellationToken);
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
                    return 0;
                }

                logger.LogInformation("Screenshot of \"{WindowTitle}\" (PID {ProcessId}) saved to {Path} ({Width}x{Height}, {Size}KB)", session.WindowTitle, session.ProcessId, absolutePath, width, height, pngBytes.Length / 1024);
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
