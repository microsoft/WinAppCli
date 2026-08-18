// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Records foreground and restore requests instead of performing them, so tests can assert that a
/// command asks for the foreground only inside a desktop-sensitive section, and never before its
/// foreground validation has run (spec §13).
/// </summary>
internal sealed class FakeDesktopForegroundService : IDesktopForegroundService
{
    /// <summary>Window handles passed to <see cref="RequestForeground"/>, in order.</summary>
    public List<long> ForegroundRequests { get; } = [];

    /// <summary>Window handles passed to <see cref="Restore"/>, in order.</summary>
    public List<long> RestoreRequests { get; } = [];

    /// <summary>Handles this fake reports as minimized, to drive the screenshot escalation path.</summary>
    public HashSet<long> MinimizedWindows { get; } = [];

    /// <summary>
    /// Reports every window as minimized, so a test can force the screenshot escalation path without
    /// having to know which handle the fake session happens to resolve to.
    /// </summary>
    public bool AllWindowsMinimized { get; set; }

    public void RequestForeground(long hwnd) => ForegroundRequests.Add(hwnd);

    public bool IsMinimized(long hwnd) => AllWindowsMinimized || MinimizedWindows.Contains(hwnd);

    public void Restore(long hwnd) => RestoreRequests.Add(hwnd);
}
