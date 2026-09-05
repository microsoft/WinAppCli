// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class UiRecordCommand : Command, IShortDescription
{
    internal static readonly Option<bool> FramesOption = new("--frames")
    {
        Description = "Write timestamped JPEGs, frames.ndjson, and manifest.json to <output-name>.frames. Supports 1-30 fps and max-edge 64-4096 (default 1280), with a 1 GiB frame-data cap.",
    };

    public string ShortDescription => "Record a window or element region to an MP4 (H.264) video";

    public UiRecordCommand()
        : base("record", "Record the target window (or an element's region) to an H.264 MP4 video. " +
               "By default records until Ctrl+C or redirected-stdin newline/EOF. " +
               "Use --duration-sec for a timed run, --frames for timestamped JPEG evidence, " +
               "and --capture-screen for overlays and popups.")
    {
        Arguments.Add(SharedUiOptions.SelectorArgument);
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(SharedUiOptions.DurationSecOption);
        Options.Add(SharedUiOptions.FpsOption);
        Options.Add(SharedUiOptions.MaxEdgeOption);
        Options.Add(SharedUiOptions.CaptureScreenOption);
        Options.Add(SharedUiOptions.OutputOption);
        Options.Add(FramesOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        IUiTargetResolver targetResolver,
        IUiRecordingService recordingService,
        IAnsiConsole ansiConsole,
        ILogger<UiRecordCommand> logger) : AsynchronousCommandLineAction
    {
        // Test seams: override Console.IsInputRedirected and Console.In without process-level side effects.
        internal static Func<bool>? s_isInputRedirectedOverride;
        internal static TextReader? s_stdinOverride;

        // Prevents the stdin monitor from racing disposal of its cancellation source.
        private volatile bool _stdinMonitorStopped;

        /// <summary>The console this verb reports through, shared with any derived verb.</summary>
        protected IAnsiConsole Output => ansiConsole;

        /// <summary>
        /// What is being recorded, resolved from this verb's own command line.
        /// </summary>
        /// <remarks>
        /// The one thing that differs between recording an app on this desktop and recording an
        /// execution target's whole desktop. Everything after it — option validation, output paths,
        /// cadence, frame artifacts, partial-output handling, and the JSON contract — is identical,
        /// and is shared rather than reimplemented so the two can never drift apart.
        /// </remarks>
        protected virtual Task<UiTarget> ResolveSubjectAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            return targetResolver.ResolveAsync(
                parseResult.GetValue(SharedUiOptions.AppOption),
                parseResult.GetValue(SharedUiOptions.WindowOption),
                cancellationToken);
        }

        /// <summary>Checks that the command line names something to record.</summary>
        /// <returns>False when it does not, after reporting why.</returns>
        protected virtual bool TrySelectSubject(ParseResult parseResult, bool json)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            if (!string.IsNullOrWhiteSpace(parseResult.GetValue(SharedUiOptions.AppOption)) ||
                parseResult.GetValue(SharedUiOptions.WindowOption) is not null)
            {
                return true;
            }

            UiErrors.MissingApp(logger, json);
            return false;
        }

        /// <summary>The element whose region to crop to, or null for the whole window.</summary>
        protected virtual string? ElementSelector(ParseResult parseResult)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            return parseResult.GetValue(SharedUiOptions.SelectorArgument);
        }

        /// <summary>Whether to capture the screen region rather than the window's own frames.</summary>
        protected virtual bool CaptureScreen(ParseResult parseResult)
        {
            ArgumentNullException.ThrowIfNull(parseResult);

            return parseResult.GetValue(SharedUiOptions.CaptureScreenOption);
        }

        /// <summary>How the recording is described while it runs.</summary>
        protected virtual string DescribeSubject(UiTarget uiTarget)
        {
            ArgumentNullException.ThrowIfNull(uiTarget);

            return $"\"{uiTarget.WindowTitle ?? ""}\" (PID {uiTarget.ProcessId})";
        }

        /// <summary>
        /// Whether this recording must never restore, activate, or foreground the window.
        /// </summary>
        /// <remarks>
        /// False for <c>ui record</c>, which records an app the user pointed at and is watching: a
        /// minimized window is raised, and a blank <c>PrintWindow</c> frame is recovered from the
        /// foreground, because the alternative is failing a recording the user is standing in front
        /// of. A verb that advertises taking no focus overrides this.
        /// </remarks>
        protected virtual bool NoActivation(ParseResult parseResult) => false;

        /// <summary>The execution target that produced the recording, or null for this machine.</summary>
        protected virtual ExecutionTargetScope? Scope => null;

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var selector = ElementSelector(parseResult);
            var captureScreen = CaptureScreen(parseResult);

            // Validate every option before resolving the subject, so an unusable request never
            // starts anything it would then have to abandon.
            if (UiRecordOptionValidator.Validate(parseResult, out var validated) is { } optionError)
            {
                UiJsonError.Emit(
                    json,
                    optionError.Code,
                    optionError.Message,
                    errorOut: parseResult.InvocationConfiguration.Error,
                    recoveryHint: optionError.RecoveryHint);
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, optionError.Message);
                return 1;
            }

            var (filePath, framesDirectory, maxEdge, durationSec, fps) = validated!;

            if (!TrySelectSubject(parseResult, json))
            {
                return 1;
            }

            // Set _stdinMonitorStopped before disposing this source.
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stdinMonitorStopped = false;
            try
            {
                try
                {
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

                var uiTarget = await ResolveSubjectAsync(parseResult, cancellationToken);

                var isStdinRedirected = s_isInputRedirectedOverride?.Invoke() ?? Console.IsInputRedirected;

                if (!json && !quiet)
                {
                    var until = durationSec > 0
                        ? $"{durationSec}s"
                        : isStdinRedirected ? "Ctrl+C, newline/EOF on stdin" : "Ctrl+C";
                    var destinations = framesDirectory is null
                        ? filePath
                        : $"{filePath}; frame artifacts: {framesDirectory}";
                    ansiConsole.MarkupLine($"[grey]Recording {Markup.Escape(DescribeSubject(uiTarget))} to {Markup.Escape(destinations)} — until {until}, {fps} fps…[/]");
                }

                // Stops received before the first frame wait for encoder readiness.
                var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                // Only unbounded recordings use redirected stdin as a stop signal.
                if (isStdinRedirected && durationSec == 0)
                {
                    var stdinReader = s_stdinOverride ?? Console.In;
                    StdinStopMonitor.Start(stdinReader, readyTcs.Task, () => CancelFromStdinMonitor(linkedCts));
                }

                void OnRecordingStarted(bool frameArtifactsActive)
                {
                    readyTcs.TrySetResult();

                    if (json)
                    {
                        var startedEvent = new UiRecordStartedEvent
                        {
                            Path = filePath,
                            Fps = fps,
                            DurationSec = durationSec,
                            FramesDirectory = frameArtifactsActive ? framesDirectory : null,
                            FramesManifest = frameArtifactsActive ? Path.Join(framesDirectory!, "manifest.json") : null,
                            FramesIndex = frameArtifactsActive ? Path.Join(framesDirectory!, "frames.ndjson") : null,
                        };
                        parseResult.InvocationConfiguration.Error.WriteLine(
                            JsonSerializer.Serialize(startedEvent, UiJsonLineContext.Default.UiRecordStartedEvent));
                    }
                }

                var options = new RecordOptions
                {
                    OutputPath = filePath,
                    DurationSec = durationSec,
                    Fps = fps,
                    MaxEdge = maxEdge,
                    CaptureScreen = captureScreen,
                    FramesDirectory = framesDirectory,
                    NoActivation = NoActivation(parseResult),
                };

                var result = await recordingService.RecordAsync(uiTarget, selector, options, linkedCts.Token, OnRecordingStarted);

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
                        ElapsedMs = result.ElapsedMs,
                        AchievedFps = result.AchievedFps,
                        CadenceRatio = result.CadenceRatio,
                        StopReason = result.StopReason,
                        FrameArtifacts = result.FrameArtifacts,
                        Warnings = result.Warnings,
                        ExecutionTarget = Scope,
                    };
                    ansiConsole.Profile.Out.Writer.WriteLine(
                        JsonSerializer.Serialize(payload, UiJsonContext.Default.UiRecordResult));
                    return 0;
                }
                logger.LogInformation(
                    "Recorded {Frames} frames ({Width}x{Height}, h264) to {Path} ({Size}KB)",
                    result.Frames, result.Width, result.Height, filePath, result.FileSize / 1024);
                foreach (var warning in result.Warnings ?? [])
                {
                    logger.LogWarning("{Symbol} {Warning}", UiSymbols.Warning, warning);
                }
                return 0;
            }
            catch (RecordPartialOutputException partialEx)
            {
                UiJsonError.Emit(
                    json,
                    UiJsonError.CodePartialOutput,
                    partialEx.Message,
                    details: partialEx.InnerException?.GetType().Name,
                    errorOut: parseResult.InvocationConfiguration.Error,
                    recoveryHint: partialEx.RecoveryHint,
                    partialOutput: new UiPartialOutputInfo
                    {
                        VideoPath = partialEx.VideoPath,
                        FramesDirectory = partialEx.FramesDirectory,
                    });
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, partialEx.Message);
                if (!json)
                {
                    if (partialEx.VideoPath is not null)
                    {
                        logger.LogError("{Symbol} Preserved MP4: {Path}", UiSymbols.Error, partialEx.VideoPath);
                    }
                    if (partialEx.FramesDirectory is not null)
                    {
                        logger.LogError(
                            "{Symbol} Preserved frame artifacts: {Path}",
                            UiSymbols.Error,
                            partialEx.FramesDirectory);
                    }
                    logger.LogError("{Symbol} Recovery: {RecoveryHint}", UiSymbols.Error, partialEx.RecoveryHint);
                }
                return 1;
            }
            catch (RecordFrameOutputException frameEx)
            {
                UiJsonError.Emit(
                    json,
                    UiJsonError.CodeFrameOutputFailed,
                    frameEx.Message,
                    details: frameEx.InnerException?.GetType().Name,
                    errorOut: parseResult.InvocationConfiguration.Error,
                    recoveryHint: frameEx.RecoveryHint);
                logger.LogError("{Symbol} {Message}", UiSymbols.Error, frameEx.Message);
                if (!json)
                {
                    logger.LogError("{Symbol} Recovery: {RecoveryHint}", UiSymbols.Error, frameEx.RecoveryHint);
                }
                return 1;
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
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                // In-loop cancellation returns a finalized recording instead.
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
                _stdinMonitorStopped = true;
                linkedCts.Dispose();
            }
        }

        internal void CancelFromStdinMonitor(CancellationTokenSource linkedCts)
        {
            if (!_stdinMonitorStopped)
            {
                try { linkedCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }

        internal static string GetFramesDirectory(string outputPath)
        {
            var framesDirectory = Path.ChangeExtension(outputPath, ".frames");
            if (string.Equals(framesDirectory, outputPath, StringComparison.OrdinalIgnoreCase))
            {
                framesDirectory += ".frames";
            }
            return framesDirectory;
        }
    }
}
