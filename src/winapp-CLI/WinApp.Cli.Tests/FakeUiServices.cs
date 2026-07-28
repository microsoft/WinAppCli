// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake UIA service for testing — returns configurable element data without touching real UIA.
/// </summary>
internal class FakeUiAutomationService : IUiAutomationService
{
    public UiElement[] InspectResult { get; set; } = [];
    public UiElement[] SearchResult { get; set; } = [];
    public UiElement? FindSingleResult { get; set; }

    /// <summary>
    /// Optional per-call results for <see cref="FindSingleElementAsync"/>. When non-empty, the first
    /// resolution of a *new* selector dequeues the next entry (so a single command resolving two
    /// distinct selectors — e.g. a selector→selector drag — returns two distinct elements). Re-reads of
    /// the same selector (the N5 stable-resolve) return the memoized element instead of draining the
    /// queue. Falls back to <see cref="FindSingleResult"/> when empty.
    /// </summary>
    public Queue<UiElement?> FindSingleResults { get; } = new();

    /// <summary>
    /// Per-selector movement sequences for N5 stability tests. Each read of the keyed selector advances
    /// the sequence (simulating an element whose bounds change between reads); once drained, the last
    /// value sticks. Key by the selector's slug or query text. Enqueue the command's initial-resolve
    /// element first, then one element per stability re-read.
    /// </summary>
    public Dictionary<string, Queue<UiElement?>> MovingResults { get; } = new();

    private readonly Dictionary<string, UiElement?> _resolvedBySelector = new();
    private readonly Dictionary<string, UiElement?> _lastMoving = new();
    public Dictionary<string, object?> PropertiesResult { get; set; } = [];
    public string InvokeResult { get; set; } = "InvokePattern";
    public (byte[] Pixels, int Width, int Height) ScreenshotResult { get; set; } = (new byte[4], 1, 1);
    public List<(nint Hwnd, int Pid, string Title)> WindowsByTitleResult { get; set; } = [];
    public List<(nint Hwnd, int Pid, string Title)> WindowsByPidResult { get; set; } = [];

    /// <summary>Text returned by <see cref="GetTextAsync"/>. Set to <see langword="null"/> to exercise
    /// the "no value" branches of get-value / wait-for. Defaults to a non-null sample.</summary>
    public string? GetTextResult { get; set; } = "fake text content";

    /// <summary>Per-call text sequence for <see cref="GetTextAsync"/>: each read dequeues the next entry,
    /// so a wait-for --value poll can see the value change across polls (e.g. "old" then "target"). Once
    /// drained, falls back to <see cref="GetTextResult"/>. Empty by default = use the single result.</summary>
    public Queue<string?> GetTextResults { get; } = new();

    // ---- Additive throw knobs (default null = no-op, so existing tests are unaffected) --------------
    // Each method throws the configured exception at its start, letting a test drive a command's
    // COMException (stale-element) or generic error branches without touching real UIA.
    public Exception? FindSingleThrow { get; set; }

    /// <summary>When &gt; 0, the next N <see cref="FindSingleElementAsync"/> calls throw a transient error
    /// (then decrement), simulating an element that isn't ready on the first poll(s). Drives wait-for's
    /// per-poll catch → keep-polling continuation deterministically. Default 0 = no transient failures.</summary>
    public int FindSingleThrowCount { get; set; }
    public Exception? InspectThrow { get; set; }
    public Exception? SearchThrow { get; set; }
    public Exception? PropertiesThrow { get; set; }
    public Exception? ScreenshotThrow { get; set; }
    public Exception? InvokeThrow { get; set; }
    public Exception? FocusThrow { get; set; }
    public Exception? GetFocusedThrow { get; set; }
    public Exception? GetTextThrow { get; set; }
    public Exception? ScrollContainerThrow { get; set; }
    public Exception? ScrollIntoViewThrow { get; set; }
    public Exception? SetValueThrow { get; set; }
    public Exception? FindWindowsThrow { get; set; }

