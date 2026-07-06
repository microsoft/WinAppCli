// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Injects synthetic touch and pen input using the Windows pointer-injection APIs. Touch prefers the
/// synthetic-pointer device (<c>CreateSyntheticPointerDevice(PT_TOUCH)</c>/
/// <c>InjectSyntheticPointerInput</c>) — the same mechanism the pen path uses — and falls back to the
/// legacy <c>InitializeTouchInjection</c>/<c>InjectTouchInput</c> API when a synthetic touch device
/// cannot be created. Pen uses <c>CreateSyntheticPointerDevice(PT_PEN)</c>. Coordinates are screen
/// pixels — the same space <c>ui inspect</c> reports.
/// </summary>
internal static class PointerInput
{
    /// <summary>Maximum simultaneous touch contacts we register with the injection subsystem.</summary>
    private const uint MaxContacts = 10;

    /// <summary>Pen pressure range used by the pointer APIs (0..1024).</summary>
    private const uint PenPressureMax = 1024;

    /// <summary>Steps used to interpolate a moving gesture between two waypoints.</summary>
    private const int GlideSteps = 20;

    // touchFlags / touchMask / penFlags / penMask are raw DWORD bitmasks in the generated structs.
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint PEN_FLAG_NONE = 0x00000000;
    private const uint PEN_FLAG_ERASER = 0x00000004;
    private const uint PEN_MASK_PRESSURE = 0x00000001;
    private const uint PEN_MASK_TILT_X = 0x00000004;
    private const uint PEN_MASK_TILT_Y = 0x00000008;

    private static readonly object InitLock = new();
    private static bool _touchInitialized;

    /// <summary>Delegate that submits one frame of touch contacts (synthetic device or legacy API).</summary>
    private delegate void TouchSender(POINTER_TOUCH_INFO[] contacts);

