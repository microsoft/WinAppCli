// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Recognizes system- and shell-reserved key combinations so <c>send-keys --via send-input</c> can warn
/// before synthesizing them. These combos act on the OS/shell (lock, Start, Task Manager, close window),
/// not just the targeted app, when injected OS-wide. This is advisory only — the keys are still sent.
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
