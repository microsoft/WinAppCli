// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// Represents a UI element discovered via UIA or DevTools inspection.
/// Element IDs (e.g., "e5") are assigned during inspect/search and persisted in the session.
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

    /// <summary>
    /// Nearest ancestor that supports an invoke pattern (InvokePattern, TogglePattern, etc.).
    /// Populated during text content search (~selector) when the matched element itself is not invokable.
    /// </summary>
    public UiElement? InvokableAncestor { get; set; }
}
