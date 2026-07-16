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

    internal delegate uint SendInputHook(INPUT[] inputs);
    internal delegate void PostMessageHook(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam);

    /// <remarks>
    /// Native adapter seam for issue #630: the default body is the innermost <c>SendInput</c> OS
    /// boundary, which cannot be unit-tested without injecting real keyboard input into the desktop.
    /// Unit tests replace the delegate and cover all surrounding batching/error logic.
    /// </remarks>
    internal static SendInputHook s_sendInput = DefaultSendInput;

    /// <remarks>
    /// Native adapter seam for issue #630: the default body posts to a real HWND queue and is left as
    /// an honest native ceiling; tests inject a fake to verify exact messages and lParams.
    /// </remarks>
    internal static PostMessageHook s_postMessage = DefaultPostMessage;

    /// <remarks>
    /// Native adapter seam for issue #630: keyboard-layout mapping is provided by User32. Tests inject
    /// deterministic mappings so character batching branches are covered without depending on the
    /// machine's active layout.
    /// </remarks>
    internal static Func<char, short> s_vkKeyScan = PInvoke.VkKeyScan;

    /// <remarks>
    /// Native adapter seam for issue #630: scan-code mapping is provided by User32. Tests inject known
    /// values to cover lParam construction deterministically.
    /// </remarks>
    internal static Func<ushort, uint> s_mapVirtualKey = DefaultMapVirtualKey;

    /// <remarks>
    /// Native adapter seam for issue #630: foreground detection probes the live desktop only when
    /// formatting a native SendInput failure message. Tests inject this predicate.
    /// </remarks>
    internal static Func<bool> s_foregroundWindowIsNull = DefaultForegroundWindowIsNull;

    internal static Action<int> s_sleep = Thread.Sleep;

    private static unsafe uint DefaultSendInput(INPUT[] inputs)
    {
        fixed (INPUT* pInputs = inputs)
        {
            return PInvoke.SendInput((uint)inputs.Length, pInputs, sizeof(INPUT));
        }
    }

    private static void DefaultPostMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.PostMessage(hwnd, message, wParam, lParam);

    private static uint DefaultMapVirtualKey(ushort vk) =>
        PInvoke.MapVirtualKey(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);

    private static bool DefaultForegroundWindowIsNull() => PInvoke.GetForegroundWindow().IsNull;

    /// <summary>
    /// Restores every native seam to its production delegate. Test cleanup calls this so a faked
    /// seam never leaks into a later test that exercises real keyboard input (issue #630).
    /// </summary>
    internal static void ResetNativeSeams()
    {
        s_sendInput = DefaultSendInput;
        s_postMessage = DefaultPostMessage;
        s_vkKeyScan = PInvoke.VkKeyScan;
        s_mapVirtualKey = DefaultMapVirtualKey;
        s_foregroundWindowIsNull = DefaultForegroundWindowIsNull;
        s_sleep = Thread.Sleep;
    }

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
                        s_postMessage(hwnd, PInvoke.WM_CHAR, new WPARAM(ch), new LPARAM(1));
                    }
                    break;
            }

            s_sleep(5);
        }
    }

    private static void Post(HWND hwnd, bool isSys, bool keyUp, ushort vk, bool extended)
    {
        uint msg = isSys
            ? (keyUp ? PInvoke.WM_SYSKEYUP : PInvoke.WM_SYSKEYDOWN)
            : (keyUp ? PInvoke.WM_KEYUP : PInvoke.WM_KEYDOWN);

        var lParam = BuildKeyLParam(isSys, keyUp, extended, s_mapVirtualKey(vk));

        s_postMessage(hwnd, msg, new WPARAM(vk), new LPARAM((nint)(int)lParam));
    }

    internal static uint BuildKeyLParam(bool isSys, bool keyUp, bool extended, uint scan)
    {
        uint lParam = 1u                       // repeat count
            | (scan << 16)                     // scan code
            | (extended ? 1u << 24 : 0u)       // extended key
            | (isSys ? 1u << 29 : 0u);         // context code (ALT down)

        if (keyUp)
        {
            lParam |= (1u << 30) | (1u << 31);  // previous-state + transition-state
        }

        return lParam;
    }

    private static void SendViaSendInput(IReadOnlyList<KeyAction> actions)
    {
        var array = BuildSendInputBatch(actions, s_vkKeyScan);
        if (array.Length == 0)
        {
            return;
        }

        var sent = s_sendInput(array);
        if (sent != (uint)array.Length)
        {
            // A zero or short write can strand a key/modifier in the down state (e.g. a Ctrl-down
            // whose matching up never fired), which corrupts the whole session. Best-effort release
            // everything we pressed before surfacing the failure.
            ReleaseHeldKeys(array);

            throw new InvalidOperationException(sent == 0
                ? (s_foregroundWindowIsNull()
                    // No foreground window → the session is locked or on a secure desktop, where a
                    // user-session process can't inject. That's not an elevation/UIPI problem.
                    ? "SendInput failed — no interactive desktop is available (the session is locked " +
                      "or on a secure desktop). Unlock the session and retry."
                    : "SendInput failed — the target window may be running at a higher integrity level (elevated) " +
                      "or be an AppContainer/AppX app blocked by UIPI. Try --via post-message, or run this CLI as administrator.")
                : $"SendInput delivered only {sent} of {array.Length} key events — input was partially applied. " +
                  "Held keys were released; retry the gesture.");
        }
    }

    internal static INPUT[] BuildSendInputBatch(IReadOnlyList<KeyAction> actions, Func<char, short>? vkKeyScan = null)
    {
        vkKeyScan ??= s_vkKeyScan;
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
                        AppendCharEvents(inputs, ch, vkKeyScan);
                    }
                    break;
            }
        }

        return inputs.ToArray();
    }

    /// <summary>
    /// Best-effort release of every key pressed in <paramref name="batch"/> — emits a matching key-up for
    /// each key-down event, in reverse order, so a partial <see cref="PInvoke.SendInput"/> can't leave a
    /// modifier or key logically stuck down. Failures here are swallowed (we're already on the error path).
    /// </summary>
    internal static void ReleaseHeldKeys(INPUT[] batch)
    {
        var ups = new List<INPUT>();
        for (int i = batch.Length - 1; i >= 0; i--)
        {
            if (batch[i].type != INPUT_TYPE.INPUT_KEYBOARD)
            {
                continue;
            }

            var ki = batch[i].Anonymous.ki;
            if ((ki.dwFlags & KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP) != 0)
            {
                continue; // already an up event
            }

            ups.Add(new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous = { ki = new KEYBDINPUT
                {
                    wVk = ki.wVk,
                    wScan = ki.wScan,
                    dwFlags = ki.dwFlags | KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP
                }}
            });
        }

        if (ups.Count == 0)
        {
            return;
        }

        s_sendInput(ups.ToArray());
    }

    internal static INPUT KeyEvent(ushort vk, bool extended, bool keyUp)
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

    internal static INPUT UnicodeEvent(char ch, bool keyUp)
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
    private static void AppendCharEvents(List<INPUT> inputs, char ch, Func<char, short> vkKeyScan)
    {
        short scan = vkKeyScan(ch);
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

    internal static bool IsExtended(ushort vk) => vk is 0x5B or 0x5C or 0x5D;
}
