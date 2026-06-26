// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Simulates mouse clicks at screen coordinates using SendInput.
/// </summary>
internal static class MouseInput
{
    /// <summary>
    /// Moves the cursor to the target position with a small wiggle to trigger hover/tooltip detection.
    /// Uses SendInput MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE to ensure apps see real WM_MOUSEMOVE messages.
    /// </summary>
    public static void Hover(int screenX, int screenY)
    {
        // Move to target via SendInput (absolute coordinates)
        SendMove(screenX, screenY);
        Thread.Sleep(30);

        // Small wiggle (±2px) to ensure the app registers mouse entry
        SendMove(screenX + 2, screenY);
        Thread.Sleep(20);
        SendMove(screenX, screenY + 2);
        Thread.Sleep(20);

        // Return to center and stop — dwell timer starts now
        SendMove(screenX, screenY);
    }

    /// <summary>
    /// Moves the cursor to the target position (no button action). Lets a caller position the pointer
    /// and run its own settle + final confirm read before issuing the button-down via
    /// <see cref="Click"/> with <c>settleMs: 0</c>, closing the settle-window race.
    /// </summary>
    public static void MoveCursor(int screenX, int screenY)
    {
        PInvoke.SetCursorPos(screenX, screenY);
    }

    public static void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false, int settleMs = 50)
    {
        // Move cursor to the target position
        PInvoke.SetCursorPos(screenX, screenY);
        if (settleMs > 0)
        {
            Thread.Sleep(settleMs); // small delay for cursor settle
        }

        // Build input events
        var downFlag = rightClick ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN;
        var upFlag = rightClick ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP;

        // Single click
        SendClick(downFlag, upFlag);

        if (doubleClick)
        {
            Thread.Sleep(50); // inter-click delay
            SendClick(downFlag, upFlag);
        }
    }

    public static void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false, int holdMs = 0, int dwellMs = 0)
    {
        var downFlag = rightButton ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN;
        var upFlag = rightButton ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP;

        // Settle on the start point, then press the button
        SendMove(fromScreenX, fromScreenY);
        Thread.Sleep(50);
        SendButton(downFlag);

        var released = false;
        try
        {
            Thread.Sleep(50);

            // Optional press-and-hold at the start before moving: drives long-press / press-and-hold
            // detection. With from == to (no movement) this is a pure long-press gesture.
            if (holdMs > 0)
            {
                Thread.Sleep(holdMs);
            }

            // Move toward the destination in steps so the app sees a stream of WM_MOUSEMOVE messages
            const int steps = 20;
            for (int i = 1; i <= steps; i++)
            {
                int x = fromScreenX + (int)Math.Round((toScreenX - fromScreenX) * (i / (double)steps));
                int y = fromScreenY + (int)Math.Round((toScreenY - fromScreenY) * (i / (double)steps));
                SendMove(x, y);
                Thread.Sleep(10);
            }

            // Optional dwell at the destination before releasing, so drop targets / merge overlays
            // that arm from a sustained hover (rather than the instant the cursor arrives) can latch.
            if (dwellMs > 0)
            {
                Thread.Sleep(dwellMs);
            }

            Thread.Sleep(50);
            SendButton(upFlag);
            released = true;
        }
        finally
        {
            // If a move or the up-event threw, make sure the button doesn't stay logically held down
            // (which would wreck the user's whole session). Best-effort — swallow a secondary failure.
            if (!released)
            {
                try { SendButton(upFlag); }
                catch (InvalidOperationException) { }
            }
        }
    }

    public static void ScrollWheel(int screenX, int screenY, int delta)
    {
        // Position the cursor over the target so the wheel message is routed to the element under it
        SendMove(screenX, screenY);
        Thread.Sleep(30);

        Span<INPUT> inputs =
        [
            new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = { mi = new MOUSEINPUT
                {
                    mouseData = unchecked((uint)delta),
                    dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL
                }}
            }
        ];

        SendInputs(inputs);
    }

    private static void SendButton(MOUSE_EVENT_FLAGS flag)
    {
        Span<INPUT> inputs =
        [
            new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = { mi = new MOUSEINPUT { dwFlags = flag } }
            }
        ];

        SendInputs(inputs);
    }

    private static void SendMove(int screenX, int screenY)
    {
        // Normalize against the full virtual desktop to support multi-monitor setups
        int vx = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int vy = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int vw = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int vh = PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);

        int absoluteX = Math.Clamp((int)Math.Round(((screenX - vx) * 65535.0) / Math.Max(vw - 1, 1)), 0, 65535);
        int absoluteY = Math.Clamp((int)Math.Round(((screenY - vy) * 65535.0) / Math.Max(vh - 1, 1)), 0, 65535);

        Span<INPUT> inputs =
        [
            new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = { mi = new MOUSEINPUT
                {
                    dx = absoluteX,
                    dy = absoluteY,
                    dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK
                }}
            }
        ];

        SendInputs(inputs);
    }

    private static void SendClick(MOUSE_EVENT_FLAGS downFlag, MOUSE_EVENT_FLAGS upFlag)
    {
        Span<INPUT> inputs =
        [
            new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = { mi = new MOUSEINPUT { dwFlags = downFlag } }
            },
            new INPUT
            {
                type = INPUT_TYPE.INPUT_MOUSE,
                Anonymous = { mi = new MOUSEINPUT { dwFlags = upFlag } }
            }
        ];

        SendInputs(inputs);
    }

    /// <summary>
    /// Dispatches the given input events via SendInput, throwing if the OS rejects them
    /// (e.g. the session is locked, or the target window is elevated and this process is not).
    /// </summary>
    private static void SendInputs(Span<INPUT> inputs)
    {
        unsafe
        {
            fixed (INPUT* pInputs = inputs)
            {
                var sent = PInvoke.SendInput((uint)inputs.Length, pInputs, sizeof(INPUT));
                if (sent != (uint)inputs.Length)
                {
                    throw new InvalidOperationException(sent == 0
                        ? (PInvoke.GetForegroundWindow().IsNull
                            // No foreground window at all → the workstation is locked or on a secure
                            // desktop (LogonUI/UAC), where a user-session process simply can't inject.
                            // Don't blame elevation in that case.
                            ? "SendInput failed — no interactive desktop is available (the session is locked " +
                              "or on a secure desktop). Unlock the session and retry."
                            : "SendInput failed — the target window may be elevated (running as admin). " +
                              "Try running this CLI as administrator.")
                        : $"SendInput delivered only {sent} of {inputs.Length} mouse events — the gesture was " +
                          "partially applied.");
                }
            }
        }
    }
}
