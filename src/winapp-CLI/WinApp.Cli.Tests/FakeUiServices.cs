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
    public Dictionary<string, object?> PropertiesResult { get; set; } = [];
    public string InvokeResult { get; set; } = "InvokePattern";
    public (byte[] Pixels, int Width, int Height) ScreenshotResult { get; set; } = (new byte[4], 1, 1);
    public List<(nint Hwnd, int Pid, string Title)> WindowsByTitleResult { get; set; } = [];
    public List<(nint Hwnd, int Pid, string Title)> WindowsByPidResult { get; set; } = [];

    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery) => WindowsByTitleResult;
    public List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid) => WindowsByPidResult;

    public Task<UiElement[]> InspectAsync(UiSessionInfo session, string? elementId, int depth, CancellationToken ct)
        => Task.FromResult(InspectResult);

    public Task<UiElement[]> InspectAncestorsAsync(UiSessionInfo session, string elementId, CancellationToken ct)
        => Task.FromResult(InspectResult);

    public Task<UiElement[]> SearchAsync(UiSessionInfo session, SelectorExpression selector, int maxResults, CancellationToken ct)
        => Task.FromResult(SearchResult.Take(maxResults).ToArray());

    public Task<UiElement?> FindSingleElementAsync(UiSessionInfo session, SelectorExpression selector, CancellationToken ct)
        => Task.FromResult(FindSingleResult);

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

    public Task<UiSessionInfo> ResolveSessionAsync(string? app, long? hwnd, CancellationToken ct)
        => Task.FromResult(SessionResult);
}

/// <summary>
/// Fake mouse input for testing — records calls instead of issuing real SendInput.
/// </summary>
internal class FakeMouseInput : WinApp.Cli.Helpers.IMouseInput
{
    public record HoverCall(int ScreenX, int ScreenY);
    public record ClickCall(int ScreenX, int ScreenY, bool DoubleClick, bool RightClick);
    public record DragCall(int FromX, int FromY, int ToX, int ToY, bool RightButton);
    public record ScrollWheelCall(int ScreenX, int ScreenY, int Delta);

    public List<HoverCall> HoverCalls { get; } = [];
    public List<ClickCall> ClickCalls { get; } = [];
    public List<DragCall> DragCalls { get; } = [];
    public List<ScrollWheelCall> ScrollWheelCalls { get; } = [];

    public void Hover(int screenX, int screenY) => HoverCalls.Add(new(screenX, screenY));
    public void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false)
        => ClickCalls.Add(new(screenX, screenY, doubleClick, rightClick));
    public void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false)
        => DragCalls.Add(new(fromScreenX, fromScreenY, toScreenX, toScreenY, rightButton));
    public void ScrollWheel(int screenX, int screenY, int delta)
        => ScrollWheelCalls.Add(new(screenX, screenY, delta));
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
