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
    Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiSessionInfo session, string? elementId, bool captureScreen, CancellationToken ct);
    Task<string> InvokeAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task SetValueAsync(UiSessionInfo session, UiElement element, string text, CancellationToken ct);
    Task FocusAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task ScrollIntoViewAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
    Task ScrollContainerAsync(UiSessionInfo session, UiElement element, string? direction, string? to, CancellationToken ct);
    Task<UiElement?> GetFocusedElementAsync(UiSessionInfo session, CancellationToken ct);
    Task<string?> GetTextAsync(UiSessionInfo session, UiElement element, CancellationToken ct);
}
