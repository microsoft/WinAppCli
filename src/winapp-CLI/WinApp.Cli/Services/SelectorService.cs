// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

internal sealed partial class SelectorService : ISelectorService
{
    [GeneratedRegex(@"^e\d+$")]
    private static partial Regex ElementIdPattern();

    public SelectorExpression Parse(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        // e5, e12, e0 — runtime element ID
        if (ElementIdPattern().IsMatch(selector))
        {
            return new SelectorExpression { ElementId = selector };
        }

        // #Submit — Name selector
        if (selector.StartsWith('#'))
        {
            return new SelectorExpression { Name = selector[1..] };
        }

        // $SearchBox — AutomationId selector
        if (selector.StartsWith('$'))
        {
            return new SelectorExpression { AutomationId = selector[1..] };
        }

        // Button#OK — Type + Name
        var hashIndex = selector.IndexOf('#');
        if (hashIndex > 0)
        {
            return new SelectorExpression
            {
                Type = selector[..hashIndex],
                Name = selector[(hashIndex + 1)..]
            };
        }

        // TextBox$Search — Type + AutomationId
        var dollarIndex = selector.IndexOf('$');
        if (dollarIndex > 0)
        {
            return new SelectorExpression
            {
                Type = selector[..dollarIndex],
                AutomationId = selector[(dollarIndex + 1)..]
            };
        }

        // Button — bare type selector
        return new SelectorExpression { Type = selector };
    }
}
