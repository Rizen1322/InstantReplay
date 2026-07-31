namespace Aura.Core.Hotkeys;

/// <summary>
/// Парсер строк вида "Ctrl+Alt+F10", "Shift+S", "F9" в (vk + модификаторы).
///
/// Живёт отдельным файлом без зависимостей: этим же кодом пользуются проверка
/// конфликтов и тесты (тест-проект компилирует файл напрямую, без WinUI).
/// </summary>
public static class HotkeyParser
{
    private static readonly Dictionary<string, uint> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Esc"] = 0x1B, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["PrintScreen"] = 0x2C, ["Pause"] = 0x13,
        ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
    };

    public static bool TryParse(string combo, out (uint vk, bool ctrl, bool shift, bool alt, bool win) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        bool ctrl = false, shift = false, alt = false, win = false;
        uint vk = 0;

        foreach (var raw in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0]))
                        vk = char.ToUpperInvariant(raw[0]);
                    else if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f') && int.TryParse(raw[1..], out int f) && f is >= 1 and <= 24)
                        vk = (uint)(0x70 + f - 1); // VK_F1..F24
                    else if (Special.TryGetValue(raw, out var s))
                        vk = s;
                    else return false;
                    break;
            }
        }
        if (vk == 0) return false;
        result = (vk, ctrl, shift, alt, win);
        return true;
    }

    /// <summary>
    /// Нормализованная запись сочетания: "alt + shift+f10" и "Shift+Alt+F10" — одно и то же.
    /// Пустая строка, если разобрать не удалось.
    /// </summary>
    public static string Normalize(string combo)
    {
        if (!TryParse(combo, out var key)) return "";
        var parts = new List<string>(4);
        if (key.ctrl) parts.Add("Ctrl");
        if (key.shift) parts.Add("Shift");
        if (key.alt) parts.Add("Alt");
        if (key.win) parts.Add("Win");
        parts.Add(KeyName(key.vk));
        return string.Join("+", parts);
    }

    /// <summary>Человекочитаемое имя клавиши по виртуальному коду.</summary>
    public static string KeyName(uint vk)
    {
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x70 + 1}";
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        foreach (var (name, code) in Special)
            if (code == vk) return name;
        return $"0x{vk:X2}";
    }
}
