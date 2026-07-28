// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Video recording: captures window/element frames at a fixed cadence and encodes
/// them incrementally to an H.264 MP4 via Media Foundation (<see cref="Mp4SinkWriterEncoder"/>).
/// </summary>
internal sealed partial class UiAutomationService
{
    /// <remarks>
    /// Coverage ceiling (issue #630): deterministic tests cover the frame-loop orchestration through
    /// WGC/screen/PrintWindow seams. The remaining lines in this method are native window-state arms
    /// (minimized/zero-size HWNDs), popup retargeting against real top-level HWND ownership, WGC init
    /// fault arms, and cancellation timing races that require mutating real desktop windows or native
    /// WGC failures and are not safe to trigger on the shared coverage host.
    /// </remarks>
    public async Task<RecordCaptureResult> RecordAsync(UiSessionInfo session, string? elementId, RecordOptions options, CancellationToken ct, Action? onRecordingStarted = null)
    {
        _logger.LogDebug("Recording process {Pid} (duration={Dur}s, fps={Fps}, maxEdge={MaxEdge}, captureScreen={Screen})",
            session.ProcessId, options.DurationSec, options.Fps, options.MaxEdge, options.CaptureScreen);

        var root = GetRootElement(session);
        if (root is null)
        {
            throw new InvalidOperationException($"No UIA window found for {session.ProcessName} (PID {session.ProcessId}).");
        }

        var rootName = SafeGetBstr(() => root.get_CurrentName());
        if (rootName is not null)
        {
            session.WindowTitle = rootName;
        }

        var hwnd = root.get_CurrentNativeWindowHandle();
        if (hwnd.IsNull && session.WindowHandle != 0)
        {
            hwnd = new HWND((nint)session.WindowHandle);
        }
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException($"No native window handle for {session.ProcessName}. Is the window visible?");
        }

