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
    /// <param name="holdMs">Milliseconds to hold the button down at the start before moving. With
    /// <paramref name="fromScreenX"/>/<paramref name="fromScreenY"/> equal to the to-point (no movement)
    /// this performs a press-and-hold / long-press gesture.</param>
    /// <param name="dwellMs">Milliseconds to dwell at the destination after moving, before releasing —
    /// lets drop targets / merge overlays that arm from a sustained hover latch before the button-up.</param>
    void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0);

    /// <summary>
    /// Rotates the mouse wheel at the given screen position. Positive delta scrolls up/away, negative down/toward.
    /// One notch is <c>120</c> units (WHEEL_DELTA).
    /// </summary>
    void ScrollWheel(int screenX, int screenY, int delta);
}