    /// <summary>
    /// Registers this process for legacy touch injection the first time it is needed. Idempotent — the
    /// OS only allows a single successful <c>InitializeTouchInjection</c> per process. On failure the
    /// actual Win32 error is surfaced so callers can tell "unsupported" from "locked desktop".
    /// </summary>
    private static void EnsureTouchInitialized()
    {
        if (_touchInitialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_touchInitialized)
            {
                return;
            }

            // TOUCH_FEEDBACK_NONE — suppress the OS touch-visual so automation stays invisible.
            if (!PInvoke.InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_MODE.TOUCH_FEEDBACK_NONE))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"InitializeTouchInjection failed (Win32 error {err}: {Win32Message(err)}) — touch " +
                    "injection is unsupported or unavailable on this desktop. This usually means the " +
                    "device/driver does not support injected touch, or the session is locked / on a secure desktop.");
            }

            _touchInitialized = true;
        }
    }

    /// <summary>Formats a Win32 error code into its system message for honest diagnostics.</summary>
    private static string Win32Message(int error)
    {
        try { return new Win32Exception(error).Message; }
        catch { return "unknown error"; }
    }

    public static void Touch(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs)
    {
        // Primary path: a synthetic touch pointer device, mirroring the working pen path. This is the
        // modern, better-supported mechanism (Windows 10 1809+).
        var device = PInvoke.CreateSyntheticPointerDevice(
            POINTER_INPUT_TYPE.PT_TOUCH, MaxContacts, POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE);

        if (!device.IsNull)
        {
            try
            {
                RunTouchGesture(gesture, contactPaths, holdMs, durationMs,
                    contacts => SendSyntheticTouch(device, contacts));
                return;
            }
            finally
            {
                PInvoke.DestroySyntheticPointerDevice(device);
            }
        }

        int createErr = Marshal.GetLastPInvokeError();

        // Fallback path: the legacy touch-injection API. EnsureTouchInitialized surfaces an honest
        // Win32 error if even this is unsupported.
        try
        {
            EnsureTouchInitialized();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Synthetic touch injection is unsupported (CreateSyntheticPointerDevice(PT_TOUCH) failed, " +
                $"Win32 error {createErr}: {Win32Message(createErr)}). {ex.Message}");
        }

        RunTouchGesture(gesture, contactPaths, holdMs, durationMs, SendLegacyTouch);
    }

    private static void RunTouchGesture(
        TouchGesture gesture,
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        TouchSender send)
    {
        int repeats = gesture == TouchGesture.DoubleTap ? 2 : 1;
        for (int r = 0; r < repeats; r++)
        {
            InjectTouchStroke(contactPaths, holdMs, durationMs, send);
            if (r + 1 < repeats)
            {
                Thread.Sleep(60); // inter-tap gap for double-tap
            }
        }
    }

    private static void InjectTouchStroke(
        IReadOnlyList<IReadOnlyList<PointerPoint>> contactPaths,
        int holdMs,
        int durationMs,
        TouchSender send)
    {
        int count = contactPaths.Count;
        var contacts = new POINTER_TOUCH_INFO[count];

        // --- Press down ---
        for (int i = 0; i < count; i++)
        {
            var start = contactPaths[i][0];
            contacts[i] = MakeContact(
                (uint)i, start.X, start.Y,
                POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                primary: i == 0);
        }
        send(contacts);

        try
        {
            if (holdMs > 0)
            {
                Thread.Sleep(holdMs);
            }

            // --- Glide between waypoints (only if any contact has more than one waypoint) ---
            int maxWaypoints = 0;
            foreach (var path in contactPaths)
            {
                maxWaypoints = Math.Max(maxWaypoints, path.Count);
            }

            if (maxWaypoints > 1)
            {
                int perStep = Math.Max(1, durationMs / GlideSteps);
                for (int step = 1; step <= GlideSteps; step++)
                {
                    double t = step / (double)GlideSteps;
                    for (int i = 0; i < count; i++)
                    {
                        var path = contactPaths[i];
                        var (x, y) = Interpolate(path, t);
                        contacts[i] = MakeContact(
                            (uint)i, x, y,
                            POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT,
                            primary: i == 0);
                    }
                    send(contacts);
                    Thread.Sleep(perStep);
                }
            }
        }
        finally
        {
            // --- Lift (always, even if a glide frame threw, so contacts don't stay stuck down) ---
            for (int i = 0; i < count; i++)
            {
                var last = contactPaths[i][^1];
                contacts[i] = MakeContact((uint)i, last.X, last.Y, POINTER_FLAGS.POINTER_FLAG_UP, primary: i == 0);
            }
            try { send(contacts); }
            catch (InvalidOperationException) { }
        }
    }

    private static POINTER_TOUCH_INFO MakeContact(uint id, int x, int y, POINTER_FLAGS flags, bool primary)
    {
        if (primary)
        {
            flags |= POINTER_FLAGS.POINTER_FLAG_PRIMARY;
        }

        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_TOUCH,
                pointerId = id,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(x, y),
            },
            touchFlags = 0,
            touchMask = TOUCH_MASK_CONTACTAREA,
            rcContact = new RECT { left = x - 2, top = y - 2, right = x + 2, bottom = y + 2 },
        };
    }

    /// <summary>Submits one frame of touch contacts via the legacy <c>InjectTouchInput</c> API.</summary>
    private static void SendLegacyTouch(POINTER_TOUCH_INFO[] contacts)
    {
        unsafe
        {
            fixed (POINTER_TOUCH_INFO* p = contacts)
            {
                if (!PInvoke.InjectTouchInput((uint)contacts.Length, p))
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException(
                        $"InjectTouchInput failed (Win32 error {err}: {Win32Message(err)}) — touch injection " +
                        "failed or is unsupported on this desktop. The target may be elevated (run this CLI " +
                        "as administrator), the desktop may be locked, or injected touch may not be supported here.");
                }
            }
        }
    }

    /// <summary>
    /// Submits one frame of touch contacts via the synthetic-pointer device
    /// (<c>InjectSyntheticPointerInput</c>) — the modern path shared with pen injection.
    /// </summary>
    private static void SendSyntheticTouch(HSYNTHETICPOINTERDEVICE device, POINTER_TOUCH_INFO[] contacts)
    {
        var infos = new POINTER_TYPE_INFO[contacts.Length];
        for (int i = 0; i < contacts.Length; i++)
        {
            infos[i] = new POINTER_TYPE_INFO { type = POINTER_INPUT_TYPE.PT_TOUCH };
            infos[i].Anonymous.touchInfo = contacts[i];
        }

        unsafe
        {
            fixed (POINTER_TYPE_INFO* p = infos)
            {
                if (!PInvoke.InjectSyntheticPointerInput(device, p, (uint)infos.Length))
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new InvalidOperationException(
                        $"InjectSyntheticPointerInput (touch) failed (Win32 error {err}: {Win32Message(err)}) — " +
                        "touch injection failed on this desktop. The target may be elevated (run this CLI as " +
                        "administrator), or the desktop may be locked.");
                }
            }
        }
    }

    public static void Pen(
        IReadOnlyList<PointerPoint> path,
        float pressure,
        int tiltX,
        int tiltY,
        bool eraser)
    {
        var device = PInvoke.CreateSyntheticPointerDevice(POINTER_INPUT_TYPE.PT_PEN, 1, POINTER_FEEDBACK_MODE.POINTER_FEEDBACK_NONE);
        if (device.IsNull)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"CreateSyntheticPointerDevice(PT_PEN) failed (Win32 error {err}: {Win32Message(err)}) — " +
                "synthetic pen injection is unavailable on this desktop (requires Windows 10 1809+ and an " +
                "unlocked interactive session).");
        }

        try
        {
            uint mappedPressure = (uint)Math.Clamp((int)Math.Round(pressure * PenPressureMax), 0, (int)PenPressureMax);
            if (mappedPressure == 0)
            {
                mappedPressure = 1; // in-contact frames need non-zero pressure
            }

            // Down at the first point.
            var first = path[0];
            SendPen(device, first.X, first.Y, mappedPressure, tiltX, tiltY, eraser,
                POINTER_FLAGS.POINTER_FLAG_DOWN | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);

            try
            {
                // Glide through the remaining ink points.
                for (int i = 1; i < path.Count; i++)
                {
                    var pt = path[i];
                    SendPen(device, pt.X, pt.Y, mappedPressure, tiltX, tiltY, eraser,
                        POINTER_FLAGS.POINTER_FLAG_UPDATE | POINTER_FLAGS.POINTER_FLAG_INRANGE | POINTER_FLAGS.POINTER_FLAG_INCONTACT);
                    Thread.Sleep(10);
                }
            }
            finally
            {
                var last = path[^1];
                try { SendPen(device, last.X, last.Y, 0, tiltX, tiltY, eraser, POINTER_FLAGS.POINTER_FLAG_UP); }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            PInvoke.DestroySyntheticPointerDevice(device);
        }
    }

    private static void SendPen(
        HSYNTHETICPOINTERDEVICE device, int x, int y, uint pressure, int tiltX, int tiltY, bool eraser, POINTER_FLAGS flags)
    {
        var penFlags = eraser ? PEN_FLAG_ERASER : PEN_FLAG_NONE;

        var info = new POINTER_TYPE_INFO
        {
            type = POINTER_INPUT_TYPE.PT_PEN,
        };
        info.Anonymous.penInfo = new POINTER_PEN_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = POINTER_INPUT_TYPE.PT_PEN,
                pointerId = 1,
                pointerFlags = flags,
                ptPixelLocation = new System.Drawing.Point(x, y),
            },
            penFlags = penFlags,
            penMask = PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y,
            pressure = pressure,
            tiltX = tiltX,
            tiltY = tiltY,
        };

        unsafe
        {
            if (!PInvoke.InjectSyntheticPointerInput(device, &info, 1))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"InjectSyntheticPointerInput (pen) failed (Win32 error {err}: {Win32Message(err)}) — the " +
                    "target may be elevated (run this CLI as administrator) or the desktop is locked.");
            }
        }
    }

    private static (int X, int Y) Interpolate(IReadOnlyList<PointerPoint> path, double t)
    {
        if (path.Count == 1)
        {
            return (path[0].X, path[0].Y);
        }

        // Treat the path as a single straight segment from first to last waypoint.
        var a = path[0];
        var b = path[^1];
        int x = a.X + (int)Math.Round((b.X - a.X) * t);
        int y = a.Y + (int)Math.Round((b.Y - a.Y) * t);
        return (x, y);
    }
}