        if (Windows.Win32.PInvoke.IsIconic(hwnd))
        {
            Windows.Win32.PInvoke.ShowWindow(hwnd, Windows.Win32.UI.WindowsAndMessaging.SHOW_WINDOW_CMD.SW_RESTORE);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        // Bring to foreground for screen-DC capture.
        if (options.CaptureScreen)
        {
            Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        Windows.Win32.PInvoke.GetWindowRect(hwnd, out var rect);
        if (rect.right - rect.left <= 0 || rect.bottom - rect.top <= 0)
        {
            throw new InvalidOperationException("Window has zero size. Is it minimized?");
        }

        var useScreen = options.CaptureScreen;
        var useWgc = !useScreen && WgcCapture.IsSupported();

        WgcCapture.IFrameGrabber? grabber = null;
        IRecordFrameSink? frameSink = null;
        var mode = useScreen ? "screen" : (useWgc ? "wgc" : "printwindow");
        var captureHwnd = (long)(nint)hwnd;

        try
        {
            int srcWidth;
            int srcHeight;
            int captureOriginLeft;
            int captureOriginTop;

            if (useWgc)
            {
                try
                {
                    grabber = WgcCapture.s_startGrabber(hwnd, _logger, options.Fps);
                    if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("Timed out waiting for the first captured frame.");
                    }
                    var first = grabber.TryGetLatest()!.Value;
                    srcWidth = first.Width;
                    srcHeight = first.Height;
                    var visibleRect = GetVisibleWindowRect(hwnd, rect);
                    captureOriginLeft = visibleRect.left;
                    captureOriginTop = visibleRect.top;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "WGC recorder init failed; throwing (no silent screen fallback without --capture-screen)");
                    grabber?.Dispose();
                    grabber = null;
                    // H1 privacy guard: do not silently fall back to screen capture unless the user
                    // explicitly consented by passing --capture-screen. Screen-DC capture includes any
                    // window overlapping the target on screen, which can leak unrelated content.
                    // EnsureWgcFallbackConsented always throws here because WGC only runs when
                    // --capture-screen is NOT set, so captureScreenRequested is always false.
                    EnsureWgcFallbackConsented(ex, options.CaptureScreen, _logger);
                    useScreen = true;
                    useWgc = false;
                    srcWidth = rect.right - rect.left;
                    srcHeight = rect.bottom - rect.top;
                    captureOriginLeft = rect.left;
                    captureOriginTop = rect.top;
                }
            }
            else
            {
                srcWidth = rect.right - rect.left;
                srcHeight = rect.bottom - rect.top;
                captureOriginLeft = rect.left;
                captureOriginTop = rect.top;
            }

            // H2: For whole-window WGC capture, derive crop dims from EACH frame (not just the first).
            // Element-crop captures use the fixed cropW/cropH computed from the element bounds below.
            var isWholeWindowWgc = useWgc && string.IsNullOrEmpty(elementId);

            // Determine the crop rectangle (in capture-space) for an element selector.
            var cropX = 0;
            var cropY = 0;
            var cropW = srcWidth;
            var cropH = srcHeight;
            if (!string.IsNullOrEmpty(elementId))
            {
                // Use the canonical single-element resolution path (same as ui click/hover) so that:
                //   - An ambiguous plain-text selector (multiple matches) → structured error with slug
                //     suggestions, not a silent first-match recording.
                //   - Element not found → UiElementNotFoundException (element_not_found error code).
                //   - Popup/child-window searching and slug validation are preserved.
                // No selector (elementId == null) → whole-window recording (by design, unchanged).
                var selectorExpr = _selectorService.Parse(elementId);
                var selectorElement = await FindSingleElementAsync(session, selectorExpr, ct).ConfigureAwait(false);
                if (selectorElement is null)
                {
                    throw new UiElementNotFoundException(elementId);
                }

                // H1 fix: When the selector resolves to a popup or owned window whose HWND differs
                // from the session's main window, retarget the capture surface to that window.
                // Using the main window's rect to clamp an element in a different window produces a
                // truncated sliver or wrong pixels; the fix switches both the capture origin and
                // (for WGC/PrintWindow) the capture HWND to the element's actual top-level window.
                var captureTargetHwnd = ResolvePopupCaptureHwnd(
                    selectorElement.WindowHandle, (nint)hwnd,
                    ref captureOriginLeft, ref captureOriginTop,
                    ref srcWidth, ref srcHeight);

                // H2 fix (r12): the UIA main-tree resolution path stamps WindowHandle =
                // session.WindowHandle on every resolved element, so a windowed popup/dialog
                // reachable via the main tree has WindowHandle == sessionHwnd and bypasses the
                // stamped-handle retarget above. Derive the element's TRUE top-level window from
                // its UIA native-window ancestor (authoritative — it follows the real UIA parent
                // chain, unlike z-order hit testing which can pick an unrelated same-process
                // window that merely overlaps the element's center) and retarget to it.
                if (captureTargetHwnd == (nint)hwnd)
                {
                    var derivedHwnd = DeriveElementCaptureHwnd(
                        (nint)hwnd,
                        ref captureOriginLeft, ref captureOriginTop,
                        ref srcWidth, ref srcHeight,
                        getElementTopLevelHwnd: () => ResolveElementTopLevelHwnd(session, selectorElement));
                    if (derivedHwnd != (nint)hwnd)
                    {
                        captureTargetHwnd = derivedHwnd;
                        _logger.LogDebug(
                            "Selector '{Sel}' element resolved to top-level window HWND 0x{Hwnd:X} via UIA ancestor walk; retargeting capture",
                            elementId, captureTargetHwnd);
                    }
                }

                if (captureTargetHwnd != (nint)hwnd)
                {
                    captureHwnd = (long)captureTargetHwnd;
                    var popupHwnd = new Windows.Win32.Foundation.HWND(captureTargetHwnd);
                    _logger.LogDebug(
                        "Selector '{Sel}' resolved to popup window HWND 0x{Hwnd:X}; retargeting capture",
                        elementId, captureTargetHwnd);

                    if (useWgc)
                    {
                        // Restart the WGC grabber on the element's owning top-level window.
                        grabber?.Dispose();
                        grabber = null;
                        try
                        {
                            grabber = WgcCapture.s_startGrabber(popupHwnd, _logger, options.Fps);
                            if (!await grabber.WaitForFirstFrameAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                            {
                                throw new InvalidOperationException("Timed out waiting for the first captured frame.");
                            }
                            var retargetFirst = grabber.TryGetLatest()!.Value;
                            srcWidth = retargetFirst.Width;
                            srcHeight = retargetFirst.Height;
                            Windows.Win32.PInvoke.GetWindowRect(popupHwnd, out var popupWinRect);
                            var visiblePopupRect = GetVisibleWindowRect(popupHwnd, popupWinRect);
                            captureOriginLeft = visiblePopupRect.left;
                            captureOriginTop = visiblePopupRect.top;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "WGC retarget to popup HWND 0x{Hwnd:X} failed", captureTargetHwnd);
                            grabber?.Dispose();
                            grabber = null;
                            // If the user did not pass --capture-screen, EnsureWgcFallbackConsented throws
                            // (same privacy guard as for main-window WGC init failure).
                            // captureOriginLeft/Top/srcWidth/srcHeight already reflect the popup window rect
                            // (set by ResolvePopupCaptureHwnd), so any screen-DC fallback uses the right region.
                            EnsureWgcFallbackConsented(ex, options.CaptureScreen, _logger);
                            useScreen = true;
                            useWgc = false;
                            mode = "screen";
                        }
                    }
                    else if (!useScreen)
                    {
                        // PrintWindow path: retarget the HWND to the popup window.
                        // captureOriginLeft/Top/srcWidth/srcHeight already updated by ResolvePopupCaptureHwnd.
                        hwnd = popupHwnd;
                    }
                    // For useScreen: captureOriginLeft/Top/srcWidth/srcHeight already updated; no further action.
                }

                // Guard: reject elements with no positive-area intersection with the capture surface.
                // A zero/negative intersection would be clamped to a 1-pixel edge sliver, then
                // inflated by encoder-min padding → garbage pixels recorded at exit 0 (M1 fix).
                // Legitimately small elements that DO intersect fall through to the clamp + padding below.
                if (IsElementOffscreen(selectorElement.X, selectorElement.Y,
                    selectorElement.Width, selectorElement.Height,
                    captureOriginLeft, captureOriginTop, srcWidth, srcHeight))
                {
                    throw new UiElementOffscreenException(elementId!);
                }

                // Compute crop: element screen coords relative to the (possibly retargeted) capture origin.
                cropX = Math.Clamp((int)selectorElement.X - captureOriginLeft, 0, Math.Max(0, srcWidth - 1));
                cropY = Math.Clamp((int)selectorElement.Y - captureOriginTop, 0, Math.Max(0, srcHeight - 1));
                cropW = Math.Clamp((int)selectorElement.Width, 1, srcWidth - cropX);
                cropH = Math.Clamp((int)selectorElement.Height, 1, srcHeight - cropY);
            }

            var (encoderW, encoderH, displayW, displayH) = ComputeTargetSize(cropW, cropH, options.MaxEdge);
            var bitrate = (uint)Math.Clamp((long)encoderW * encoderH * options.Fps / 8, 1_000_000, 24_000_000);
            var encoderFactory = options.FramesDirectory is null
                ? Mp4SinkWriterEncoder.s_create
                : Mp4SinkWriterEncoder.s_createNoClobber;
            using var encoder = encoderFactory(options.OutputPath, encoderW, encoderH, options.Fps, bitrate);

            var frameDurationHns = 10_000_000L / options.Fps;
            var totalFrames = options.DurationSec > 0 ? (long)options.DurationSec * options.Fps : (long?)null;
            var stopwatch = Stopwatch.StartNew();
            var startedUtc = DateTimeOffset.UtcNow;
            var frameIndex = 0;
            var frameSamplesAccepted = 0;
            long lastEncodedVersion = -1;
            var startedSignaled = false;
            var targetClosed = false;
            var nextProgressMs = 5_000L;
            Exception? frameSinkFailure = null;
            RecordFrameArtifactResult? frameArtifacts = null;

            if (options.FramesDirectory is not null)
            {
                var (contentX, contentY, contentW, contentH) = ComputeFittedContentRect(
                    cropW, cropH, encoderW, encoderH, displayW, displayH);
                try
                {
                    frameSink = RecordFrameBundleWriter.s_create(new RecordFrameBundleConfiguration
                    {
                        FinalDirectory = options.FramesDirectory,
                        VideoPath = options.OutputPath,
                        RecordingId = Guid.NewGuid().ToString("N"),
                        StartedUtc = startedUtc,
                        Width = encoderW,
                        Height = encoderH,
                        ContentRect = new RecordFrameRectManifest
                        {
                            X = contentX,
                            Y = contentY,
                            Width = contentW,
                            Height = contentH,
                        },
                        Requested = new RecordFrameRequestManifest
                        {
                            DurationSec = options.DurationSec,
                            Fps = options.Fps,
                            MaxEdge = options.MaxEdge,
                            Selector = options.Selector,
                            CaptureScreen = options.CaptureScreen,
                        },
                        Source = new RecordFrameSourceManifest
                        {
                            ProcessId = session.ProcessId,
                            ProcessName = session.ProcessName,
                            WindowTitle = session.WindowTitle,
                            SessionHwnd = session.WindowHandle,
                            CaptureHwnd = captureHwnd,
                            CaptureMode = mode,
                        },
                        Crop = new RecordFrameCropManifest
                        {
                            Kind = string.IsNullOrEmpty(elementId) ? "window" : "element",
                            Rect = new RecordFrameRectManifest
                            {
                                X = cropX,
                                Y = cropY,
                                Width = cropW,
                                Height = cropH,
                            },
                        },
                        Logger = _logger,
                    });
                }
                catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
                {
                    frameSinkFailure = ex;
                    _logger.LogError(ex, "Could not initialize frame artifact output");
                }
            }

            async ValueTask CommitFrameAsync(
                byte[] processedFrame,
                long sourceVersion,
                int sourceWidth,
                int sourceHeight)
            {
                var sampleIndex = frameIndex;
                var elapsedMs = Math.Max(0, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));

                if (frameSink is not null)
                {
                    try
                    {
                        var (_, _, frameCropW, frameCropH) = isWholeWindowWgc
                            ? (0, 0, sourceWidth, sourceHeight)
                            : ClampCropRect(
                                cropX,
                                cropY,
                                cropW,
                                cropH,
                                sourceWidth,
                                sourceHeight);
                        var (contentX, contentY, contentW, contentH) = ComputeFittedContentRect(
                            frameCropW,
                            frameCropH,
                            encoderW,
                            encoderH,
                            displayW,
                            displayH);
                        await frameSink.WriteAsync(processedFrame, new RecordFrameSample
                        {
                            SampleIndex = sampleIndex,
                            ElapsedMs = elapsedMs,
                            MediaTimeMs = sampleIndex * 1000.0 / options.Fps,
                            SourceVersion = sourceVersion >= 0 ? sourceVersion : null,
                            SourceWidth = sourceWidth,
                            SourceHeight = sourceHeight,
                            ContentRect = new RecordFrameRectManifest
                            {
                                X = contentX,
                                Y = contentY,
                                Width = contentW,
                                Height = contentH,
                            },
                        }, CancellationToken.None).ConfigureAwait(false);
                        frameSamplesAccepted++;
                    }
                    catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
                    {
                        frameSinkFailure ??= ex;
                        _logger.LogError(ex, "Frame artifact output failed; continuing MP4 recording");
                        try
                        {
                            await frameSink.AbortAsync().ConfigureAwait(false);
                        }
                        catch (Exception cleanupEx) when (IsRecoverableFrameOutputFailure(cleanupEx))
                        {
                            frameSinkFailure = new AggregateException(frameSinkFailure, cleanupEx);
                            _logger.LogDebug(cleanupEx, "Frame artifact cleanup also failed");
                        }
                        await frameSink.DisposeAsync().ConfigureAwait(false);
                        frameSink = null;
                    }
                }

                encoder.WriteFrame(
                    processedFrame,
                    sampleIndex * frameDurationHns,
                    frameDurationHns);
                frameIndex++;
                if (!startedSignaled)
                {
                    startedSignaled = true;
                    onRecordingStarted?.Invoke();
                }

                if (options.OnProgress is not null && elapsedMs >= nextProgressMs)
                {
                    options.OnProgress(new RecordProgress
                    {
                        ElapsedMs = elapsedMs,
                        Samples = frameIndex,
                        Images = frameSink?.ImageCount ?? 0,
                        AchievedFps = elapsedMs > 0 ? frameIndex * 1000.0 / elapsedMs : 0,
                    });
                    nextProgressMs = elapsedMs + 5_000;
                }

            }

