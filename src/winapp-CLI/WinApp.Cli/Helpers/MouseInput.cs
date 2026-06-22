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

    public static void Click(int screenX, int screenY, bool doubleClick = false, bool rightClick = false)
    {
        // Move cursor to the target position
        PInvoke.SetCursorPos(screenX, screenY);
        Thread.Sleep(50); // small delay for cursor settle

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

    public static void Drag(int fromScreenX, int fromScreenY, int toScreenX, int toScreenY, bool rightButton = false)
    {
        var downFlag = rightButton ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN;
        var upFlag = rightButton ? MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP : MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP;

        // Settle on the start point, then press the button
        SendMove(fromScreenX, fromScreenY);
        Thread.Sleep(50);
        SendButton(downFlag);
        Thread.Sleep(50);

        // Move toward the destination in steps so the app sees a stream of WM_MOUSEMOVE messages
        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            int x = fromScreenX + (int)Math.Round((toScreenX - fromScreenX) * (i / (double)steps));
            int y = fromScreenY + (int)Math.Round((toScreenY - fromScreenY) * (i / (double)steps));
            SendMove(x, y);
            Thread.Sleep(10);
        }

        Thread.Sleep(50);
        SendButton(upFlag);
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
    /// (e.g. the target window is elevated and this process is not).
    /// </summary>
    private static void SendInputs(Span<INPUT> inputs)
    {
        unsafe
        {
            fixed (INPUT* pInputs = inputs)
            {
                var sent = PInvoke.SendInput((uint)inputs.Length, pInputs, sizeof(INPUT));
                if (sent == 0)
                {
                    throw new InvalidOperationException(
                        "SendInput failed — the target window may be elevated (running as admin). " +
                        "Try running this CLI as administrator.");
                }
            }
        }
    }
}
