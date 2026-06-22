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

    /// <summary>
    /// Presses the mouse button at the from-point, moves to the to-point in steps, then releases.
    /// </summary>
    void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false);

    /// <summary>
    /// Rotates the mouse wheel at the given screen position. Positive delta scrolls up/away, negative down/toward.
    /// One notch is <c>120</c> units (WHEEL_DELTA).
    /// </summary>
    void ScrollWheel(int screenX, int screenY, int delta);
}
