// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Immutable snapshot of a process captured at enumeration time. The resolver works with
/// these value copies so it never has to touch (or dispose) a live
/// <see cref="System.Diagnostics.Process"/> handle while making selection decisions.
/// </summary>
internal readonly record struct UiProcessInfo(int Id, string ProcessName, nint MainWindowHandle, string? MainWindowTitle);

/// <summary>
/// Abstracts the operating-system boundaries used by <see cref="UiSessionService"/> — process
/// enumeration (<see cref="System.Diagnostics.Process"/>) and a few Win32 window queries.
/// Production wires this to <see cref="SystemUiQuery"/>, which calls the real APIs; tests inject a
/// fake so the resolver's decision logic (PID/name/partial-name selection, auto-select, HWND
/// resolution) can be exercised without live processes or a real desktop.
/// </summary>
internal interface ISystemUiQuery
{
    /// <summary>Snapshot the process with the given PID, or <c>null</c> if no such process exists.</summary>
    UiProcessInfo? GetProcessById(int pid);

    /// <summary>Snapshot every process whose name matches <paramref name="name"/> exactly.</summary>
    IReadOnlyList<UiProcessInfo> GetProcessesByName(string name);

    /// <summary>Snapshot every process whose name contains <paramref name="substring"/> (case-insensitive).</summary>
    IReadOnlyList<UiProcessInfo> GetProcessesMatching(string substring);

    /// <summary>The current foreground window handle (0 when none).</summary>
    nint GetForegroundWindow();

    /// <summary>The PID that owns <paramref name="hwnd"/> (0 when the window is not found/accessible).</summary>
    uint GetProcessIdForWindow(long hwnd);

    /// <summary>The title text of <paramref name="hwnd"/>, or <c>null</c> when empty/unavailable.</summary>
    string? GetWindowText(long hwnd);
}
