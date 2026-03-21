// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// Parses selector strings into structured expressions.
/// Supports: e5 (ID), #Name, @AutomationId, Type, Type#Name, Type@AutomationId.
/// </summary>
internal interface ISelectorService
{
    SelectorExpression Parse(string selector);
}
