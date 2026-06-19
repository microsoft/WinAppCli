// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Abstraction over mouse input for testability.
/// Real implementation calls SendInput P/Invoke; fakes record coordinates.
/// </summary>
internal interface IMouseInput
{
    /// <summary>
    /// Moves the cursor to the target with a wiggle to trigger hover/tooltip detection.
    /// </summary>
    void Hover(int screenX, int screenY);

    /// <summary>
    /// Clicks at screen coordinates.
    /// </summary>
    void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false);
}
