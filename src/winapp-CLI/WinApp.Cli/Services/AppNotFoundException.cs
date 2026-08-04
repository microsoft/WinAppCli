// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Thrown by <see cref="UiSessionService"/> when no running app matches the requested
/// identifier. Allows callers to distinguish an app-not-found failure from other
/// <see cref="InvalidOperationException"/> sources such as selector-ambiguity errors
/// from <see cref="UiAutomationService"/>.
/// </summary>
internal sealed class AppNotFoundException : InvalidOperationException
{
    public AppNotFoundException(string message) : base(message) { }
}