            Exception? mp4Failure = null;
            long captureElapsedMs;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (totalFrames.HasValue && frameIndex >= totalFrames.Value)
                    {
                        break;
                    }

                    if (totalFrames.HasValue && stopwatch.Elapsed.TotalSeconds >= options.DurationSec)
                    {
                        break;
                    }

                    if (useWgc && grabber!.IsClosed)
                    {
                        var closedLatest = grabber.TryGetLatest();
                        if (closedLatest is not null)
                        {
                            var (closedSrc, closedSw, closedSh, closedVersion) = closedLatest.Value;
                            if (ShouldEncodeClosedDrainFrame(closedVersion, lastEncodedVersion))
                            {
                                var closedCropW = isWholeWindowWgc ? closedSw : cropW;
                                var closedCropH = isWholeWindowWgc ? closedSh : cropH;
                                var closedFrame = ProcessFrame(
                                    closedSrc, closedSw, closedSh,
                                    cropX, cropY, closedCropW, closedCropH,
                                    encoderW, encoderH, displayW, displayH);
                                await CommitFrameAsync(
                                    closedFrame, closedVersion, closedSw, closedSh).ConfigureAwait(false);
                            }
                        }
                        targetClosed = true;
                        _logger.LogDebug(
                            "WGC capture item closed mid-recording; finalizing {Frames} frames captured so far",
                            frameIndex);
                        break;
                    }

