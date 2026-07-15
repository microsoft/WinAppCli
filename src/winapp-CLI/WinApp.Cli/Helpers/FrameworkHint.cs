// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Cheap, best-effort UI-framework detection from a window's class name. Used to scope framework-
/// specific advice (e.g. the WM_CHAR / post-message warning) to the frameworks it actually applies to,
/// instead of false-alarming on every app.
/// </summary>
internal static class FrameworkHint
{
    /// <summary>
    /// Pure class-name classifier for XAML-based windows (WinUI 3 desktop / UWP CoreWindow / XAML
    /// content island), where a posted <c>WM_CHAR</c> is dropped by the XAML input pipeline so literal
    /// typed text via <c>--via post-message</c> silently no-ops. Returns <see langword="true"/> for
    /// those class names; <see langword="false"/> for null/empty or non-XAML stacks (Win32, WPF
    /// <c>HwndWrapper*</c>, Electron <c>Chrome_WidgetWin_*</c>) which consume <c>WM_CHAR</c>. The
    /// command reads the class name through ISystemUiQuery and passes it here, keeping the heuristic
    /// unit-testable without a live window handle.
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
