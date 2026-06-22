// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinApp.Cli.Helpers;

/// <summary>Transport used to deliver synthetic keyboard input.</summary>
internal enum KeyTransport
{
    /// <summary>Posts WM_KEYDOWN/WM_KEYUP/WM_CHAR to a specific window's message queue. Bypasses UIPI.</summary>
    PostMessage,

    /// <summary>Injects OS-wide input via SendInput. Hits low-level hooks; subject to UIPI.</summary>
    SendInput,
}

/// <summary>A single parsed keyboard action.</summary>
internal abstract record KeyAction;

/// <summary>
/// A key press, optionally with held modifiers (e.g., <c>enter</c>, <c>down</c>, <c>ctrl+shift+t</c>).
/// Modifiers are pressed before and released after the main key.
/// </summary>
internal sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Vk, bool Extended) : KeyAction;

/// <summary>Literal text typed character by character (e.g., <c>hello</c>).</summary>
internal sealed record TextInput(string Text) : KeyAction;

/// <summary>
/// Parses the friendly key-string grammar used by <c>winapp ui send-keys</c> into a list of
/// <see cref="KeyAction"/>s. Tokens are whitespace separated:
/// <list type="bullet">
/// <item>Named keys: <c>down</c>, <c>enter</c>, <c>tab</c>, <c>esc</c>, <c>f5</c> …</item>
/// <item>Modifier combos: <c>ctrl+shift+t</c>, <c>alt+f4</c></item>
/// <item>Raw virtual keys: <c>vk=0x42</c> or <c>vk=66</c></item>
/// <item>Anything else is treated as literal text and typed character by character.</item>
/// </list>
/// </summary>
internal static class KeyStringParser
{
    // Modifier name -> virtual-key code.
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12, ["menu"] = 0x12,
        ["win"] = 0x5B, ["cmd"] = 0x5B, ["super"] = 0x5B, ["meta"] = 0x5B,
    };

    // Named key -> (virtual-key code, is-extended-key).
    private static readonly Dictionary<string, (ushort Vk, bool Extended)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = (0x0D, false), ["return"] = (0x0D, false),
        ["tab"] = (0x09, false),
        ["esc"] = (0x1B, false), ["escape"] = (0x1B, false),
        ["space"] = (0x20, false), ["spacebar"] = (0x20, false),
        ["backspace"] = (0x08, false), ["bksp"] = (0x08, false), ["bs"] = (0x08, false),
        ["delete"] = (0x2E, true), ["del"] = (0x2E, true),
        ["insert"] = (0x2D, true), ["ins"] = (0x2D, true),
        ["home"] = (0x24, true), ["end"] = (0x23, true),
        ["pageup"] = (0x21, true), ["pgup"] = (0x21, true),
        ["pagedown"] = (0x22, true), ["pgdn"] = (0x22, true),
        ["up"] = (0x26, true), ["down"] = (0x28, true), ["left"] = (0x25, true), ["right"] = (0x27, true),
        ["capslock"] = (0x14, false),
        ["printscreen"] = (0x2C, true), ["prtsc"] = (0x2C, true),
        ["apps"] = (0x5D, true), ["menukey"] = (0x5D, true),
        ["f1"] = (0x70, false), ["f2"] = (0x71, false), ["f3"] = (0x72, false), ["f4"] = (0x73, false),
        ["f5"] = (0x74, false), ["f6"] = (0x75, false), ["f7"] = (0x76, false), ["f8"] = (0x77, false),
        ["f9"] = (0x78, false), ["f10"] = (0x79, false), ["f11"] = (0x7A, false), ["f12"] = (0x7B, false),
        ["f13"] = (0x7C, false), ["f14"] = (0x7D, false), ["f15"] = (0x7E, false), ["f16"] = (0x7F, false),
    };

    /// <summary>
    /// Parses a key string into ordered key actions.
    /// </summary>
    /// <exception cref="FormatException">A token uses the <c>vk=</c> form with an invalid value, or an
    /// unknown modifier/key name was used inside a <c>+</c> combo.</exception>
    public static IReadOnlyList<KeyAction> Parse(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            throw new FormatException("No keys to send. Provide one or more tokens, e.g. \"ctrl+a delete\".");
        }

        var actions = new List<KeyAction>();
        var tokens = keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            // Modifier combo, e.g. ctrl+shift+t  (a bare "+" main key is supported via vk=).
            if (token.Contains('+') && token.Length > 1)
            {
                actions.Add(ParseChord(token));
                continue;
            }

            // Raw virtual key: vk=0x42 / vk=66
            if (TryParseVk(token, out var rawVk))
            {
                actions.Add(new KeyChord([], rawVk, IsExtendedVk(rawVk)));
                continue;
            }

            // Named key: down, enter, f5 …
            if (NamedKeys.TryGetValue(token, out var named))
            {
                actions.Add(new KeyChord([], named.Vk, named.Extended));
                continue;
            }

            // Otherwise literal text typed character by character.
            actions.Add(new TextInput(token));
        }

        return actions;
    }

    private static KeyChord ParseChord(string token)
    {
        var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new FormatException($"Invalid key combo '{token}'. Use the form modifier+key, e.g. ctrl+shift+t.");
        }

        var modifiers = new List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!Modifiers.TryGetValue(parts[i], out var modVk))
            {
                throw new FormatException(
                    $"Unknown modifier '{parts[i]}' in '{token}'. Valid modifiers: ctrl, shift, alt, win.");
            }
            modifiers.Add(modVk);
        }

        var mainKey = parts[^1];
        if (!TryResolveKey(mainKey, out var vk, out var extended))
        {
            throw new FormatException(
                $"Unknown key '{mainKey}' in '{token}'. Use a named key (enter, down, f5), a single character, or vk=0xNN.");
        }

        return new KeyChord(modifiers, vk, extended);
    }

    private static bool TryResolveKey(string name, out ushort vk, out bool extended)
    {
        if (TryParseVk(name, out vk))
        {
            extended = IsExtendedVk(vk);
            return true;
        }

        if (NamedKeys.TryGetValue(name, out var named))
        {
            vk = named.Vk;
            extended = named.Extended;
            return true;
        }

        // Single character: map to its virtual key via the active keyboard layout.
        if (name.Length == 1)
        {
            short scan = Windows.Win32.PInvoke.VkKeyScan(name[0]);
            if (scan != -1)
            {
                vk = (ushort)(scan & 0xFF);
                extended = false;
                return true;
            }
        }

        extended = false;
        return false;
    }

    private static bool TryParseVk(string token, out ushort vk)
    {
        vk = 0;
        if (!token.StartsWith("vk=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = token[3..];
        bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk)
            : ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out vk);

        if (!parsed || vk == 0 || vk > 0xFF)
        {
            throw new FormatException($"Invalid virtual key '{token}'. Use vk=0xNN or vk=NN with a value between 1 and 255.");
        }

        return true;
    }

    /// <summary>Virtual keys that require the extended-key flag for correct delivery.</summary>
    private static bool IsExtendedVk(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or // PgUp PgDn End Home
        0x25 or 0x26 or 0x27 or 0x28 or // arrows
        0x2D or 0x2E or                 // Insert Delete
        0x2C or                         // PrintScreen
        0x5B or 0x5C or 0x5D or         // LWin RWin Apps
        0x90;                           // NumLock
}
