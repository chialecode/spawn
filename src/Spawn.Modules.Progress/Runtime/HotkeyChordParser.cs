using System.Globalization;

namespace Spawn.Modules.Progress.Runtime;

/// <summary>
/// Parsed virtual-key chord ready to be sent to <c>Spawn.TrainerWorker</c> via <c>trainer.triggerHotkey</c>.
/// </summary>
public sealed record ParsedHotkeyChord(
    ushort VirtualKey,
    IReadOnlyList<ushort> Modifiers,
    bool IsExtendedKeyOverride);

/// <summary>
/// Parses human-readable trainer hotkey chord strings (e.g. <c>"Num 1"</c>, <c>"Ctrl+Num /"</c>, <c>"Alt+Num Enter"</c>,
/// <c>"Ctrl+Num +"</c>, <c>"Shift+Home"</c>, <c>"F1"</c>) into Windows virtual-key codes and extended-key hints.
/// </summary>
public static class HotkeyChordParser
{
    public const ushort VkControl = 0x11;
    public const ushort VkMenuAlt = 0x12;
    public const ushort VkShift = 0x10;
    public const ushort VkLWin = 0x5B;
    public const ushort VkReturn = 0x0D;

    private static readonly Dictionary<string, (ushort Vk, bool ExtendedOverride)> KeyTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // Numpad
        ["Num 0"] = (0x60, false), ["Num0"] = (0x60, false), ["Numpad 0"] = (0x60, false), ["Numpad0"] = (0x60, false),
        ["Num 1"] = (0x61, false), ["Num1"] = (0x61, false), ["Numpad 1"] = (0x61, false), ["Numpad1"] = (0x61, false),
        ["Num 2"] = (0x62, false), ["Num2"] = (0x62, false), ["Numpad 2"] = (0x62, false), ["Numpad2"] = (0x62, false),
        ["Num 3"] = (0x63, false), ["Num3"] = (0x63, false), ["Numpad 3"] = (0x63, false), ["Numpad3"] = (0x63, false),
        ["Num 4"] = (0x64, false), ["Num4"] = (0x64, false), ["Numpad 4"] = (0x64, false), ["Numpad4"] = (0x64, false),
        ["Num 5"] = (0x65, false), ["Num5"] = (0x65, false), ["Numpad 5"] = (0x65, false), ["Numpad5"] = (0x65, false),
        ["Num 6"] = (0x66, false), ["Num6"] = (0x66, false), ["Numpad 6"] = (0x66, false), ["Numpad6"] = (0x66, false),
        ["Num 7"] = (0x67, false), ["Num7"] = (0x67, false), ["Numpad 7"] = (0x67, false), ["Numpad7"] = (0x67, false),
        ["Num 8"] = (0x68, false), ["Num8"] = (0x68, false), ["Numpad 8"] = (0x68, false), ["Numpad8"] = (0x68, false),
        ["Num 9"] = (0x69, false), ["Num9"] = (0x69, false), ["Numpad 9"] = (0x69, false), ["Numpad9"] = (0x69, false),
        ["Num *"] = (0x6A, false), ["Num*"] = (0x6A, false), ["Num Multiply"] = (0x6A, false), ["Numpad *"] = (0x6A, false), ["Multiply"] = (0x6A, false),
        ["Num +"] = (0x6B, false), ["Num+"] = (0x6B, false), ["Num Plus"] = (0x6B, false), ["Num Add"] = (0x6B, false), ["Numpad +"] = (0x6B, false), ["Add"] = (0x6B, false),
        ["Num -"] = (0x6D, false), ["Num-"] = (0x6D, false), ["Num Minus"] = (0x6D, false), ["Num Subtract"] = (0x6D, false), ["Numpad -"] = (0x6D, false), ["Subtract"] = (0x6D, false),
        ["Num ."] = (0x6E, false), ["Num."] = (0x6E, false), ["Num Dot"] = (0x6E, false), ["Num Decimal"] = (0x6E, false), ["Numpad ."] = (0x6E, false), ["Decimal"] = (0x6E, false),
        ["Num /"] = (0x6F, false), ["Num/"] = (0x6F, false), ["Num Divide"] = (0x6F, false), ["Numpad /"] = (0x6F, false), ["Divide"] = (0x6F, false),
        ["Num Enter"] = (VkReturn, true), ["NumEnter"] = (VkReturn, true), ["Numpad Enter"] = (VkReturn, true), ["NumpadEnter"] = (VkReturn, true),

        // Navigation & editing
        ["Home"] = (0x24, false),
        ["End"] = (0x23, false),
        ["Insert"] = (0x2D, false), ["Ins"] = (0x2D, false),
        ["Delete"] = (0x2E, false), ["Del"] = (0x2E, false),
        ["Page Up"] = (0x21, false), ["PageUp"] = (0x21, false), ["PgUp"] = (0x21, false),
        ["Page Down"] = (0x22, false), ["PageDown"] = (0x22, false), ["PgDn"] = (0x22, false),
        ["Up"] = (0x26, false), ["Up Arrow"] = (0x26, false),
        ["Down"] = (0x28, false), ["Down Arrow"] = (0x28, false),
        ["Left"] = (0x25, false), ["Left Arrow"] = (0x25, false),
        ["Right"] = (0x27, false), ["Right Arrow"] = (0x27, false),

