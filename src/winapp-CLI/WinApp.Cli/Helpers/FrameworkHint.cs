// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Cheap, best-effort UI-framework detection from a window's class name. Used to scope framework-
/// specific advice (e.g. the WM_CHAR / post-message warning) to the frameworks it actually applies to,
/// instead of false-alarming on every app.
/// </summary>
internal static class FrameworkHint
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="hwnd"/> looks like a XAML-based window
    /// (WinUI 3 desktop, UWP/CoreWindow, or a XAML content island), where a posted <c>WM_CHAR</c> is
    /// dropped by the XAML input pipeline and literal typed text via <c>--via post-message</c> silently
    /// no-ops. Non-XAML stacks (Win32/WinForms/WPF edit controls, Electron/Chromium) consume
    /// <c>WM_CHAR</c>, so they should not be warned. Unknown/undeterminable windows return
    /// <see langword="false"/> (don't warn) since post-message text works for the non-XAML majority.
    /// </summary>
    public static bool IsLikelyXaml(long hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        return IsXamlClassName(UiSessionService.GetWindowClassName((nint)hwnd));
    }

    /// <summary>
    /// Pure class-name classifier behind <see cref="IsLikelyXaml(long)"/>, split out so the framework
    /// heuristic can be unit-tested without a live window handle. Returns <see langword="true"/> for
    /// WinUI 3 desktop / UWP CoreWindow / XAML-island class names; <see langword="false"/> for
    /// null/empty or non-XAML stacks (Win32, WPF <c>HwndWrapper*</c>, Electron <c>Chrome_WidgetWin_*</c>).
    /// </summary>
    internal static bool IsXamlClassName(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        // WinUI 3 desktop top-level: "WinUIDesktopWin32WindowClass". UWP: "Windows.UI.Core.CoreWindow".
        // XAML islands: "Microsoft.UI.Content.*" / "Windows.UI.Composition.DesktopWindowContentBridge".
        return className.Contains("WinUI", StringComparison.OrdinalIgnoreCase)
            || className.Contains("Xaml", StringComparison.OrdinalIgnoreCase)
            || className.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
            || className.StartsWith("Microsoft.UI.Content", StringComparison.OrdinalIgnoreCase)
            || className.Contains("DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase);
    }
}