                    byte[] frame;
                    long sourceVersion = -1;
                    var sourceWidth = srcWidth;
                    var sourceHeight = srcHeight;
                    if (useWgc)
                    {
                        var latest = grabber!.TryGetLatest();
                        if (latest is null)
                        {
                            await Task.Delay(5, ct).ConfigureAwait(false);
                            continue;
                        }
                        var (source, sw, sh, version) = latest.Value;
                        sourceVersion = version;
                        sourceWidth = sw;
                        sourceHeight = sh;
                        frame = ProcessFrame(
                            source, sw, sh, cropX, cropY,
                            isWholeWindowWgc ? sw : cropW,
                            isWholeWindowWgc ? sh : cropH,
                            encoderW, encoderH, displayW, displayH);
                    }
                    else if (useScreen)
                    {
                        frame = CaptureScreenFrame(
                            captureOriginLeft + cropX,
                            captureOriginTop + cropY,
                            cropW,
                            cropH,
                            encoderW,
                            encoderH,
                            displayW,
                            displayH);
                    }
                    else
                    {
                        var source = CaptureFromWindowWithBlankRetry(hwnd, srcWidth, srcHeight);
                        frame = ProcessFrame(
                            source, srcWidth, srcHeight,
                            cropX, cropY, cropW, cropH,
                            encoderW, encoderH, displayW, displayH);
                    }

                    await CommitFrameAsync(
                        frame, sourceVersion, sourceWidth, sourceHeight).ConfigureAwait(false);
                    if (useWgc)
                    {
                        lastEncodedVersion = sourceVersion;
                    }

