// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Thrown by <see cref="UiTargetResolver"/> when no running app matches the requested
/// identifier. Allows callers to distinguish an app-not-found failure from other
/// <see cref="InvalidOperationException"/> sources such as selector-ambiguity errors
/// from <see cref="UiAutomationService"/>.
/// </summary>
public sealed class AppNotFoundException : InvalidOperationException
{
    public AppNotFoundException(string message) : base(message) { }
}