    /// <summary>When set, the primary <see cref="InvokeAsync"/> throws this for an element that carries an
    /// <see cref="UiElement.InvokableAncestor"/> (simulating "element not invokable"), while the follow-up
    /// call on the ancestor itself (no InvokableAncestor) succeeds — driving invoke's ancestor-fallback.</summary>
    public bool InvokeThrowsForAncestorFallback { get; set; }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery)
    {
        if (FindWindowsThrow is not null) { throw FindWindowsThrow; }
        return WindowsByTitleResult;
    }

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid)
    {
        if (FindWindowsThrow is not null) { throw FindWindowsThrow; }
        return WindowsByPidResult;
    }

    /// <summary>When non-null, <see cref="FindSingleElementAsync"/> throws this exception instead of
    /// returning a result. Use to simulate selector-ambiguity or other UIA failures.</summary>
    public Exception? FindSingleElementThrowException { get; set; }

    /// <summary>The rectangle returned for any nonzero handle (default: 0,0 – 1920,1080).</summary>
    public WinApp.Cli.Helpers.PointerRect WindowRect { get; set; } = new(0, 0, 1920, 1080);

    /// <summary>When <see langword="false"/>, reports the window rect as unreadable (returns false).</summary>
    public bool WindowRectAllow { get; set; } = true;

    /// <summary>Records each <see cref="TryGetWindowRect"/> call so tests can distinguish the
    /// hwnd-0 rejection (no lookup) from the out-of-bounds rejection (lookup happened).</summary>
    public List<long> WindowRectCalls { get; } = [];

    public bool TryGetWindowRect(long hwnd, out WinApp.Cli.Helpers.PointerRect rect)
    {
        WindowRectCalls.Add(hwnd);
        if (!WindowRectAllow || hwnd == 0)
        {
            rect = default;
            return false;
        }

        rect = WindowRect;
        return true;
    }

    public Task<UiElement[]> InspectAsync(UiSessionInfo session, string? elementId, int depth, CancellationToken ct)
    {
        if (InspectThrow is not null) { throw InspectThrow; }
        return Task.FromResult(InspectResult);
    }

    public Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct)
    {
        if (InspectThrow is not null) { throw InspectThrow; }
        return Task.FromResult(InspectResult);
    }

    public Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct)
    {
        if (SearchThrow is not null) { throw SearchThrow; }
        return Task.FromResult(SearchResult.Take(maxResults).ToArray());
    }

    public Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct)
    {
        if (FindSingleElementThrowException is not null) { throw FindSingleElementThrowException; }
        if (FindSingleThrow is not null) { throw FindSingleThrow; }
        if (FindSingleThrowCount > 0)
        {
            FindSingleThrowCount--;
            throw new InvalidOperationException("Transient element lookup failure (test).");
        }
        var key = selector.Slug ?? selector.Query ?? string.Empty;

        // Per-selector movement sequence (N5 stability tests): advance each read, last value sticks.
        if (MovingResults.TryGetValue(key, out var seq))
        {
            var moving = seq.Count > 0 ? seq.Dequeue() : _lastMoving.GetValueOrDefault(key);
            _lastMoving[key] = moving;
            return Task.FromResult(moving);
        }

        // Re-read of an already-resolved selector → return the memoized element so the N5 re-resolve
        // doesn't drain the cross-selector queue.
        if (_resolvedBySelector.TryGetValue(key, out var memo))
        {
            return Task.FromResult(memo);
        }

        // First resolution of a new selector: dequeue the next distinct cross-selector result.
        if (FindSingleResults.Count > 0)
        {
            var dequeued = FindSingleResults.Dequeue();
            _resolvedBySelector[key] = dequeued;
            return Task.FromResult(dequeued);
        }

        return Task.FromResult(FindSingleResult);
    }

    public Task<Dictionary<string, object?>> GetPropertiesAsync(UiSessionInfo session, UiElement element, string? propertyName, CancellationToken ct)
    {
        if (PropertiesThrow is not null) { throw PropertiesThrow; }
        return Task.FromResult(PropertiesResult);
    }

    public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, bool captureScreen, bool focus, CancellationToken ct)
    {
        if (ScreenshotThrow is not null) { throw ScreenshotThrow; }
        return Task.FromResult(ScreenshotResult);
    }

    /// <summary>Configurable result for <see cref="RecordAsync"/>. The fake writes a tiny placeholder file to the output path.</summary>
    public RecordCaptureResult RecordResult { get; set; } = new() { Frames = 3, Width = 2, Height = 2, FileSize = 0, Mode = "wgc" };

    public RecordOptions? LastRecordOptions { get; private set; }

    /// <summary>When non-null, <see cref="RecordAsync"/> throws this exception instead of writing a file.</summary>
    public Exception? RecordException { get; set; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="RecordAsync"/> signals readiness (calls onRecordingStarted)
    /// then blocks on the cancellation token rather than returning immediately. This lets tests verify that
    /// a stop signal (stdin EOF, Ctrl+C) cancels an in-progress recording and produces a graceful result.
    /// </summary>
    public bool RecordShouldWaitForCancellation { get; set; }

    public async Task<RecordCaptureResult> RecordAsync(UiSessionInfo session, string? elementId, RecordOptions options, CancellationToken ct, Action? onRecordingStarted = null)
    {
        LastRecordOptions = options;
        if (RecordException is not null)
        {
            throw RecordException;
        }
        await File.WriteAllBytesAsync(options.OutputPath, new byte[16], CancellationToken.None);
        RecordFrameArtifactResult? frameArtifacts = RecordResult.FrameArtifacts;
        if (options.FramesDirectory is not null)
        {
            Directory.CreateDirectory(Path.Combine(options.FramesDirectory, "frames"));
            await File.WriteAllTextAsync(
                Path.Combine(options.FramesDirectory, "frames.ndjson"),
                "{\"sampleIndex\":0}\n",
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(options.FramesDirectory, "manifest.json"),
                "{\"schemaVersion\":1,\"status\":\"complete\"}",
                CancellationToken.None);
            frameArtifacts = new RecordFrameArtifactResult
            {
                Directory = options.FramesDirectory,
                Manifest = Path.Combine(options.FramesDirectory, "manifest.json"),
                Index = Path.Combine(options.FramesDirectory, "frames.ndjson"),
                Samples = RecordResult.Frames,
                Images = 1,
                RepeatedSamples = Math.Max(0, RecordResult.Frames - 1),
                TotalBytes = 64,
            };
            options.OnProgress?.Invoke(new RecordProgress
            {
                ElapsedMs = 5_000,
                Samples = RecordResult.Frames,
                Images = 1,
                AchievedFps = RecordResult.Frames / 5.0,
            });
        }
        // Signal readiness before returning — mirrors the real service behavior (encoder is
        // initialized and the first frame has been captured at this point).
        onRecordingStarted?.Invoke();
        if (RecordShouldWaitForCancellation)
        {
            // Block until cancelled — simulates a long recording that is stopped early
            // by a stdin EOF, Ctrl+C, or a duration-expiry. The real RecordAsync returns
            // normally (not via exception) when cancelled inside the frame loop.
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { /* graceful stop */ }
        }
        var size = new FileInfo(options.OutputPath).Length;
        return new RecordCaptureResult
        {
            Frames = RecordResult.Frames,
            Width = RecordResult.Width,
            Height = RecordResult.Height,
            FileSize = size,
            Mode = RecordResult.Mode,
            ElapsedMs = RecordResult.ElapsedMs,
            AchievedFps = RecordResult.AchievedFps,
            CadenceRatio = RecordResult.CadenceRatio,
            StopReason = RecordResult.StopReason,
            FrameArtifacts = frameArtifacts,
            Warnings = RecordResult.Warnings,
        };
    }

    public Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        if (InvokeThrow is not null) { throw InvokeThrow; }
        if (InvokeThrowsForAncestorFallback && element.InvokableAncestor is not null)
        {
            throw new InvalidOperationException("Element does not support an actionable pattern (test).");
        }
        return Task.FromResult(InvokeResult);
    }

    public Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct)
    {
        if (SetValueThrow is not null) { throw SetValueThrow; }
        return Task.CompletedTask;
    }

    public Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        if (FocusThrow is not null) { throw FocusThrow; }
        return Task.CompletedTask;
    }

    public Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        if (ScrollIntoViewThrow is not null) { throw ScrollIntoViewThrow; }
        return Task.CompletedTask;
    }

    public Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct)
    {
        if (ScrollContainerThrow is not null) { throw ScrollContainerThrow; }
        return Task.CompletedTask;
    }

    public UiElement? FocusedResult { get; set; } = new UiElement { Id = "e0", Type = "Edit", Name = "FocusedElement" };

    public Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct)
    {
        if (GetFocusedThrow is not null) { throw GetFocusedThrow; }
        return Task.FromResult(FocusedResult);
    }

    public Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
    {
        if (GetTextThrow is not null) { throw GetTextThrow; }
        if (GetTextResults.Count > 0) { return Task.FromResult(GetTextResults.Dequeue()); }
        return Task.FromResult(GetTextResult);
    }
}