                    var targetMs = frameIndex * 1000.0 / options.Fps;
                    var delayMs = targetMs - stopwatch.Elapsed.TotalMilliseconds;
                    if (delayMs > 1)
                    {
                        try
                        {
                            await Task.Delay((int)delayMs, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }

                captureElapsedMs = Math.Max(1, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                captureElapsedMs = Math.Max(1, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                captureElapsedMs = Math.Max(1, (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
                mp4Failure = ex;
            }

            if (frameIndex == 0 && ct.IsCancellationRequested)
            {
                if (frameSink is not null)
                {
                    await frameSink.AbortAsync().ConfigureAwait(false);
                    await frameSink.DisposeAsync().ConfigureAwait(false);
                    frameSink = null;
                }
                ct.ThrowIfCancellationRequested();
            }

            if (mp4Failure is null)
            {
                try
                {
                    encoder.Complete();
                }
                catch (Exception ex)
                {
                    mp4Failure = ex;
                }
            }

            var elapsedMs = captureElapsedMs;
            var achievedFps = frameIndex * 1000.0 / elapsedMs;
            var cadenceRatio = achievedFps / options.Fps;
            var frameAchievedFps = frameSamplesAccepted * 1000.0 / elapsedMs;
            var frameCadenceRatio = frameAchievedFps / options.Fps;
            var stopReason = mp4Failure is not null
                ? "mp4_failed"
                : targetClosed
                    ? "target_closed"
                    : ct.IsCancellationRequested ? "cancelled" : "duration_elapsed";

            if (mp4Failure is not null)
            {
                if (frameSink is not null && frameSamplesAccepted > 0)
                {
                    try
                    {
                        frameArtifacts = await frameSink.CompleteAsync(new RecordFrameCompletion
                        {
                            Status = "partial",
                            StopReason = stopReason,
                            ElapsedMs = elapsedMs,
                            AchievedFps = frameAchievedFps,
                            CadenceRatio = frameCadenceRatio,
                            VideoStatus = "failed",
                            VideoFrameCount = frameIndex,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception frameEx) when (frameEx is not OutOfMemoryException
                        and not StackOverflowException
                        and not AccessViolationException)
                    {
                        frameSinkFailure ??= frameEx;
                        _logger.LogError(frameEx, "Could not preserve partial frame artifacts after MP4 failure");
                        try
                        {
                            await frameSink.AbortAsync().ConfigureAwait(false);
                        }
                        catch (Exception cleanupEx) when (IsRecoverableFrameOutputFailure(cleanupEx))
                        {
                            frameSinkFailure = new AggregateException(frameSinkFailure, cleanupEx);
                            _logger.LogDebug(cleanupEx, "Partial frame artifact cleanup also failed");
                        }
                    }
                }
                else if (frameSink is not null)
                {
                    await frameSink.AbortAsync().ConfigureAwait(false);
                }

                if (frameSink is not null)
                {
                    await frameSink.DisposeAsync().ConfigureAwait(false);
                    frameSink = null;
                }

                if (frameArtifacts is not null)
                {
                    throw new RecordPartialOutputException(
                        "MP4 recording failed, but partial frame artifacts were preserved.",
                        videoPath: null,
                        framesDirectory: frameArtifacts.Directory,
                        "Inspect the partial frame bundle, then retry the recording to produce the MP4.",
                        mp4Failure);
                }

                if (frameSinkFailure is not null)
                {
                    throw new RecordFrameOutputException(
                        "Frame artifact output failed and no recording artifact could be preserved.",
                        frameSinkFailure);
                }

                ExceptionDispatchInfo.Capture(mp4Failure).Throw();
            }

            var fileSize = new FileInfo(options.OutputPath).Length;
            if (frameSink is not null)
            {
                var activeFrameSink = frameSink;
                try
                {
                    frameArtifacts = await activeFrameSink.CompleteAsync(new RecordFrameCompletion
                    {
                        Status = "complete",
                        StopReason = stopReason,
                        ElapsedMs = elapsedMs,
                        AchievedFps = achievedFps,
                        CadenceRatio = cadenceRatio,
                        VideoStatus = "complete",
                        VideoFrameCount = frameIndex,
                        VideoFileSize = fileSize,
                    }).ConfigureAwait(false);
                    await activeFrameSink.DisposeAsync().ConfigureAwait(false);
                    frameSink = null;
                }
                catch (Exception ex) when (IsRecoverableFrameOutputFailure(ex))
                {
                    frameSinkFailure ??= ex;
                    _logger.LogError(ex, "Could not finalize frame artifact output");
                    try
                    {
                        await activeFrameSink.AbortAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupEx) when (IsRecoverableFrameOutputFailure(cleanupEx))
                    {
                        frameSinkFailure = new AggregateException(frameSinkFailure, cleanupEx);
                        _logger.LogDebug(cleanupEx, "Final frame artifact cleanup also failed");
                    }
                    await activeFrameSink.DisposeAsync().ConfigureAwait(false);
                    frameSink = null;
                }
            }

            if (frameSinkFailure is not null)
            {
                throw new RecordPartialOutputException(
                    "The MP4 was recorded successfully, but frame artifact output failed.",
                    options.OutputPath,
                    framesDirectory: null,
                    "Use the MP4 as the durable recording and retry with new --output and --frames-dir paths.",
                    frameSinkFailure);
            }

            var warnings = cadenceRatio < 0.90
                ? new[] { $"Capture cadence was {cadenceRatio:P0} of the requested {options.Fps} fps." }
                : null;
            return new RecordCaptureResult
            {
                Frames = frameIndex,
                Width = encoderW,
                Height = encoderH,
                FileSize = fileSize,
                Mode = mode,
                ElapsedMs = elapsedMs,
                AchievedFps = achievedFps,
                CadenceRatio = cadenceRatio,
                StopReason = stopReason,
                FrameArtifacts = frameArtifacts,
                Warnings = warnings,
            };
        }
        finally
        {
            if (frameSink is not null)
            {
                try
                {
                    await frameSink.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException
                    or InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or COMException)
                {
                    _logger.LogDebug(ex, "Could not clean up unpublished frame artifacts");
                }
            }
            grabber?.Dispose();
        }
    }

    private static bool IsRecoverableFrameOutputFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException
            or ExternalException
            or OperationCanceledException;

    /// <summary>
    /// Called when Windows Graphics Capture fails to initialize. If the user did NOT explicitly
    /// pass <c>--capture-screen</c>, this throws an <see cref="InvalidOperationException"/> with a
    /// clear, actionable message — screen-DC capture is NOT a silent privacy-safe fallback.
    /// If <paramref name="captureScreenRequested"/> is <see langword="true"/>, a warning is emitted
    /// and the caller proceeds with the screen-DC path that the user consented to.
    /// </summary>
    /// <param name="inner">The original WGC init exception (used as inner exception).</param>
    /// <param name="captureScreenRequested">Whether the user passed <c>--capture-screen</c>.</param>
    /// <param name="logger">Logger for the consent-granted warning.</param>
    internal static void EnsureWgcFallbackConsented(Exception inner, bool captureScreenRequested, ILogger logger)
    {
        if (!captureScreenRequested)
        {
            throw new InvalidOperationException(
                "Windows Graphics Capture is unavailable on this system/session. " +
                "Re-run with --capture-screen to record via screen capture " +
                "(note: this may include other windows overlapping the target).", inner);
        }
        logger.LogWarning(
            "WGC init failed; falling back to screen-DC capture as requested by --capture-screen. " +
            "Overlapping windows may be captured.");
    }

    internal static bool ShouldEncodeClosedDrainFrame(long cachedVersion, long lastEncodedVersion) => cachedVersion != lastEncodedVersion;

    // Minimum dimensions accepted by the Windows MF H.264 encoder.
    // Empirically, the encoder rejects frames narrower or shorter than 64 pixels with
    // COM error 0xC00D36B4 ("media type is invalid"). Frames smaller than this are
    // centered on a black letterbox canvas padded to the minimum, preserving aspect ratio.
    private const int MfH264MinWidth = 64;
    private const int MfH264MinHeight = 64;

    /// <summary>
    /// Computes the encoder output size and display (content) size for the given crop dimensions.
    /// The encoder size is at least <see cref="MfH264MinWidth"/>×<see cref="MfH264MinHeight"/> to
    /// satisfy the Windows MF H.264 encoder's minimum requirements; if the content is smaller it is
    /// letterboxed (centered on a black background) rather than stretched.
    /// Both dimensions are always even (H.264 requirement).
    /// </summary>
    internal static (int EncoderW, int EncoderH, int DisplayW, int DisplayH) ComputeTargetSize(int width, int height, int maxEdge)
    {
        var scale = 1.0;
        var longest = Math.Max(width, height);
        if (maxEdge > 0 && longest > maxEdge)
        {
            // Scale so the longest DISPLAY edge does not EXCEED maxEdge.
            // EvenRound (nearest-even) can round up past the limit, so we compute the exact
            // ratio here and clamp the scaled long edge to the greatest even number ≤ maxEdge.
            scale = (double)maxEdge / longest;
        }

        // Round the short edge to nearest-even to minimise aspect-ratio distortion.
        // Round the long edge DOWN to the greatest even number ≤ maxEdge (when capped) so
        // "at most N pixels" is honoured — nearest-even could round UP past the cap.
        int displayW, displayH;
        if (maxEdge > 0 && longest > maxEdge)
        {
            var evenMaxEdge = EvenFloor(maxEdge);
            if (width >= height)
            {
                // width is the long edge — clamp it down to even ≤ maxEdge
                displayW = evenMaxEdge;
                // M9: clamp the short edge too — EvenRound can round UP past the cap for
                // near-square inputs (e.g. 100×100 at maxEdge=99: round(99)=100 > 99).
                displayH = Math.Min(EvenRound(height * scale), evenMaxEdge);
            }
            else
            {
                // height is the long edge — clamp it down to even ≤ maxEdge
                displayH = evenMaxEdge;
                // M9: clamp the short edge for the same reason.
                displayW = Math.Min(EvenRound(width * scale), evenMaxEdge);
            }
        }
        else
        {
            displayW = EvenRound(width * scale);
            displayH = EvenRound(height * scale);
        }

        // Pad up to the encoder minimum while preserving even dimensions.
        var encoderW = Math.Max(displayW, MfH264MinWidth);
        var encoderH = Math.Max(displayH, MfH264MinHeight);

        return (encoderW, encoderH, displayW, displayH);

        // Round a scaled double to the nearest even integer ≥ 2.
        static int EvenRound(double v)
            => Math.Max(2, (int)(Math.Round(v / 2.0, MidpointRounding.AwayFromZero) * 2));

        // Floor to the greatest even integer ≤ v (and ≥ 2).
        static int EvenFloor(int v)
            => Math.Max(2, v % 2 == 0 ? v : v - 1);
    }

    /// <summary>
    /// When the resolved UI element lives in a popup or owned window (a top-level HWND different
    /// from the session's main window), retargets the capture surface to that window so that the
    /// recording captures the correct pixels rather than a clamped sliver of the main frame.
    /// </summary>
    /// <param name="elementWindowHandle">
    /// The <see cref="Models.UiElement.WindowHandle"/> from the resolved element (nullable).
    /// Set to the HWND of the window that was searched when the element was found.
    /// </param>
    /// <param name="sessionHwnd">The session's main window HWND (as <see cref="nint"/>).</param>
    /// <param name="captureOriginLeft">In/out: updated to the popup window's screen-left when retargeting.</param>
    /// <param name="captureOriginTop">In/out: updated to the popup window's screen-top when retargeting.</param>
    /// <param name="srcWidth">In/out: updated to the popup window's pixel width when retargeting.</param>
    /// <param name="srcHeight">In/out: updated to the popup window's pixel height when retargeting.</param>
    /// <param name="getAncestorRoot">
    /// Optional injectable for <c>GetAncestor(hwnd, GA_ROOT)</c> — used for unit testing without Win32.
    /// Receives and returns an HWND as <see cref="nint"/> (0 = null/not found).
    /// When <see langword="null"/>, the real <c>Windows.Win32.PInvoke.GetAncestor</c> is called.
    /// </param>
    /// <param name="getWindowRect">
    /// Optional injectable for <c>GetWindowRect</c> — used for unit testing without Win32.
    /// Returns <c>(left, top, right, bottom)</c> screen coordinates of the window.
    /// When <see langword="null"/>, the real <c>Windows.Win32.PInvoke.GetWindowRect</c> is called.
    /// </param>
    /// <returns>
    /// The HWND (as <see cref="nint"/>) to use as the capture surface: the element's top-level
    /// window when a retarget was needed, or <paramref name="sessionHwnd"/> when unchanged.
    /// </returns>
    /// <remarks>
    /// Coverage ceiling (issue #630): unit tests cover the retargeting decisions through injected
    /// ancestor/rect delegates. The only remaining uncovered lines call the real GetAncestor/
    /// GetWindowRect Win32 APIs for live popup HWNDs, which require desktop window topology that is
    /// not deterministic on the shared test host.
    /// </remarks>
    internal static nint ResolvePopupCaptureHwnd(
        long? elementWindowHandle,
        nint sessionHwnd,
        ref int captureOriginLeft, ref int captureOriginTop,
        ref int srcWidth, ref int srcHeight,
        Func<nint, nint>? getAncestorRoot = null,
        Func<nint, (int left, int top, int right, int bottom)>? getWindowRect = null)
    {
        if (!elementWindowHandle.HasValue || elementWindowHandle.Value == sessionHwnd)
        {
            return sessionHwnd;
        }

        var rawElementHwnd = (nint)elementWindowHandle.Value;

        // Walk up to the true top-level window (GA_ROOT) so that child HWNDs (e.g. hosted
        // control islands) resolve to the containing top-level window for WGC/capture.
        nint elementOwnerHwnd;
        if (getAncestorRoot is not null)
        {
            var root = getAncestorRoot(rawElementHwnd);
            elementOwnerHwnd = root != 0 ? root : rawElementHwnd;
        }
        else
        {
            var rootHwnd = Windows.Win32.PInvoke.GetAncestor(
                new Windows.Win32.Foundation.HWND(rawElementHwnd),
                Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
            elementOwnerHwnd = rootHwnd.IsNull ? rawElementHwnd : (nint)rootHwnd;
        }

        // If GA_ROOT turned out to be the session window (element is a child HWND inside
        // the main window), no retarget is needed.
        if (elementOwnerHwnd == sessionHwnd)
        {
            return sessionHwnd;
        }

        // Update capture origin and dimensions to the element's owning top-level window.
        if (getWindowRect is not null)
        {
            var (l, t, r, b) = getWindowRect(elementOwnerHwnd);
            captureOriginLeft = l;
            captureOriginTop = t;
            srcWidth = Math.Max(1, r - l);
            srcHeight = Math.Max(1, b - t);
        }
        else
        {
            Windows.Win32.PInvoke.GetWindowRect(
                new Windows.Win32.Foundation.HWND(elementOwnerHwnd), out var popupRect);
            captureOriginLeft = popupRect.left;
            captureOriginTop = popupRect.top;
            srcWidth = Math.Max(1, popupRect.right - popupRect.left);
            srcHeight = Math.Max(1, popupRect.bottom - popupRect.top);
        }

        return elementOwnerHwnd;
    }

    /// <summary>
    /// Returns <see langword="true"/> when an element rect has no positive-area intersection with
    /// the capture surface. Used to reject entirely-offscreen elements before crop clamping so
    /// callers get an actionable error instead of a garbage 1-pixel-sliver recording.
    /// A legitimately small element that does intersect returns <see langword="false"/> and is
    /// allowed to fall through to the existing encoder-min padding (intended behaviour).
    /// </summary>
    internal static bool IsElementOffscreen(
        double elemX, double elemY, double elemWidth, double elemHeight,
        int captureOriginLeft, int captureOriginTop, int srcWidth, int srcHeight)
    {
        if (elemWidth <= 0 || elemHeight <= 0)
        {
            return true;
        }

        var interLeft   = Math.Max((int)elemX,                       captureOriginLeft);
        var interTop    = Math.Max((int)elemY,                       captureOriginTop);
        var interRight  = Math.Min((int)elemX + (int)elemWidth,      captureOriginLeft + srcWidth);
        var interBottom = Math.Min((int)elemY + (int)elemHeight,     captureOriginTop  + srcHeight);

        return interRight <= interLeft || interBottom <= interTop;
    }

    /// <summary>
    /// Crops and scales a captured BGRA frame to the encoder target dimensions (top-down output).
    /// When the display size is smaller than the encoder size, the content is centered on a black
    /// letterbox background rather than stretched — aspect ratio is always preserved.
    /// </summary>
    internal static byte[] ProcessFrame(
        byte[] source, int sourceWidth, int sourceHeight,
        int cropX, int cropY, int cropW, int cropH,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight)
    {
        // Fast path: whole frame at native encoder size with no letterbox needed.
        if (cropX == 0 && cropY == 0 && cropW == sourceWidth && cropH == sourceHeight
            && encoderWidth == sourceWidth && encoderHeight == sourceHeight
            && displayWidth == encoderWidth && displayHeight == encoderHeight
            && source.Length == encoderWidth * encoderHeight * 4)
        {
            return source;
        }

        // Clamp crop against the actual frame in case dimensions drifted between frames.
        (cropX, cropY, cropW, cropH) = ClampCropRect(
            cropX,
            cropY,
            cropW,
            cropH,
            sourceWidth,
            sourceHeight);

        var srcInfo = new SKImageInfo(sourceWidth, sourceHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var srcBitmap = new SKBitmap(srcInfo);
        Marshal.Copy(source, 0, srcBitmap.GetPixels(), Math.Min(source.Length, srcInfo.BytesSize));

        var dstInfo = new SKImageInfo(encoderWidth, encoderHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var dstBitmap = new SKBitmap(dstInfo);
        using (var canvas = new SKCanvas(dstBitmap))
        {
            // Black letterbox background (covers padding regions when encoderW > displayW or encoderH > displayH).
            canvas.Clear(SKColors.Black);

            var (offsetX, offsetY, fitW, fitH) = ComputeFittedContentRect(
                cropW, cropH, encoderWidth, encoderHeight, displayWidth, displayHeight);

            var srcRect = SKRect.Create(cropX, cropY, cropW, cropH);
            var dstRect = SKRect.Create(offsetX, offsetY, fitW, fitH);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = false };
            canvas.DrawBitmap(srcBitmap, srcRect, dstRect, paint);
        }

        var output = new byte[dstInfo.BytesSize];
        Marshal.Copy(dstBitmap.GetPixels(), output, 0, output.Length);
        return output;
    }

    internal static (int X, int Y, int Width, int Height) ClampCropRect(
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        int sourceWidth,
        int sourceHeight)
    {
        cropX = Math.Clamp(cropX, 0, Math.Max(0, sourceWidth - 1));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, sourceHeight - 1));
        cropW = Math.Clamp(cropW, 1, sourceWidth - cropX);
        cropH = Math.Clamp(cropH, 1, sourceHeight - cropY);
        return (cropX, cropY, cropW, cropH);
    }

    internal static (int OffsetX, int OffsetY, int FitW, int FitH) ComputeFittedContentRect(
        int cropW, int cropH, int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
    {
        var scale = Math.Min(displayWidth / (double)cropW, displayHeight / (double)cropH);
        var fitW = Math.Clamp((int)Math.Round(cropW * scale), 1, displayWidth);
        var fitH = Math.Clamp((int)Math.Round(cropH * scale), 1, displayHeight);
        var offsetX = (encoderWidth - fitW) / 2;
        var offsetY = (encoderHeight - fitH) / 2;
        return (offsetX, offsetY, fitW, fitH);
    }

    /// <summary>
    /// Resolves the element's TRUE top-level native window by re-resolving the live UIA element
    /// (<see cref="ResolveComElement"/>) and walking up the control view to the nearest ancestor
    /// that owns a native window handle, then to that handle's <c>GA_ROOT</c> top-level window.
    ///
    /// This is authoritative because it follows the actual UIA parent chain, so it is immune to
    /// the z-order pitfall of hit testing (<c>WindowFromPoint</c>), which can return an unrelated
    /// window that merely overlaps the element's on-screen center — even one in the same process.
    /// Returns 0 when the element cannot be re-resolved or has no native-window ancestor, in which
    /// case the caller leaves capture on the session window (no retarget).
    /// </summary>
    /// <remarks>
    /// Coverage ceiling (issue #630): this walks live UIA COM parents and Win32 ancestors. The safe
    /// static decision helper is unit-tested; the remaining lines require a real popup/owned HWND
    /// ancestor chain or a COM provider fault during parent walking.
    /// </remarks>
    private nint ResolveElementTopLevelHwnd(UiSessionInfo session, UiElement selectorElement)
    {
        try
        {
            var comElement = ResolveComElement(session, selectorElement);
            if (comElement is null)
            {
                return 0;
            }

            // Walk up the control view to the nearest element that owns a native HWND, then
            // resolve that handle's top-level (GA_ROOT) window. Bounded to avoid pathological loops.
            var walker = _automation.get_ControlViewWalker();
            var current = comElement;
            var maxWalk = 40;
            while (current is not null && maxWalk-- > 0)
            {
                var native = current.get_CurrentNativeWindowHandle();
                if (!native.IsNull)
                {
                    var root = Windows.Win32.PInvoke.GetAncestor(
                        native, Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT);
                    return root.IsNull ? (nint)native : (nint)root;
                }
                current = walker.GetParentElement(current);
            }

            return 0;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "Deriving element top-level HWND failed; leaving capture on the session window");
            return 0;
        }
    }

    /// <summary>
    /// Decides whether to retarget the capture surface to the element's derived top-level window
    /// and, when so, updates the capture origin/size to that window's rect. The authoritative HWND
    /// is supplied by <paramref name="getElementTopLevelHwnd"/> (production: the UIA native-window
    /// ancestor of the resolved element via <see cref="ResolveElementTopLevelHwnd"/>; tests: an
    /// injected value). Retargets only when the derived window is non-zero and differs from the
    /// session window; otherwise returns <paramref name="sessionHwnd"/> and leaves the rect intact.
    /// </summary>
    /// <param name="sessionHwnd">The session's main window HWND.</param>
    /// <param name="captureOriginLeft">In/out: updated to the derived window's screen-left when retargeting.</param>
    /// <param name="captureOriginTop">In/out: updated to the derived window's screen-top when retargeting.</param>
    /// <param name="srcWidth">In/out: updated to the derived window's pixel width when retargeting.</param>
    /// <param name="srcHeight">In/out: updated to the derived window's pixel height when retargeting.</param>
    /// <param name="getElementTopLevelHwnd">
    /// Supplies the element's authoritative top-level HWND (0 when it cannot be derived).
    /// </param>
    /// <param name="getWindowRect">
    /// Optional injectable for <c>GetWindowRect</c>. Returns <c>(left, top, right, bottom)</c>.
    /// When <see langword="null"/>, the real <c>Windows.Win32.PInvoke.GetWindowRect</c> is called.
    /// </param>
    /// <returns>
    /// The derived top-level HWND when a retarget is warranted, otherwise <paramref name="sessionHwnd"/>.
    /// </returns>
    /// <remarks>
    /// Coverage ceiling (issue #630): unit tests cover the decision logic through the injectable rect
    /// delegate. The uncovered branch is the real GetWindowRect call for a live derived HWND, which is
    /// native desktop state and not deterministic in headless/shared coverage runs.
    /// </remarks>
    internal static nint DeriveElementCaptureHwnd(
        nint sessionHwnd,
        ref int captureOriginLeft, ref int captureOriginTop,
        ref int srcWidth, ref int srcHeight,
        Func<nint> getElementTopLevelHwnd,
        Func<nint, (int left, int top, int right, int bottom)>? getWindowRect = null)
    {
        var derived = getElementTopLevelHwnd();

        // No retarget when the element has no derivable top-level window, or it is the session
        // window itself (genuine in-window element). The overlap case that broke the previous
        // geometry approach cannot occur here: an overlapping window is never a UIA ancestor.
        if (derived == 0 || derived == sessionHwnd)
        {
            return sessionHwnd;
        }

        // Update capture origin and dimensions to the derived top-level window.
        if (getWindowRect is not null)
        {
            var (l, t, r, b) = getWindowRect(derived);
            captureOriginLeft = l;
            captureOriginTop = t;
            srcWidth = Math.Max(1, r - l);
            srcHeight = Math.Max(1, b - t);
        }
        else
        {
            Windows.Win32.PInvoke.GetWindowRect(
                new Windows.Win32.Foundation.HWND(derived), out var derivedRect);
            captureOriginLeft = derivedRect.left;
            captureOriginTop = derivedRect.top;
            srcWidth = Math.Max(1, derivedRect.right - derivedRect.left);
            srcHeight = Math.Max(1, derivedRect.bottom - derivedRect.top);
        }

        return derived;
    }

}
