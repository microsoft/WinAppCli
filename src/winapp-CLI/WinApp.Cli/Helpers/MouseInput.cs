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
