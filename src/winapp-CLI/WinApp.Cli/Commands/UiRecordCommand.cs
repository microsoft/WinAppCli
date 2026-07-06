// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiRecordCommand : Command, IShortDescription
{
    public string ShortDescription => "Record a window or element region to an MP4 (H.264) video";

    public UiRecordCommand()
        : base("record", "Record the target window (or an element's region) to an H.264 MP4 video. " +
               "Captures frames via Windows Graphics Capture and encodes with Media Foundation. " +
               "Use --duration-sec 0 to record until Ctrl+C. Use --capture-screen to include overlays/popups.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(SharedUiOptions.DurationSecOption);
        Options.Add(SharedUiOptions.FpsOption);
        Options.Add(SharedUiOptions.MaxEdgeOption);
        Options.Add(SharedUiOptions.CaptureScreenOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiSessionService sessionService,
        IUiAutomationService uiAutomation,
        IAnsiConsole ansiConsole,
        ILogger<UiRecordCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            var durationSec = parseResult.GetValue(SharedUiOptions.DurationSecOption);
            var fps = parseResult.GetValue(SharedUiOptions.FpsOption);
            var maxEdge = parseResult.GetValue(SharedUiOptions.MaxEdgeOption);
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);

            if (durationSec < 0)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, "--duration-sec must be 0 or greater.");
                logger.LogError("{Symbol} --duration-sec must be 0 or greater.", UiSymbols.Error);
                return 1;
            }
            if (fps < 1)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, "--fps must be at least 1.");
                logger.LogError("{Symbol} --fps must be at least 1.", UiSymbols.Error);
                return 1;
            }
            if (maxEdge < 0)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInternalError, "--max-edge must be 0 or greater.");
                logger.LogError("{Symbol} --max-edge must be 0 or greater.", UiSymbols.Error);
                return 1;
            }

            var filePath = output ?? $"recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4";
            filePath = Path.GetFullPath(filePath);
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                if (!json)
                {
                    var until = durationSec > 0 ? $"{durationSec}s" : "Ctrl+C";
                    ansiConsole.MarkupLine($"[grey]Recording \"{Markup.Escape(session.WindowTitle ?? "")}\" (PID {session.ProcessId}) to {Markup.Escape(filePath)} — until {until}, {fps} fps…[/]");
                }

                var options = new RecordOptions
                {
                    OutputPath = filePath,
                    DurationSec = durationSec,
                    Fps = fps,
                    MaxEdge = maxEdge,
                    CaptureScreen = captureScreen,
                };

                var result = await uiAutomation.RecordAsync(session, selector, options, cancellationToken);

                if (json)
                {
                    var payload = new UiRecordResult
                    {
                        Path = filePath,
                        DurationSec = durationSec,
                        Fps = fps,
                        Frames = result.Frames,
                        Width = result.Width,
                        Height = result.Height,
                        FileSize = result.FileSize,
                        Codec = "h264",
                        Mode = result.Mode,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(payload, UiJsonContext.Default.UiRecordResult));
                    return 0;
                }

                logger.LogInformation(
                    "Recorded {Frames} frames ({Width}x{Height}, h264) to {Path} ({Size}KB)",
                    result.Frames, result.Width, result.Height, filePath, result.FileSize / 1024);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation before any recording started (e.g. session resolution). The recorder
                // itself finalizes the MP4 on Ctrl+C, so this only fires for pre-capture cancellation.
                logger.LogDebug("Recording cancelled before capture started.");
                return 1;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                logger.LogDebug("COM error: {HResult} {StackTrace}", comEx.HResult, comEx.StackTrace);
                UiErrors.GenericError(logger, comEx, json);
                return 1;
            }
            catch (Exception ex)
            {
                UiErrors.GenericError(logger, ex, json);
                return 1;
            }
        }
    }
}
