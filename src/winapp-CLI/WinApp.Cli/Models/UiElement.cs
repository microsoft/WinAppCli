// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Represents a UI element discovered via UIA or DevTools inspection.
/// </summary>
internal sealed class UiElement
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string? AutomationId { get; set; }
    public string? ClassName { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsOffscreen { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public UiElement[]? Children { get; set; }

    /// <summary>Stable semantic slug that uniquely identifies this element (e.g., "btn-minimize-d1a0").</summary>
    public string? Selector { get; set; }

    /// <summary>Current text value for editable elements (from ValuePattern). Null if not editable or empty.</summary>
    public string? Value { get; set; }

    /// <summary>Toggle state for checkboxes/toggles: "on", "off", "indeterminate". Null if not a toggle.</summary>
    public string? ToggleState { get; set; }

    /// <summary>Expand/collapse state: "expanded", "collapsed". Null if not expandable.</summary>
    public string? ExpandState { get; set; }

    /// <summary>Scroll capability: "v" (vertical), "h" (horizontal), "vh" (both). Null if not scrollable.</summary>
    public string? ScrollDir { get; set; }

    /// <summary>Depth in the tree (0 = root). Used for display indentation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Depth { get; set; }

    /// <summary>
    /// Nearest ancestor that supports an invoke pattern (InvokePattern, TogglePattern, etc.).
    /// Populated during search when the matched element itself is not invokable.
    /// </summary>
    public UiElement? InvokableAncestor { get; set; }
}
