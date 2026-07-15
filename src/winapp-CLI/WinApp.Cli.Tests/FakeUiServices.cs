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

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => WindowsByTitleResult;
    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => WindowsByPidResult;

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
        => Task.FromResult(InspectResult);

    public Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct)
        => Task.FromResult(InspectResult);

    public Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct)
        => Task.FromResult(SearchResult.Take(maxResults).ToArray());

    public Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct)
    {
        if (FindSingleElementThrowException is not null) { throw FindSingleElementThrowException; }

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
        => Task.FromResult(PropertiesResult);

    public Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, bool captureScreen, bool focus, CancellationToken ct)
        => Task.FromResult(ScreenshotResult);

    public Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
        => Task.FromResult(InvokeResult);

    public Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct)
        => Task.CompletedTask;

    public Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
        => Task.CompletedTask;

    public Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
        => Task.CompletedTask;

    public Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct)
        => Task.CompletedTask;

    public UiElement? FocusedResult { get; set; } = new UiElement { Id = "e0", Type = "Edit", Name = "FocusedElement" };

    public Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct)
        => Task.FromResult(FocusedResult);

    public Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct)
        => Task.FromResult<string?>("fake text content");
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

    public Task<UiSessionInfo> ResolveSessionAsync(string? app, long? hwnd, CancellationToken ct)
    {
        if (ThrowException is not null) { throw ThrowException; }
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

    public void Send(long hwnd, IReadOnlyList<WinApp.Cli.Helpers.KeyAction> actions, WinApp.Cli.Helpers.KeyTransport transport)
        => SendCalls.Add(new(hwnd, actions, transport));
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

    /// <summary>Error emitted on denial — defaults to the locked-desktop reason.</summary>
    public string DenyCode { get; set; } = WinApp.Cli.Helpers.UiJsonError.CodeNoInteractiveDesktop;

    public bool TryEnsureForeground(long targetHwnd, Microsoft.Extensions.Logging.ILogger logger, bool json, string action)
    {
        Calls.Add(new(targetHwnd, action));
        if (Allow)
        {
            return true;
        }

        WinApp.Cli.Helpers.UiJsonError.Emit(json, DenyCode,
            $"Foreground guard denied the {action} (test).");
        return false;
    }
}

