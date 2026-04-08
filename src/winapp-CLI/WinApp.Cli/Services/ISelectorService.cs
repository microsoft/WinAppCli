// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Parses selector strings into structured expressions.
/// Supports semantic slugs (e.g., btn-ok-a1b2) and plain-text substring queries
/// matched against element Name and AutomationId.
/// </summary>
internal interface ISelectorService
{
    SelectorExpression Parse(string selector);
}
