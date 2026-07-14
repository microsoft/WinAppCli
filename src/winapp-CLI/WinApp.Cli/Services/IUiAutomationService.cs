// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Low-level UI Automation (UIA) operations. Uses Windows UIA APIs to inspect
/// and interact with any running Windows app.
/// </summary>
internal interface IUiAutomationService
{
    /// <summary>
    /// Find all top-level windows matching a partial title. Returns (HWND, PID, Title) tuples.
    /// Uses Win32 enumeration to find ALL windows including inactive/background ones.
    /// </summary>
    List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery);

    /// <summary>
    /// Find all top-level windows for a specific process ID.
    /// </summary>
    List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid);
    
    Task<UiElement[]> InspectAsync(UiSessionInfo session, string? elementId, int depth, CancellationToken ct);
    Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct);
    Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct);
    Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct);
    Task<Dictionary<string, object?>> GetPropertiesAsync(UiSessionInfo session, UiElement element, string? propertyName, CancellationToken ct);
    Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, bool captureScreen, bool focus, CancellationToken ct);

    /// <summary>
    /// Records the target window (or an element's region) to an H.264 MP4 at the
    /// requested frame rate for the requested duration, encoding incrementally.
    /// </summary>
    /// <param name="onRecordingStarted">
    /// Optional callback invoked once the encoder is initialized and the first frame has been
    /// captured — i.e., recording is genuinely underway. Use this to arm a stdin-stop monitor or
    /// emit a liveness event so that programmatic callers never trigger a premature cancel.
    /// </param>
    Task<RecordCaptureResult> RecordAsync(UiSessionInfo session, string? elementId, RecordOptions options, CancellationToken ct, Action? onRecordingStarted = null);
    Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct);
    Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct);
    Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct);
    Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
}
