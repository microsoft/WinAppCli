// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Resolves a UI automation session for a target application.
/// Locates the target process and window from an app identifier or HWND.
/// </summary>
internal interface IUiSessionService
{
    /// <summary>
    /// Resolve or create a session for the given app. Always requires app identifier.
    /// </summary>
    /// <param name="app">Process name, window title, or PID. Required unless hwnd is set.</param>
    /// <param name="hwnd">Direct window handle (from -w flag). Takes precedence over app.</param>
    /// <param name="restoreIfMinimized">
    /// When <see langword="true"/> (default), a minimized target window is restored so its full UI
    /// tree is realized before the caller reads or acts on it. Pass <see langword="false"/> to
    /// inspect the window as-is (honors the global <c>--allow-minimized</c> opt-out).
    /// </param>
    Task<UiSessionInfo> ResolveSessionAsync(string? app, long? hwnd, CancellationToken ct, bool restoreIfMinimized = true);
}
