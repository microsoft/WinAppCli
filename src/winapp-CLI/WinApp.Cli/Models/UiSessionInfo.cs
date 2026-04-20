// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Target app info resolved from --app or --window arguments.
/// Passed to UIA service methods to identify the target window.
/// </summary>
internal sealed class UiSessionInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? WindowTitle { get; set; }
    /// <summary>Specific window handle when process has multiple windows.</summary>
    public long WindowHandle { get; set; }

    /// <summary>
    /// True when the user explicitly targeted this window via <c>--window/-w</c>. When set,
    /// inspect/search/find operations must not silently expand to other top-level windows owned
    /// by the same process. See issue #472.
    /// </summary>
    public bool IsExplicitWindow { get; set; }
}
