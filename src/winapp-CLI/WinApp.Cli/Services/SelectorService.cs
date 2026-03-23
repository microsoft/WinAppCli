// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed class SelectorService : ISelectorService
{
    public SelectorExpression Parse(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        // Semantic slug format: btn-minimize-c4b9 (lowercase, dashes, ends with 4-char hex)
        var slugParsed = SlugGenerator.ParseSlug(selector);
        if (slugParsed is not null)
        {
            return new SelectorExpression { Slug = selector };
        }

        // Everything else is a plain text search query
        return new SelectorExpression { Query = selector };
    }
}
