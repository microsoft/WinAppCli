// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Resolves a UI automation session for a target application.
/// Locates the target process and window from an app identifier or HWND.
/// </summary>
public interface IUiTargetResolver
{
    /// <summary>
    /// Resolve or create a session for the given app. Always requires app identifier.
    /// </summary>
    /// <param name="app">Process name, window title, or PID. Required unless hwnd is set.</param>
    /// <param name="hwnd">Direct window handle (from -w flag). Takes precedence over app.</param>
    Task<UiTarget> ResolveSessionAsync(string? app, long? hwnd, CancellationToken ct);
}
