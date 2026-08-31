// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Low-level UI Automation (UIA) operations. Uses Windows UIA APIs to inspect
/// and interact with any running Windows app.
/// </summary>
public interface IUiAutomation
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

    
    Task<UiElement[]> InspectAsync(UiTarget session, string? elementId, int depth, CancellationToken ct);
    Task<UiElement[]> InspectAncestorsAsync(UiTarget session, string elementId, CancellationToken ct);
    Task<UiElement[]> SearchAsync(UiTarget session, UiSelector selector, int maxResults, CancellationToken ct);
    Task<UiElement?> FindSingleElementAsync(UiTarget session, UiSelector selector, CancellationToken ct);
    Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget session, UiElement element, string? propertyName, CancellationToken ct);
    Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget session, string? elementId, bool captureScreen, bool focus, CancellationToken ct);

    Task<string> InvokeAsync(UiTarget session, UiElement element, CancellationToken ct);
    Task SetValueAsync(UiTarget session, UiElement element, string text, CancellationToken ct);
    Task FocusAsync(UiTarget session, UiElement element, CancellationToken ct);
    Task ScrollIntoViewAsync(UiTarget session, UiElement element, CancellationToken ct);
    Task ScrollContainerAsync(UiTarget session, UiElement element, string? direction, string? destination, CancellationToken ct);
    Task<UiElement?> GetFocusedElementAsync(UiTarget session, CancellationToken ct);
    Task<string?> GetTextAsync(UiTarget session, UiElement element, CancellationToken ct);

    /// <summary>
    /// Resolves the target's root UIA window. Returns <see langword="false"/> when no UIA window
    /// exists for the target. <paramref name="hwnd"/> is 0 when the root element has no native window
    /// handle, in which case callers should fall back to <see cref="UiTarget.WindowHandle"/>.
    /// </summary>
    bool TryResolveRootWindow(UiTarget target, out nint hwnd, out string? title);

    /// <summary>
    /// Resolves the element's top-level native window by walking its UIA ancestors, or 0 when no
    /// ancestor exposes one. Lets a caller retarget capture at the window an element actually lives
    /// in, which for popups and dialogs is not the session window.
    /// </summary>
    nint ResolveElementTopLevelWindow(UiTarget target, UiElement element);

    /// <summary>
    /// The window's bounds excluding the invisible DWM resize border, falling back to
    /// <paramref name="fallback"/> when the extended frame bounds are unavailable.
    /// </summary>
    PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback);

    /// <summary>
    /// Captures a window's pixels (BGRA) via <c>PrintWindow</c>, foregrounding and retrying once when
    /// the first attempt comes back blank.
    /// </summary>
    byte[] CaptureWindowPixels(nint hwnd, int width, int height);

    /// <summary>
    /// Captures a screen region (BGRA), scaling it to fit
    /// <paramref name="displayWidth"/>×<paramref name="displayHeight"/> and centering it within an
    /// <paramref name="encoderWidth"/>×<paramref name="encoderHeight"/> surface.
    /// </summary>
    byte[] CaptureScreenPixels(
        int x, int y, int cropWidth, int cropHeight,
        int encoderWidth, int encoderHeight,
        int displayWidth, int displayHeight);
}