/// <summary>
/// Fake session service for testing — returns a configurable session without process resolution.
/// </summary>
internal class FakeUiSessionService : IUiSessionService
{
    public UiSessionInfo SessionResult { get; set; } = new()
    {
        ProcessId = 1234,
        ProcessName = "TestApp",
        WindowTitle = "Test Window"
    };

    /// <summary>When non-null, <see cref="ResolveSessionAsync"/> throws this exception instead
    /// of returning <see cref="SessionResult"/>. Use to test command-level exception handling.</summary>
    public Exception? ThrowException { get; set; }

    /// <summary>When set, <see cref="ResolveSessionAsync"/> throws this — drives a command's generic
    /// (or COMException) catch from inside its <c>try</c>, before any element work. Default null = no-op.</summary>
    public Exception? ResolveThrow { get; set; }

    public Task<UiSessionInfo> ResolveSessionAsync(string? app, long? hwnd, CancellationToken ct)
    {
        if (ThrowException is not null) { throw ThrowException; }
        if (ResolveThrow is not null) { throw ResolveThrow; }
        return Task.FromResult(SessionResult);
    }
}

/// <summary>
/// Fake mouse input for testing — records calls instead of issuing real SendInput.
/// </summary>
internal class FakeMouseInput : WinApp.Cli.Helpers.IMouseInput
{
    public record HoverCall(int ScreenX, int ScreenY);
    public record MoveCursorCall(int ScreenX, int ScreenY);
    public record ClickCall(int ScreenX, int ScreenY, bool DoubleClick, bool RightClick, int SettleMs = 0);
    public record DragCall(int FromX, int FromY, int ToX, int ToY, bool RightButton, int HoldMs = 0, int DwellMs = 0, int SettleMs = 50);
    public record ScrollWheelCall(int ScreenX, int ScreenY, int Delta, int SettleMs = 30);

