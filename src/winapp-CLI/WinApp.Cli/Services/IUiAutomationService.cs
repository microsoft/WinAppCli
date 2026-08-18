// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services.InteractiveDesktop;

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

    /// <summary>
    /// Resolve a top-level window's screen rectangle via <c>GetWindowRect</c>. Returns
    /// <see langword="false"/> (and a default rect) when the handle is 0, invalid, or unreadable —
    /// callers must treat that as "no verifiable target window". Used to bounds-check
    /// <c>ui touch</c>/<c>ui pen</c> coordinates before any OS-wide injection.
    /// </summary>
    bool TryGetWindowRect(long hwnd, out PointerRect rect);

    
    Task<UiElement[]> InspectAsync(UiSessionInfo session, string? elementId, int depth, CancellationToken ct);
    Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct);
    Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct);
    Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct);
    Task<Dictionary<string, object?>> GetPropertiesAsync(UiSessionInfo session, UiElement element, string? propertyName, CancellationToken ct);
    /// <summary>Captures a window or element region as raw BGRA pixels.</summary>
    /// <param name="desktopSection">
    /// Scope entered only around restore, foreground, and live-screen moments — never around the
    /// readback, crop or encode (spec §6.5).
    /// </param>
    /// <param name="observeOnly">
    /// When <see langword="true"/> the capture refuses to restore or foreground anything and instead
    /// throws <see cref="DesktopEscalationRequiredException"/>, so an <c>Observe</c> screenshot can
    /// escalate the whole invocation and recapture from the beginning.
    /// </param>
    Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(
        UiSessionInfo session, string? elementId, bool captureScreen, bool focus,
        IDesktopSection desktopSection, bool observeOnly, CancellationToken ct);

    /// <summary>Records a window or element region to H.264 MP4.</summary>
    /// <param name="desktopSection">
    /// Scope used only for the restore/foreground moment before capture starts. The capture loop itself
    /// stays outside it so same-owner input can interleave with an in-flight recording (spec §6.3).
    /// </param>
    /// <param name="onRecordingStarted">Invoked after the first frame; reports whether frame output is active.</param>
    Task<RecordCaptureResult> RecordAsync(UiSessionInfo session, string? elementId, RecordOptions options, IDesktopSection desktopSection, CancellationToken ct, Action<bool>? onRecordingStarted = null);
    Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct);
    Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct);
    Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct);
    Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
}