        // Standard control keys
        ["Enter"] = (VkReturn, false), ["Return"] = (VkReturn, false),
        ["Space"] = (0x20, false), ["Spacebar"] = (0x20, false),
        ["Tab"] = (0x09, false),
        ["Esc"] = (0x1B, false), ["Escape"] = (0x1B, false),
        ["Backspace"] = (0x08, false), ["Back"] = (0x08, false),
        ["NumLock"] = (0x90, false), ["Num Lock"] = (0x90, false),
        ["CapsLock"] = (0x14, false), ["Caps Lock"] = (0x14, false),
        ["ScrollLock"] = (0x91, false), ["Scroll Lock"] = (0x91, false),
        ["Pause"] = (0x13, false),

        // OEM keys
        ["-"] = (0xBD, false), ["Minus"] = (0xBD, false),
        ["="] = (0xBB, false), ["Equals"] = (0xBB, false), ["Plus"] = (0xBB, false),
        ["["] = (0xDB, false), ["]"] = (0xDD, false),
        [";"] = (0xBA, false), ["'"] = (0xDE, false),
        [","] = (0xBC, false), ["."] = (0xBE, false),
        ["/"] = (0xBF, false), ["\\"] = (0xDC, false),
        ["`"] = (0xC0, false), ["~"] = (0xC0, false),
    };

    public static bool TryParse(string? chordText, out ParsedHotkeyChord? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(chordText))
        {
            return false;
        }

        string trimmed = chordText.Trim();
        List<string> tokens = TokenizeChord(trimmed);
        if (tokens.Count == 0)
        {
            return false;
        }

        var modifiers = new List<ushort>(3);
        string? primaryToken = null;

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            bool isLast = i == tokens.Count - 1;

            if (!isLast && TryParseModifier(token, out ushort modVk))
            {
                if (!modifiers.Contains(modVk))
                {
                    modifiers.Add(modVk);
                }

                continue;
            }

            if (isLast)
            {
                primaryToken = token;
            }
            else
            {
                // Multi-word primary key split unexpectedly? Rejoin remaining tokens as primary key.
                primaryToken = string.Join("+", tokens.Skip(i));
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(primaryToken))
        {
            return false;
        }

        if (!TryParsePrimaryKey(primaryToken, out ushort primaryVk, out bool isExtendedOverride))
        {
            return false;
        }

        parsed = new ParsedHotkeyChord(primaryVk, modifiers, isExtendedOverride);
        return true;
    }

    private static List<string> TokenizeChord(string chord)
    {
        var tokens = new List<string>(4);
        int start = 0;
        int len = chord.Length;

        for (int i = 0; i < len; i++)
        {
            if (chord[i] != '+')
            {
                continue;
            }

            string before = chord[start..i].Trim();
            // Handle "Num +" or "Ctrl+Num +" where '+' is part of the key name rather than a separator.
            if (before.Equals("Num", StringComparison.OrdinalIgnoreCase)
                || before.Equals("Numpad", StringComparison.OrdinalIgnoreCase))
            {
                // Keep consuming '+' as part of "Num +" token.
                continue;
            }

            if (before.Length > 0)
            {
                tokens.Add(before);
                start = i + 1;
            }
            else
            {
                // Leading or doubled '+' (e.g. "Ctrl++") represents the '+' key itself.
                tokens.Add("+");
                start = i + 1;
            }
        }

        if (start < len)
        {
            string tail = chord[start..].Trim();
            if (tail.Length > 0)
            {
                tokens.Add(tail);
            }
        }

        return tokens;
    }

    private static bool TryParseModifier(string token, out ushort modifierVk)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                modifierVk = VkControl;
                return true;
            case "ALT":
            case "MENU":
                modifierVk = VkMenuAlt;
                return true;
            case "SHIFT":
                modifierVk = VkShift;
                return true;
            case "WIN":
            case "LWIN":
            case "WINDOWS":
                modifierVk = VkLWin;
                return true;
            default:
                modifierVk = 0;
                return false;
        }
    }

    private static bool TryParsePrimaryKey(string token, out ushort vk, out bool extendedOverride)
    {
        vk = 0;
        extendedOverride = false;
        string clean = CollapseSpaces(token.Trim());

        if (clean == "+")
        {
            vk = 0xBB; // VK_OEM_PLUS
            return true;
        }

        if (KeyTable.TryGetValue(clean, out var entry))
        {
            vk = entry.Vk;
            extendedOverride = entry.ExtendedOverride;
            return true;
        }

        // Function keys F1..F24
        if ((clean.StartsWith('F') || clean.StartsWith('f'))
            && clean.Length is 2 or 3
            && int.TryParse(clean[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fNum)
            && fNum is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + (fNum - 1));
            return true;
        }

        // Single alphanumeric A-Z / 0-9
        if (clean.Length == 1)
        {
            char ch = char.ToUpperInvariant(clean[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                vk = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                vk = ch;
                return true;
            }
        }

        return false;
    }

    private static string CollapseSpaces(string value)
    {
        if (!value.Contains("  ", StringComparison.Ordinal))
        {
            return value;
        }

        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