    public List<HoverCall> HoverCalls { get; } = [];
    public List<MoveCursorCall> MoveCursorCalls { get; } = [];
    public List<ClickCall> ClickCalls { get; } = [];
    public List<DragCall> DragCalls { get; } = [];
    public List<ScrollWheelCall> ScrollWheelCalls { get; } = [];

    public void Hover(int screenX, int screenY) => HoverCalls.Add(new(screenX, screenY));
    public void MoveCursor(int screenX, int screenY) => MoveCursorCalls.Add(new(screenX, screenY));
    public void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false, int settleMs = 50)
        => ClickCalls.Add(new(screenX, screenY, doubleClick, rightClick, settleMs));
    public void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0, int settleMs = 50)
        => DragCalls.Add(new(fromScreenX, fromScreenY, toScreenX, toScreenY, rightButton, holdMs, dwellMs, settleMs));
    public void ScrollWheel(int screenX, int screenY, int delta, int settleMs = 30)
        => ScrollWheelCalls.Add(new(screenX, screenY, delta, settleMs));
}

/// <summary>
/// Fake pointer input for testing — records injected touch contacts and pen strokes instead of
/// issuing real synthetic-pointer injection.
/// </summary>
internal class FakePointerInput : WinApp.Cli.Helpers.IPointerInput
{
    public record TouchCall(
        WinApp.Cli.Helpers.TouchGesture Gesture,
        IReadOnlyList<IReadOnlyList<WinApp.Cli.Helpers.PointerPoint>> ContactPaths,
        int HoldMs,
        int DurationMs);

    public record PenCall(
        IReadOnlyList<WinApp.Cli.Helpers.PointerPoint> Path,
        float Pressure,
        int TiltX,
        int TiltY,
        bool Eraser,
        int DurationMs);

    public List<TouchCall> TouchCalls { get; } = [];
    public List<PenCall> PenCalls { get; } = [];

    /// <summary>When non-null, both Touch() and Pen() throw this exception instead of recording the call.
    /// Use to test command-level exception handling without a live injection path.</summary>
    public Exception? ThrowException { get; set; }

    public void Touch(
        WinApp.Cli.Helpers.TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<WinApp.Cli.Helpers.PointerPoint>> contactPaths,
        int holdMs,
        int durationMs)
    {
        if (ThrowException is not null) { throw ThrowException; }
        TouchCalls.Add(new(gesture, contactPaths, holdMs, durationMs));
    }

    public void Pen(
        IReadOnlyList<WinApp.Cli.Helpers.PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        int durationMs)
    {
        if (ThrowException is not null) { throw ThrowException; }
        PenCalls.Add(new(path, pressure, tiltX, tiltY, eraser, durationMs));
    }
}

/// <summary>
/// Fake keyboard input for testing — records the actions/transport instead of issuing real input.
/// </summary>
internal class FakeKeyboardInput : WinApp.Cli.Helpers.IKeyboardInput
{
    public record SendCall(long Hwnd, IReadOnlyList<WinApp.Cli.Helpers.KeyAction> Actions, WinApp.Cli.Helpers.KeyTransport Transport);

