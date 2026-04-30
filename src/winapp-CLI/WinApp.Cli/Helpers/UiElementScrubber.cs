// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Strips internal/redundant fields from <see cref="UiElement"/> trees before JSON serialization.
/// </summary>
/// <remarks>
/// JSON consumers address elements via the public <c>selector</c> slug; the synthetic walk-order
/// <c>id</c>, <c>parentSelector</c>, and <c>windowHandle</c> are implementation detail that leak
/// if not scrubbed. This helper also flattens <c>InvokableAncestor</c> down to a non-recursive
/// hint (<c>type</c>, <c>name</c>, <c>automationId</c>, <c>selector</c>, <c>isInvokable</c>) so
/// it can never form a reference cycle with its parent's children — System.Text.Json (without
/// ReferenceHandler) would otherwise throw.
/// </remarks>
internal static class UiElementScrubber
{
    /// <summary>Recursively clear internal fields on <paramref name="element"/>, its
    /// <see cref="UiElement.Children"/>, and (in flattened form) its
    /// <see cref="UiElement.InvokableAncestor"/>.</summary>
    public static void Scrub(UiElement? element)
    {
        if (element is null) { return; }
        Strip(element);
        FlattenInvokableAncestor(element);
        if (element.Children is { } kids)
        {
            foreach (var c in kids) { Scrub(c); }
        }
    }

    /// <summary>Scrub each element of a flat list (e.g., search/wait-for matches).</summary>
    public static void ScrubAll(IEnumerable<UiElement>? elements)
    {
        if (elements is null) { return; }
        foreach (var el in elements) { Scrub(el); }
    }

    private static void Strip(UiElement el)
    {
        el.Id = null;
        el.ParentSelector = null;
        el.WindowHandle = null;
    }

    private static void FlattenInvokableAncestor(UiElement el)
    {
        if (el.InvokableAncestor is not { } anc) { return; }
        // Project to a hint with no Children / nested InvokableAncestor — breaks any cycle and
        // keeps the payload small. Consumers only need a slug+label to invoke the fallback.
        el.InvokableAncestor = new UiElement
        {
            Type = anc.Type,
            Name = anc.Name,
            AutomationId = anc.AutomationId,
            Selector = anc.Selector,
            IsInvokable = anc.IsInvokable,
        };
    }
}
