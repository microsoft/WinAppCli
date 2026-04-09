// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

/// <summary>
/// A parsed selector expression. Either a semantic slug (btn-minimize-c4b9) or a plain text search query.
/// </summary>
internal sealed record SelectorExpression
{
    /// <summary>Semantic slug selector, e.g., "btn-minimize-c4b9".</summary>
    public string? Slug { get; init; }

    /// <summary>Plain text search query — matches against Name and AutomationId (substring, case-insensitive).</summary>
    public string? Query { get; init; }

    public bool IsSlug => Slug is not null;
    public bool IsQuery => Query is not null;
}