    public List<SendCall> SendCalls { get; } = [];

    /// <summary>When set, <see cref="Send"/> records the call then throws this — lets a test drive the
    /// command's exception mapping (e.g. a mid-injection <c>ForegroundLostException</c> → foreground_not_target).</summary>
    public Exception? SendException { get; set; }

    public void Send(long hwnd, IReadOnlyList<WinApp.Cli.Helpers.KeyAction> actions, WinApp.Cli.Helpers.KeyTransport transport)
    {
        SendCalls.Add(new(hwnd, actions, transport));
        if (SendException is not null)
        {
            throw SendException;
        }
    }
}

/// <summary>
/// Fake foreground guard for testing — lets a test force the pre-injection foreground decision so the
/// coordinate-gesture verbs can be exercised without a live, unlocked desktop. Proceeds by default.
/// </summary>
internal class FakeForegroundGuard : WinApp.Cli.Helpers.IForegroundGuard
{
    public record EnsureCall(long TargetHwnd, string Action);

    public List<EnsureCall> Calls { get; } = [];

    /// <summary>When <see langword="false"/>, emits the configured error and aborts the gesture.</summary>
    public bool Allow { get; set; } = true;

    /// <summary>When set, denies exactly the Nth call (1-based) regardless of <see cref="Allow"/>, letting
    /// a test drive the *final* foreground gate (e.g. click/scroll/drag check the foreground twice —
    /// first gate passes, final gate denies). Other calls fall back to <see cref="Allow"/>.</summary>
    public int? DenyOnCallNumber { get; set; }

    /// <summary>Error emitted on denial — defaults to the locked-desktop reason.</summary>
    public string DenyCode { get; set; } = WinApp.Cli.Helpers.UiJsonError.CodeNoInteractiveDesktop;

    /// <summary>Value returned by <see cref="IsRemoteSession"/> — drives the remote-session delivery warning on touch/pen.</summary>
    public bool IsRemoteSessionResult { get; set; }

    public bool IsRemoteSession() => IsRemoteSessionResult;

    public bool TryEnsureForeground(long targetHwnd, Microsoft.Extensions.Logging.ILogger logger, bool json, string action)
    {
        Calls.Add(new(targetHwnd, action));

        var deny = DenyOnCallNumber is int n ? Calls.Count == n : !Allow;
        if (!deny)
        {
            return true;
        }

        WinApp.Cli.Helpers.UiJsonError.Emit(json, DenyCode,
            $"Foreground guard denied the {action} (test).");
        return false;
    }
}

/// <summary>
/// Fake <see cref="WinApp.Cli.Helpers.IOwnedWindowFinder"/> — returns a configurable owned-window list
/// (default empty) so the screenshot command's owned-dialog / multi-window discovery can be exercised
/// without a live desktop. The real finder issues a Win32 window walk.
/// </summary>
internal class FakeOwnedWindowFinder : WinApp.Cli.Helpers.IOwnedWindowFinder
{
    /// <summary>Owned windows returned for any input. Default empty = "no owned dialogs".</summary>
    public List<(nint Hwnd, int Pid, string Title)> OwnedWindowsResult { get; set; } = [];

    public List<(nint Hwnd, int Pid, string Title)> FindOwnedWindows(List<(nint Hwnd, int Pid, string Title)> appWindows)
        => OwnedWindowsResult;
}

/// <summary>
/// Fake <see cref="ISystemUiQuery"/> — drives <see cref="UiSessionService"/>'s OS boundaries
/// (process enumeration + a few Win32 window queries) from in-memory data so its resolver logic can
/// be exercised deterministically. Every knob defaults to "nothing found" so a bare instance yields
/// the same behavior a headless box would (no processes, no foreground, no window title).
/// </summary>
internal sealed class FakeSystemUiQuery : ISystemUiQuery
{
    /// <summary>Explicit per-PID lookups. A key mapped to <c>null</c> models "no such process".</summary>
    public Dictionary<int, UiProcessInfo?> ProcessesById { get; } = [];

    /// <summary>When set, any PID not in <see cref="ProcessesById"/> resolves to this snapshot
    /// (with its <see cref="UiProcessInfo.Id"/> swapped to the requested PID). Lets CreateSession's
    /// name lookup succeed for arbitrary PIDs without seeding each one.</summary>
    public UiProcessInfo? DefaultProcessById { get; set; }

