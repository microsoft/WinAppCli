// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Synthesizes keyboard input via either PostMessage (HWND-targeted) or SendInput (OS-wide).
/// </summary>
/// <remarks>
/// Known limits:
/// <list type="bullet">
/// <item><see cref="KeyTransport.PostMessage"/> posts to a window's message queue and cannot trigger
/// <c>WH_KEYBOARD_LL</c> global hotkeys (low-level hooks tap upstream of any HWND queue). Apps that read
/// raw key state via <c>GetAsyncKeyState</c> may not observe held modifiers.</item>
/// <item><see cref="KeyTransport.SendInput"/> is blocked by UIPI when injecting from an elevated process
/// into a lower-integrity (e.g., AppContainer / AppX) target.</item>
/// </list>
/// </remarks>
internal static class KeyboardInput
{
    private const ushort VkMenu = 0x12; // ALT

    public static void Send(long hwnd, IReadOnlyList<KeyAction> actions, KeyTransport transport)
    {
        switch (transport)
        {
            case KeyTransport.PostMessage:
                SendViaPostMessage(new HWND((nint)hwnd), actions);
                break;
            case KeyTransport.SendInput:
                SendViaSendInput(actions);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown key transport.");
        }
    }

    private static void SendViaPostMessage(HWND hwnd, IReadOnlyList<KeyAction> actions)
    {
        if (hwnd.IsNull)
        {
            throw new InvalidOperationException(
                "PostMessage transport requires a target window. Specify --target/-a/-w to resolve one, or use --via send-input.");
        }

        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyChord chord:
                    // ALT combos (without Ctrl) are delivered as WM_SYSKEY* so menus/accelerators fire.
                    bool sys = chord.Modifiers.Contains(VkMenu);

                    foreach (var mod in chord.Modifiers)
                    {
                        Post(hwnd, isSys: false, keyUp: false, mod, IsExtended(mod));
                    }

                    Post(hwnd, sys, keyUp: false, chord.Vk, chord.Extended);
                    Post(hwnd, sys, keyUp: true, chord.Vk, chord.Extended);

                    for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
                    {
                        Post(hwnd, isSys: false, keyUp: true, chord.Modifiers[i], IsExtended(chord.Modifiers[i]));
                    }
                    break;

                case TextInput text:
                    foreach (var ch in text.Text)
                    {
                        PInvoke.PostMessage(hwnd, PInvoke.WM_CHAR, new WPARAM(ch), new LPARAM(1));
                    }
                    break;
            }

            Thread.Sleep(5);
        }
    }

    private static void Post(HWND hwnd, bool isSys, bool keyUp, ushort vk, bool extended)
    {
        uint msg = isSys
            ? (keyUp ? PInvoke.WM_SYSKEYUP : PInvoke.WM_SYSKEYDOWN)
            : (keyUp ? PInvoke.WM_KEYUP : PInvoke.WM_KEYDOWN);

        uint scan = PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);

        uint lParam = 1u                       // repeat count
            | (scan << 16)                     // scan code
            | (extended ? 1u << 24 : 0u)       // extended key
            | (isSys ? 1u << 29 : 0u);         // context code (ALT down)

        if (keyUp)
        {
            lParam |= (1u << 30) | (1u << 31);  // previous-state + transition-state
        }

        PInvoke.PostMessage(hwnd, msg, new WPARAM(vk), new LPARAM((nint)(int)lParam));
    }

    private static unsafe void SendViaSendInput(IReadOnlyList<KeyAction> actions)
    {
        var inputs = new List<INPUT>();

        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyChord chord:
                    foreach (var mod in chord.Modifiers)
                    {
                        inputs.Add(KeyEvent(mod, IsExtended(mod), keyUp: false));
                    }

                    inputs.Add(KeyEvent(chord.Vk, chord.Extended, keyUp: false));
                    inputs.Add(KeyEvent(chord.Vk, chord.Extended, keyUp: true));

                    for (int i = chord.Modifiers.Count - 1; i >= 0; i--)
                    {
                        inputs.Add(KeyEvent(chord.Modifiers[i], IsExtended(chord.Modifiers[i]), keyUp: true));
                    }
                    break;

                case TextInput text:
                    foreach (var ch in text.Text)
                    {
                        AppendCharEvents(inputs, ch);
                    }
                    break;
            }
        }

        if (inputs.Count == 0)
        {
            return;
        }

        var array = inputs.ToArray();
        fixed (INPUT* pInputs = array)
        {
            var sent = PInvoke.SendInput((uint)array.Length, pInputs, sizeof(INPUT));
            if (sent == 0)
            {
                throw new InvalidOperationException(
                    "SendInput failed — the target window may be running at a higher integrity level (elevated) " +
                    "or be an AppContainer/AppX app blocked by UIPI. Try --via post-message, or run this CLI as administrator.");
            }
        }
    }

    private static INPUT KeyEvent(ushort vk, bool extended, bool keyUp)
    {
        var flags = (KEYBD_EVENT_FLAGS)0;
        if (keyUp) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP; }
        if (extended) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY; }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = { ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)vk, dwFlags = flags } }
        };
    }

    private static INPUT UnicodeEvent(char ch, bool keyUp)
    {
        var flags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE;
        if (keyUp) { flags |= KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP; }

        return new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = { ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = flags } }
        };
    }

    /// <summary>
    /// Appends real per-character key events for SendInput. Maps the character to a virtual key (plus Shift)
    /// via the active keyboard layout so the target sees a genuine WM_KEYDOWN (KeyDown event with the correct
    /// virtual key) and the OS composes the matching WM_CHAR (TextChanged) — i.e. per-keystroke fidelity.
    /// Characters not reachable on the current layout, or requiring Ctrl/AltGr, fall back to a Unicode packet
    /// so the exact character still lands.
    /// </summary>
    private static void AppendCharEvents(List<INPUT> inputs, char ch)
    {
        short scan = PInvoke.VkKeyScan(ch);
        int lo = scan & 0xFF;
        int hi = (scan >> 8) & 0xFF;

        bool mappable = scan != -1 && lo != 0xFF;
        bool needsCtrlOrAlt = (hi & 0x02) != 0 || (hi & 0x04) != 0; // Ctrl / Alt (AltGr) — layout-specific

        if (!mappable || needsCtrlOrAlt)
        {
            inputs.Add(UnicodeEvent(ch, keyUp: false));
            inputs.Add(UnicodeEvent(ch, keyUp: true));
            return;
        }

        var vk = (ushort)lo;
        bool needsShift = (hi & 0x01) != 0;

        if (needsShift) { inputs.Add(KeyEvent(0x10, extended: false, keyUp: false)); } // Shift down
        inputs.Add(KeyEvent(vk, extended: false, keyUp: false));
        inputs.Add(KeyEvent(vk, extended: false, keyUp: true));
        if (needsShift) { inputs.Add(KeyEvent(0x10, extended: false, keyUp: true)); }  // Shift up
    }

    private static bool IsExtended(ushort vk) => vk is 0x5B or 0x5C or 0x5D;
}
