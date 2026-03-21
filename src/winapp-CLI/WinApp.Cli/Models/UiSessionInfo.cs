// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Persisted session state for UI automation.
/// Stored at ~/.winapp/sessions/ui-session.json.
/// </summary>
internal sealed class UiSessionInfo
{
    public int ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string? WindowTitle { get; set; }
    /// <summary>The original --app value used to create this session (for window title matching).</summary>
    public string? AppQuery { get; set; }
    /// <summary>Specific window handle when process has multiple windows.</summary>
    public long WindowHandle { get; set; }
    public string? PipeName { get; set; }
    public string Mode { get; set; } = "uia";
    public DateTime ConnectedAt { get; set; }

    /// <summary>
    /// Cached element ID → stable identifiers for cross-invocation resolution.
    /// </summary>
    public Dictionary<string, CachedElement>? Elements { get; set; }
}

/// <summary>
/// Stable identifiers for re-finding an element across CLI invocations.
/// </summary>
internal sealed class CachedElement
{
    public string? AutomationId { get; set; }
    public string? Name { get; set; }
    public string Type { get; set; } = "";
    public string Path { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}
