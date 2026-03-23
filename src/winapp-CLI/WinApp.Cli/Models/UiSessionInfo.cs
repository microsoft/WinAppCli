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
}
