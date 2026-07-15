// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Recognizes system- and shell-reserved key combinations so <c>send-keys --via send-input</c> can refuse
/// to synthesize them. These combos act on the OS/shell (lock, Start, Task Manager, close window), not just
/// the targeted app, when injected OS-wide — so send-input rejects them rather than acting beyond the target.
/// (<c>--via post-message</c> is window-scoped and is not affected.)
/// </summary>
internal static class SystemKeyGuard
{
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkEsc = 0x1B;
    private const ushort VkTab = 0x09;
    private const ushort VkDelete = 0x2E;
    private const ushort VkPrintScreen = 0x2C;
    private const ushort VkF4 = 0x73;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkL = 0x4C; // 'L' — win+l triggers LockWorkStation() via the shell hook

    /// <summary>
    /// Returns the friendly names of any combos that must NEVER be synthesized via send-input, even when
    /// the caller opts in with <c>--allow-system-keys</c>. Currently scoped to <c>win+l</c> only:
    /// when VK_LWIN/VK_RWIN + VK_L (0x4C) is injected OS-wide the shell hook fires
    /// <c>LockWorkStation()</c>, locking the interactive session immediately with no recovery path from
    /// automation. Unlike soft-blocked combos (<c>alt+f4</c>, <c>ctrl+shift+esc</c>, <c>win+r</c>, …)
    /// that can be opted into for driving global hotkeys, a session lock is unrecoverable from code and
    /// breaks CI and remote-desktop sessions irreversibly. (alt+f4, ctrl+shift+esc, win+r, etc. are
    /// intentionally left as "soft" blocks — callers may legitimately need them for global hotkey testing.)
    /// </summary>
    public static IReadOnlyList<string> FindNeverBypassableCombos(IEnumerable<KeyAction> actions)
    {
        var hits = new List<string>();
        foreach (var action in actions)
        {
            if (action is not KeyChord chord)
            {
                continue;
            }

            bool win = chord.Modifiers.Contains(VkLWin) || chord.Modifiers.Contains(VkRWin);
            if (win && chord.Vk == VkL)
            {
                const string name = "win+l";
                if (!hits.Contains(name))
                {
                    hits.Add(name);
                }
            }
        }

        return hits;
    }

    /// <summary>
    /// Returns the friendly names of any system-reserved combos present in <paramref name="actions"/>,
    /// in first-seen order with duplicates removed. Empty when none are present.
    /// </summary>
    public static IReadOnlyList<string> FindSystemCombos(IEnumerable<KeyAction> actions)
    {
        var hits = new List<string>();
        foreach (var action in actions)
        {
            if (action is not KeyChord chord)
            {
                continue;
            }

            var name = Describe(chord);
            if (name is not null && !hits.Contains(name))
            {
                hits.Add(name);
            }
        }

        return hits;
    }

    private static string? Describe(KeyChord chord)
    {
        bool ctrl = chord.Modifiers.Contains(VkControl);
        bool shift = chord.Modifiers.Contains(VkShift);
        bool alt = chord.Modifiers.Contains(VkAlt);
        bool win = chord.Modifiers.Contains(VkLWin) || chord.Modifiers.Contains(VkRWin);

        // Any Win-modified combo is a shell shortcut (win+l lock, win+r Run, win+d desktop, win+e, …).
        if (win)
        {
            return "win+<key>";
        }

        if (chord.Modifiers.Count == 0)
        {
            // Lone Win key opens Start; lone PrintScreen captures the screen.
            if (chord.Vk is VkLWin or VkRWin)
            {
                return "win";
            }

            if (chord.Vk == VkPrintScreen)
            {
                return "printscreen";
            }

            return null;
        }

        // Alt+PrintScreen captures the active window to the clipboard.
        if (alt && chord.Vk == VkPrintScreen)
        {
            return "alt+printscreen";
        }

        if (ctrl && shift && chord.Vk == VkEsc)
        {
            return "ctrl+shift+esc";
        }

        if (ctrl && alt && chord.Vk == VkDelete)
        {
            return "ctrl+alt+del";
        }

        if (ctrl && !alt && !shift && chord.Vk == VkEsc)
        {
            return "ctrl+esc";
        }

        if (alt && chord.Vk == VkTab)
        {
            return "alt+tab";
        }

        if (alt && chord.Vk == VkEsc)
        {
            return "alt+esc";
        }

        if (alt && chord.Vk == VkF4)
        {
            return "alt+f4";
        }

        return null;
    }
}
