// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Parses selector strings into structured expressions.
/// Supports semantic slugs (e.g., btn-ok-a1b2) and plain-text substring queries
/// matched against element Name and AutomationId.
/// </summary>
public interface IUiSelectorParser
{
    UiSelector Parse(string selector);
}
