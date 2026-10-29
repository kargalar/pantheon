using Avalonia.Input;
using AvaloniaKey = Avalonia.Input.Key;

namespace VideoPlayer;

internal readonly record struct ShortcutGesture(string Key, KeyModifiers Modifiers)
{
    private const KeyModifiers AllModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;

    public static bool TryParse(string value, out ShortcutGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "alt" => KeyModifiers.Alt,
                "shift" => KeyModifiers.Shift,
                "win" or "meta" => KeyModifiers.Meta,
                _ => KeyModifiers.None
            };
            if (modifier == KeyModifiers.None || modifiers.HasFlag(modifier)) return false;
            modifiers |= modifier;
        }

        var key = NormalizeKeyName(parts[^1]);
        if (key is null) return false;
        gesture = new ShortcutGesture(key, modifiers);
        return true;
    }

    public static bool TryFromAvalonia(AvaloniaKey key, KeyModifiers modifiers, out ShortcutGesture gesture)
    {
        gesture = default;
        if ((modifiers & ~AllModifiers) != 0 || !TryGetVirtualKey(key, out var virtualKey)) return false;
        gesture = new ShortcutGesture(VirtualKeyName(virtualKey)!, modifiers & AllModifiers);
        return true;
    }

    public static bool TryFromVirtualKey(int virtualKey, KeyModifiers modifiers, out ShortcutGesture gesture)
    {
        gesture = default;
        var name = VirtualKeyName(virtualKey);
        if (name is null) return false;
        gesture = new ShortcutGesture(name, modifiers & AllModifiers);
        return true;
    }

    public override string ToString()
    {
        var prefix = string.Join('+', new[]
        {
            Modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl" : null,
            Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt" : null,
            Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift" : null,
            Modifiers.HasFlag(KeyModifiers.Meta) ? "Win" : null
        }.Where(value => value is not null));
        var displayKey = Key switch
        {
            "OemPlus" => "Plus",
            "OemMinus" => "Minus",
            "Oem3" => "\"",
            "Return" => "Enter",
            "OemComma" => ",",
            "OemPeriod" => ".",
            "Oem102" => "<",
            "Back" => "Backspace",
            _ => Key
        };
        return prefix.Length == 0 ? displayKey : $"{prefix}+{displayKey}";
    }

    private static string? NormalizeKeyName(string value)
    {
        if (value.Length == 1 && char.IsAsciiLetter(value[0])) return value.ToUpperInvariant();
        if (value.Length == 2 && value[0] == 'D' && char.IsAsciiDigit(value[1])) return value;
        if (value.Length >= 2 && value[0] == 'F' && int.TryParse(value.AsSpan(1), out var function) && function is >= 1 and <= 24)
            return $"F{function}";
        if (value.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && value.Length == 7 && char.IsAsciiDigit(value[^1]))
            return $"NumPad{value[^1]}";
        return value.ToLowerInvariant() switch
        {
            "space" => "Space",
            "left" => "Left",
            "right" => "Right",
            "up" => "Up",
            "down" => "Down",
            "back" or "backspace" => "Back",
            "quote" or "\"" or "oem3" => "Oem3",
            "," or "ö" or "oemcomma" => "OemComma",
            "." or "ç" or "oemperiod" => "OemPeriod",
            "return" or "enter" => "Return",
            "escape" or "esc" => "Escape",
            "tab" => "Tab",
            "delete" or "del" => "Delete",
            "insert" => "Insert",
            "home" => "Home",
            "end" => "End",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            "capslock" => "CapsLock",
            "pause" => "Pause",
            "printscreen" => "PrintScreen",
            "scroll" or "scrolllock" => "Scroll",
            "numlock" => "NumLock",
            "add" => "Add",
            "subtract" => "Subtract",
            "plus" or "oemplus" => "OemPlus",
            "minus" or "oemminus" => "OemMinus",
            "oem1" => "Oem1",
            "oem2" => "Oem2",
            "oem4" => "Oem4",
            "oem5" => "Oem5",
            "oem6" => "Oem6",
            "oem7" => "Oem7",
            "oem8" => "Oem8",
            "<" or ">" or "oem102" => "Oem102",
            "multiply" => "Multiply",
            "divide" => "Divide",
            "decimal" => "Decimal",
            _ => null
        };
    }

    private static bool TryGetVirtualKey(AvaloniaKey key, out int virtualKey)
    {
        var keyValue = (int)key;
        virtualKey = keyValue switch
        {
            (int)AvaloniaKey.Space => 0x20,
            (int)AvaloniaKey.Left => 0x25,
            (int)AvaloniaKey.Up => 0x26,
            (int)AvaloniaKey.Right => 0x27,
            (int)AvaloniaKey.Down => 0x28,
            (int)AvaloniaKey.Back => 0x08,
            (int)AvaloniaKey.Return => 0x0D,
            (int)AvaloniaKey.Escape => 0x1B,
            (int)AvaloniaKey.Tab => 0x09,
            (int)AvaloniaKey.Delete => 0x2E,
            (int)AvaloniaKey.Insert => 0x2D,
            (int)AvaloniaKey.Home => 0x24,
            (int)AvaloniaKey.End => 0x23,
            (int)AvaloniaKey.PageUp => 0x21,
            (int)AvaloniaKey.PageDown => 0x22,
            (int)AvaloniaKey.Add => 0x6B,
            (int)AvaloniaKey.Subtract => 0x6D,
            (int)AvaloniaKey.Multiply => 0x6A,
            (int)AvaloniaKey.Divide => 0x6F,
            (int)AvaloniaKey.Decimal => 0x6E,
            (int)AvaloniaKey.CapsLock => 0x14,
            (int)AvaloniaKey.Pause => 0x13,
            (int)AvaloniaKey.PrintScreen => 0x2C,
            (int)AvaloniaKey.Scroll => 0x91,
            (int)AvaloniaKey.NumLock => 0x90,
            (int)AvaloniaKey.Oem1 => 0xBA,
            (int)AvaloniaKey.OemPlus => 0xBB,
            (int)AvaloniaKey.OemComma => 0xBC,
            (int)AvaloniaKey.OemMinus => 0xBD,
            (int)AvaloniaKey.OemPeriod => 0xBE,
            (int)AvaloniaKey.Oem2 => 0xBF,
            (int)AvaloniaKey.Oem3 => 0xC0,
            (int)AvaloniaKey.Oem4 => 0xDB,
            (int)AvaloniaKey.Oem5 => 0xDC,
            (int)AvaloniaKey.Oem6 => 0xDD,
            (int)AvaloniaKey.Oem7 => 0xDE,
            (int)AvaloniaKey.Oem8 => 0xDF,
            (int)AvaloniaKey.Oem102 => 0xE2,
            var value when value >= (int)AvaloniaKey.A && value <= (int)AvaloniaKey.Z => 0x41 + value - (int)AvaloniaKey.A,
            var value when value >= (int)AvaloniaKey.D0 && value <= (int)AvaloniaKey.D9 => 0x30 + value - (int)AvaloniaKey.D0,
            var value when value >= (int)AvaloniaKey.NumPad0 && value <= (int)AvaloniaKey.NumPad9 => 0x60 + value - (int)AvaloniaKey.NumPad0,
            var value when value >= (int)AvaloniaKey.F1 && value <= (int)AvaloniaKey.F24 => 0x70 + value - (int)AvaloniaKey.F1,
            _ => 0
        };
        return virtualKey != 0;
    }

    private static string? VirtualKeyName(int virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x30 and <= 0x39 => $"D{(char)virtualKey}",
        >= 0x60 and <= 0x69 => $"NumPad{virtualKey - 0x60}",
        >= 0x70 and <= 0x87 => $"F{virtualKey - 0x70 + 1}",
        0x20 => "Space",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x08 => "Back",
        0x0D => "Return",
        0x1B => "Escape",
        0x09 => "Tab",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x6B => "Add",
        0x6D => "Subtract",
        0x6A => "Multiply",
        0x6F => "Divide",
        0x6E => "Decimal",
        0x14 => "CapsLock",
        0x13 => "Pause",
        0x2C => "PrintScreen",
        0x91 => "Scroll",
        0x90 => "NumLock",
        0xBA => "Oem1",
        0xBB => "OemPlus",
        0xBC => "OemComma",
        0xBD => "OemMinus",
        0xBE => "OemPeriod",
        0xBF => "Oem2",
        0xC0 => "Oem3",
        0xDB => "Oem4",
        0xDC => "Oem5",
        0xDD => "Oem6",
        0xDE => "Oem7",
        0xDF => "Oem8",
        0xE2 => "Oem102",
        _ => null
    };
}
