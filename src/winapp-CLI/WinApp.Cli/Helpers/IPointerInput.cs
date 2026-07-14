// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>A single point in app/screen pixel space (the same space <c>ui inspect</c> reports).</summary>
internal readonly record struct PointerPoint(int X, int Y);

/// <summary>
/// A window rectangle in screen pixels (as returned by <c>GetWindowRect</c>). Used to bounds-check
/// explicit touch/pen coordinates so a gesture can never be injected outside the target window.
/// </summary>
internal readonly record struct PointerRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Whether <paramref name="p"/> lies inside (inclusive) this rectangle.</summary>
    public bool Contains(PointerPoint p)
        => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}

/// <summary>The synthetic touch gestures supported by <c>winapp ui touch</c>.</summary>
internal enum TouchGesture
{
    Tap,
    DoubleTap,
    LongPress,
    Swipe,
    Pinch,
    Stretch,
}

/// <summary>
/// Abstraction over synthetic pointer (touch / pen) injection for testability. The real
/// implementation drives <c>InitializeTouchInjection</c>/<c>InjectTouchInput</c> (touch) and
/// <c>CreateSyntheticPointerDevice</c>/<c>InjectSyntheticPointerInput</c> (pen); fakes record the
/// injected contacts and gesture parameters so the <c>ui touch</c>/<c>ui pen</c> commands can be
/// unit-tested without a live, unlocked desktop.
/// </summary>
internal interface IPointerInput
{
    /// <summary>
    /// Injects a synthetic touch gesture. <paramref name="contactPaths"/> holds one ordered waypoint
    /// path per finger (each path has at least the contact's start point; a two-point path glides from
    /// start to end). The implementation presses all contacts down, interpolates between waypoints over
    /// <paramref name="durationMs"/>, then lifts them.
    /// </summary>
    /// <param name="gesture">Gesture kind — drives double-tap repetition and long-press semantics.</param>
    /// <param name="holdMs">Milliseconds to hold the contacts down before lifting (long-press).</param>
    /// <param name="durationMs">Glide time in milliseconds for moving gestures (swipe/pinch/stretch).</param>
    void Touch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs);

    /// <summary>
    /// Injects a synthetic pen action along <paramref name="path"/> (a single ink stroke; a one-point
    /// path is a tap). <paramref name="pressure"/> is 0..1 (mapped to the 0..1024 pen range),
    /// <paramref name="tiltX"/>/<paramref name="tiltY"/> are tilt angles in degrees,
    /// <paramref name="eraser"/> selects the eraser end of the pen, and <paramref name="durationMs"/>
    /// controls total glide time distributed across path segments (0 = ~10 ms per segment).
    /// </summary>
    void Pen(
        IReadOnlyList<PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser,
        int durationMs);
}