    /// <summary>Result for <see cref="GetProcessesByName"/> (exact-name match). Default empty.</summary>
    public List<UiProcessInfo> ByNameResult { get; set; } = [];

    /// <summary>Result for <see cref="GetProcessesMatching"/> (partial-name match). Default empty.</summary>
    public List<UiProcessInfo> MatchingResult { get; set; } = [];

    /// <summary>Handle returned by <see cref="GetForegroundWindow"/>. Default 0 = "no foreground".</summary>
    public nint ForegroundWindowResult { get; set; }

    /// <summary>PID returned by <see cref="GetProcessIdForWindow"/>. Default 0 = "window not found".</summary>
    public uint ProcessIdForWindowResult { get; set; }

    /// <summary>Title returned by <see cref="GetWindowText"/>. Default null = "no/empty title".</summary>
    public string? WindowTextResult { get; set; }

    /// <summary>Per-HWND window sizes for <see cref="GetWindowSize"/>. Unmapped handles report (0, 0),
    /// matching a headless box; seed distinct areas to drive the "largest window" auto-select heuristic.</summary>
    public Dictionary<long, (int Width, int Height)> WindowSizeByHwnd { get; } = [];

    /// <summary>Per-HWND class names for <see cref="GetWindowClassName"/>. Unmapped handles report null.</summary>
    public Dictionary<long, string?> WindowClassNameByHwnd { get; } = [];

    /// <summary>Per-HWND owner handles for <see cref="GetWindowOwner"/>. Unmapped handles report 0 (no owner).</summary>
    public Dictionary<long, nint> WindowOwnerByHwnd { get; } = [];

    /// <summary>Per-HWND focused child handles for <see cref="GetFocusedWindow"/>. Unmapped handles report 0
    /// (no resolvable focus → the command keeps the passed target HWND).</summary>
    public Dictionary<long, long> FocusedWindowByHwnd { get; } = [];

    /// <summary>Per-HWND top-level root handles for <see cref="GetRootWindow"/>. Unmapped handles report the
    /// handle itself (a top-level window is its own root); map a child to its top-level window to model
    /// <c>GetAncestor(GA_ROOT)</c>.</summary>
    public Dictionary<long, long> RootWindowByHwnd { get; } = [];

    public UiProcessInfo? GetProcessById(int pid)
    {
        if (ProcessesById.TryGetValue(pid, out var info)) { return info; }
        if (DefaultProcessById is { } d) { return d with { Id = pid }; }
        return null;
    }

    public IReadOnlyList<UiProcessInfo> GetProcessesByName(string name) => ByNameResult;

    public IReadOnlyList<UiProcessInfo> GetProcessesMatching(string substring) => MatchingResult;

    public nint GetForegroundWindow() => ForegroundWindowResult;

    public uint GetProcessIdForWindow(long hwnd) => ProcessIdForWindowResult;

    public string? GetWindowText(long hwnd) => WindowTextResult;

    public (int Width, int Height) GetWindowSize(long hwnd)
        => WindowSizeByHwnd.TryGetValue(hwnd, out var size) ? size : (0, 0);

    public string? GetWindowClassName(long hwnd)
        => WindowClassNameByHwnd.TryGetValue(hwnd, out var name) ? name : null;

    public nint GetWindowOwner(long hwnd)
        => WindowOwnerByHwnd.TryGetValue(hwnd, out var owner) ? owner : 0;

    public long GetFocusedWindow(long hwnd)
        => FocusedWindowByHwnd.TryGetValue(hwnd, out var focused) ? focused : 0;

    public long GetRootWindow(long hwnd)
        => RootWindowByHwnd.TryGetValue(hwnd, out var root) ? root : hwnd;
}

/// <summary>
/// Fake <see cref="WinApp.Cli.Helpers.IPollDelay"/> — replaces wait-for's inter-poll wall-clock wait so
/// the retry-loop continuations run deterministically. Uses a 1ms yield (not a busy no-op) to keep poll
/// counts bounded without depending on real 100ms sleeps. Records how many times it was awaited.
/// </summary>
internal sealed class FakePollDelay : WinApp.Cli.Helpers.IPollDelay
{
    /// <summary>Number of inter-poll delays awaited — one per "condition not met, keep polling" iteration.</summary>
    public int CallCount { get; private set; }

    public Task DelayAsync(int milliseconds, CancellationToken ct)
    {
        CallCount++;
        return Task.Delay(1, ct);
    }
}
