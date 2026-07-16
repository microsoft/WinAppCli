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

    /// <summary>
    /// Number of characters injected per <c>SendInput</c> call for literal typed text. Long text sent as
    /// one unbroken burst overruns the target thread's input queue, which silently drops characters even
    /// though <c>SendInput</c> reports success (issue #657). Splitting the text into small chunks paced by
    /// <see cref="s_chunkDelayMs"/> lets the target drain its queue between bursts so every character lands.
    /// </summary>
    internal const int DefaultTextChunkChars = 16;

    /// <summary>
    /// Pause (ms) between injected chunks, giving the target time to pump its input queue. Chosen so the
    /// injection rate (<see cref="DefaultTextChunkChars"/> chars per delay) stays comfortably below the
    /// rate a target drains its input queue, with margin at both the coarse (~15.6&#160;ms) and fine
    /// (~1&#160;ms) Windows timer resolutions <c>Thread.Sleep</c> may run at.
    /// </summary>
    internal const int DefaultChunkDelayMs = 15;

    /// <remarks>Overridable seam so tests can drive the chunking branches deterministically.</remarks>
    internal static int s_textChunkChars = DefaultTextChunkChars;

    /// <remarks>Overridable seam so tests can drive the throttle branches deterministically.</remarks>
    internal static int s_chunkDelayMs = DefaultChunkDelayMs;

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
        s_textChunkChars = DefaultTextChunkChars;
        s_chunkDelayMs = DefaultChunkDelayMs;
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
        // A single SendInput call carrying the whole payload overruns the target thread's input queue,
        // which silently drops characters even though SendInput returns success (issue #657). Split the
        // work into small self-contained segments (each chord, and text in chunks of s_textChunkChars)
        // and pace them so the target can drain its queue between bursts.
        var segments = BuildSendInputSegments(actions, s_vkKeyScan);

        for (int i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            var sent = s_sendInput(segment);
            if (sent != (uint)segment.Length)
            {
                // A zero or short write can strand a key/modifier in the down state (e.g. a Shift-down
                // whose matching up never fired), which corrupts the whole session. Every already-sent
                // segment is balanced (its downs and ups are contained within it), so only this failing
                // segment can leave a key held — best-effort release it before surfacing the failure.
                ReleaseHeldKeys(segment);
                throw BuildSendInputFailure(sent, segment.Length);
            }

            // Only sleep *between* segments, so a short payload that fits in one segment adds no latency.
            if (i < segments.Count - 1)
            {
                s_sleep(s_chunkDelayMs);
            }
        }
    }

    /// <summary>
    /// Builds the SendInput failure surfaced when a chunk is only partially injected. A zero write means
    /// the injection was refused outright (no interactive desktop, or UIPI/integrity mismatch); a short
    /// write means input was partially applied and the held keys were already released.
    /// </summary>
    private static InvalidOperationException BuildSendInputFailure(uint sent, int expected) =>
        new(sent == 0
            ? (s_foregroundWindowIsNull()
                // No foreground window → the session is locked or on a secure desktop, where a
                // user-session process can't inject. That's not an elevation/UIPI problem.
                ? "SendInput failed — no interactive desktop is available (the session is locked " +
                  "or on a secure desktop). Unlock the session and retry."
                : "SendInput failed — the target window may be running at a higher integrity level (elevated) " +
                  "or be an AppContainer/AppX app blocked by UIPI. Try --via post-message, or run this CLI as administrator.")
            : $"SendInput delivered only {sent} of {expected} key events — input was partially applied. " +
              "Held keys were released; retry the gesture.");

    /// <summary>
    /// Flattens key actions into ordered, self-contained SendInput segments for throttled delivery: one
    /// segment per chord, and literal text split into chunks of <see cref="s_textChunkChars"/> characters.
    /// Each segment carries balanced key-down/key-up pairs so pacing (or a failure) between segments can
    /// never strand a modifier. See <see cref="BuildSendInputBatch"/> for the un-chunked equivalent.
    /// </summary>
    internal static List<INPUT[]> BuildSendInputSegments(IReadOnlyList<KeyAction> actions, Func<char, short>? vkKeyScan = null)
    {
        vkKeyScan ??= s_vkKeyScan;
        int chunkChars = Math.Max(1, s_textChunkChars);
        var segments = new List<INPUT[]>();

        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyChord chord:
                    var chordEvents = new List<INPUT>();
                    AppendChordEvents(chordEvents, chord);
                    if (chordEvents.Count > 0)
                    {
                        segments.Add(chordEvents.ToArray());
                    }
                    break;

                case TextInput text:
                    for (int start = 0; start < text.Text.Length; start += chunkChars)
                    {
                        int end = Math.Min(start + chunkChars, text.Text.Length);
                        var chunk = new List<INPUT>();
                        for (int j = start; j < end; j++)
                        {
                            AppendCharEvents(chunk, text.Text[j], vkKeyScan);
                        }

                        if (chunk.Count > 0)
                        {
                            segments.Add(chunk.ToArray());
                        }
                    }
                    break;
            }
        }

        return segments;
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
                    AppendChordEvents(inputs, chord);
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
    /// Appends a chord's events in order: each modifier down, the main key down then up, then each
    /// modifier up in reverse — so the modifiers wrap the key press symmetrically.
    /// </summary>
    private static void AppendChordEvents(List<INPUT> inputs, KeyChord chord)
    {
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
