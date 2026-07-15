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
               "By default records until stopped (Ctrl+C, or a newline/EOF on stdin for programmatic callers). " +
               "Use --duration-sec N for a timed run. A valid MP4 is always finalized on graceful stop. " +
               "Use --capture-screen to include overlays/popups.")
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
        // Test seams: override Console.IsInputRedirected and Console.In without process-level side effects.
        internal static Func<bool>? s_isInputRedirectedOverride;
        internal static TextReader? s_stdinOverride;

        // H1: Guard flag for the stdin monitor cancel callback. Set to true (BEFORE linkedCts
        // is disposed) so the monitor's callback can safely skip Cancel() on a disposed CTS.
        // volatile ensures the background thread sees the write from the main thread.
        private volatile bool _stdinMonitorStopped;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var selector = parseResult.GetValue(SharedUiOptions.SelectorArgument);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var durationSec = parseResult.GetValue(SharedUiOptions.DurationSecOption);
            var fps = parseResult.GetValue(SharedUiOptions.FpsOption);
            var maxEdge = parseResult.GetValue(SharedUiOptions.MaxEdgeOption);
            var captureScreen = parseResult.GetValue(SharedUiOptions.CaptureScreenOption);
            var output = parseResult.GetValue(SharedUiOptions.OutputOption);

            // Validate all numeric/range arguments before requiring a target, so bad argument
            // values produce invalid_arguments regardless of whether a target was supplied.
            if (durationSec < 0)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--duration-sec must be 0 or greater.");
                logger.LogError("{Symbol} --duration-sec must be 0 or greater.", UiSymbols.Error);
                return 1;
            }
            if (durationSec > 86400)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--duration-sec must not exceed 86400 (24 hours).");
                logger.LogError("{Symbol} --duration-sec must not exceed 86400 (24 hours).", UiSymbols.Error);
                return 1;
            }
            if (fps < 1)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--fps must be at least 1.");
                logger.LogError("{Symbol} --fps must be at least 1.", UiSymbols.Error);
                return 1;
            }
            if (maxEdge != 0 && maxEdge < 64)
            {
                UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, "--max-edge must be 0 (unbounded) or >= 64 (encoder minimum).");
                logger.LogError("{Symbol} --max-edge must be 0 (unbounded) or >= 64 (encoder minimum).", UiSymbols.Error);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(app) && window is null)
            {
                UiErrors.MissingApp(logger, json);
                return 1;
            }

            // Linked token so either Ctrl+C OR the stdin monitor can cancel and finalize the MP4.
            // Use explicit dispose (not using var) so we can set the stop flag BEFORE disposal,
            // preventing the monitor's cancel callback from racing a disposed CTS (H1 fix).
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stdinMonitorStopped = false;
            try
            {
                // Resolve output path inside error handling so path errors produce structured output.
                string filePath;
                try
                {
                    filePath = Path.GetFullPath(output ?? $"recording-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
                    var dir = Path.GetDirectoryName(filePath);
                    if (dir is not null)
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                catch (Exception pathEx)
                {
                    UiJsonError.Emit(json, UiJsonError.CodeInvalidArguments, $"Invalid output path: {pathEx.Message}");
                    logger.LogError("{Symbol} Invalid output path: {Message}", UiSymbols.Error, pathEx.Message);
                    return 1;
                }

                var session = await sessionService.ResolveSessionAsync(app, window, cancellationToken);

                // Compute isStdinRedirected once so both the status message (L1) and the
                // stdin monitor use the same value without re-evaluating the override.
                var isStdinRedirected = s_isInputRedirectedOverride?.Invoke() ?? Console.IsInputRedirected;

                if (!json && !quiet)
                {
                    // L1: interactive callers (non-redirected stdin) use Ctrl+C only; piped/redirected
                    // callers can also stop via newline/EOF on stdin.
                    var until = durationSec > 0
                        ? $"{durationSec}s"
                        : isStdinRedirected ? "Ctrl+C, newline/EOF on stdin" : "Ctrl+C";
                    ansiConsole.MarkupLine($"[grey]Recording \"{Markup.Escape(session.WindowTitle ?? "")}\" (PID {session.ProcessId}) to {Markup.Escape(filePath)} — until {until}, {fps} fps…[/]");
                }

                // Readiness gate: completed by OnRecordingStarted after the first frame is encoded.
                // The stdin monitor waits on this task before applying a stop so the encoder always
                // exists when Cancel() is called (prevents the round-2 internal_error race).
                var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                // Start the stdin monitor BEFORE recording so a pre-buffered stop signal (e.g. an
                // empty pipe that immediately delivers EOF) is caught immediately and then latched
                // until the encoder is ready. Do NOT start for interactive consoles — humans use Ctrl+C.
                if (isStdinRedirected)
                {
                    var stdinReader = s_stdinOverride ?? Console.In;
                    StdinStopMonitor.Start(stdinReader, readyTcs.Task, () =>
                    {
                        // H1 defense in depth: check the stop flag first (set before linkedCts
                        // is disposed), then catch ObjectDisposedException as a belt-and-suspenders
                        // guard against a very narrow race between the flag check and disposal.
                        if (!_stdinMonitorStopped)
                        {
                            try { linkedCts.Cancel(); }
                            catch (ObjectDisposedException) { }
                        }
                    });
                }

                // Readiness callback: invoked by RecordAsync after the encoder is initialized and the
                // first frame has been captured — i.e., recording is genuinely live.
                void OnRecordingStarted()
                {
                    // Signal readiness so the stdin monitor can now apply any latched stop.
                    readyTcs.TrySetResult();

                    if (json)
                    {
                        // Emit a structured liveness event to stderr so programmatic callers know
                        // the capture loop is live before the final result JSON arrives on stdout.
                        // Written via the invocation error writer (= Console.Error in production,
                        // = ConsoleStdErr in tests) so test captures work without Console.SetError.
                        var startedEvent = new UiRecordStartedEvent
                        {
                            Path = filePath,
                            Fps = fps,
                            DurationSec = durationSec,
                        };
                        parseResult.InvocationConfiguration.Error.WriteLine(
                            JsonSerializer.Serialize(startedEvent, UiJsonContext.Default.UiRecordStartedEvent));
                    }
                }

                var options = new RecordOptions
                {
                    OutputPath = filePath,
                    DurationSec = durationSec,
                    Fps = fps,
                    MaxEdge = maxEdge,
                    CaptureScreen = captureScreen,
                };

                var result = await uiAutomation.RecordAsync(session, selector, options, linkedCts.Token, OnRecordingStarted);

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
            catch (UiAmbiguousSelectorException ambiguousEx)
            {
                UiErrors.AmbiguousSelector(logger, ambiguousEx.Message, json);
                return 1;
            }
            catch (UiElementOffscreenException offscreenEx)
            {
                const string offscreenMsg = "Element is entirely offscreen / has no visible area to capture; nothing to record. Bring the window/element into view or pass a different selector.";
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, offscreenMsg);
                UiJsonError.Emit(json, UiJsonError.CodeElementNotFound, offscreenMsg, offscreenEx.Selector);
                return 1;
            }
            catch (UiElementNotFoundException notFoundEx)
            {
                UiErrors.ElementNotFound(logger, notFoundEx.Selector, json);
                return 1;
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
            finally
            {
                // H1 ordering: signal the stop flag BEFORE disposing the CTS so the monitor
                // callback's flag check sees true and skips Cancel() on the disposed source.
                // The guarded try/catch in the callback is the reliable guarantee;
                // this flag+ordering eliminates the race window for typical cases.
                _stdinMonitorStopped = true;
                linkedCts.Dispose();
            }
        }
    }
}
