// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Thrown by <see cref="UiAutomationService"/> when a caller-supplied selector does not
/// match any element in the target window's UIA tree. Distinct from a null/empty selector
/// (which records the whole window by design).
/// </summary>
internal sealed class UiElementNotFoundException(string selector)
    : Exception($"No element found matching '{selector}'.")
{
    public string Selector { get; } = selector;
}

/// <summary>
/// Thrown when a plain-text selector matches multiple elements in the UIA tree.
/// Distinct from <see cref="UiElementNotFoundException"/> (zero matches).
/// Carries the human-readable listing of matching elements with slug suggestions.
/// </summary>
internal sealed class UiAmbiguousSelectorException(string message)
    : Exception(message)
{
}
