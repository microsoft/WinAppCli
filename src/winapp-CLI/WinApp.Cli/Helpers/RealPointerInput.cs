// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Production implementation — delegates to the <see cref="PointerInput"/> static P/Invoke helpers
/// that drive the Windows touch- and pen-injection APIs.
/// </summary>
internal class RealPointerInput : IPointerInput
{
    public void Touch(TouchGesture gesture, IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths, int holdMs, int durationMs)
        => PointerInput.Touch(gesture, contactPaths, holdMs, durationMs);

    public void Pen(IReadOnlyList<PointerPoint> path, float pressure, int tiltX, int tiltY, bool eraser, int durationMs)
        => PointerInput.Pen(path, pressure, tiltX, tiltY, eraser, durationMs);
}
