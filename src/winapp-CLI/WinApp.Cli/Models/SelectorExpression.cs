// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// A parsed selector expression. Selectors target elements by ID, Name, AutomationId, Type, or text content.
/// Examples: e5, #Submit, @SearchBox, Button, Button#OK, ~partial text
/// </summary>
internal sealed record SelectorExpression
{
    /// <summary>Runtime element ID, e.g., "e5". Regex: ^e\d+$</summary>
    public string? ElementId { get; init; }

    /// <summary>Element Name, e.g., "Submit" (from #Submit selector)</summary>
    public string? Name { get; init; }

    /// <summary>AutomationId, e.g., "SearchBox" (from $SearchBox selector)</summary>
    public string? AutomationId { get; init; }

    /// <summary>Control type, e.g., "Button" (bare type selector)</summary>
    public string? Type { get; init; }

    /// <summary>Text content substring match, e.g., "bla" (from ~bla selector). Case-insensitive.</summary>
    public string? Text { get; init; }

    public bool IsElementId => ElementId is not null;
    public bool IsTextSearch => Text is not null;
}
