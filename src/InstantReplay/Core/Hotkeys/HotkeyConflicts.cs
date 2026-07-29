using InstantReplay.Core.Settings;

namespace InstantReplay.Core.Hotkeys;

/// <summary>Одно назначение: действие, его человекочитаемое имя и сочетание из настроек.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, string Title, string Combo);

/// <summary>
/// Проверка назначений горячих клавиш.
///
/// Зачем: хук перехватывает сочетание ДО игры и возвращает 1 — то есть клавиша не
/// доходит ни до игры, ни до любой другой программы. Поэтому одно и то же сочетание
/// на двух действиях означает, что одно из них молча никогда не сработает, а бинд
/// без модификатора (просто «S») превращает эту букву в мёртвую во всей системе.
/// Раньше приложение об этом не предупреждало.
///
/// Класс чистый (только настройки + парсер) — покрыт тестами.
/// </summary>
public static class HotkeyConflicts
{
    /// <summary>Все назначения в порядке, в котором они показаны на вкладке «Клавиши».</summary>
    public static List<HotkeyBinding> Bindings(AppSettings s) =>
    [
        new(HotkeyAction.SaveReplay, "Сохранить повтор", s.HotkeySaveReplay),
        new(HotkeyAction.SaveLast30, "Сохранить последние 30 сек", s.HotkeySaveLast30),
        new(HotkeyAction.ToggleInstantReplay, "Вкл/выкл Instant Replay", s.HotkeyToggleInstantReplay),
        new(HotkeyAction.StartRecording, "Начать запись", s.HotkeyStartRecording),
        new(HotkeyAction.StopRecording, "Остановить запись", s.HotkeyStopRecording),
        new(HotkeyAction.Screenshot, "Скриншот", s.HotkeyScreenshot),
        new(HotkeyAction.OpenFolder, "Открыть папку записей", s.HotkeyOpenFolder),
    ];

    /// <summary>
    /// Действия, чьё сочетание занято другим действием: действие → имя конфликтующего.
    /// Пустые бинды и неразбираемые строки не участвуют.
    /// </summary>
    public static Dictionary<HotkeyAction, string> FindDuplicates(IEnumerable<HotkeyBinding> bindings)
    {
        var byCombo = new Dictionary<string, List<HotkeyBinding>>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            string key = HotkeyParser.Normalize(binding.Combo);
            if (key.Length == 0) continue; // пусто или мусор — не конфликт
            if (!byCombo.TryGetValue(key, out var list)) byCombo[key] = list = [];
            list.Add(binding);
        }

        var result = new Dictionary<HotkeyAction, string>();
        foreach (var list in byCombo.Values)
        {
            if (list.Count < 2) continue;
            foreach (var binding in list)
            {
                var others = list.Where(b => b.Action != binding.Action).Select(b => b.Title);
                result[binding.Action] = string.Join(", ", others);
            }
        }
        return result;
    }

    /// <summary>Кто уже занял это сочетание, кроме указанного действия (null — свободно).</summary>
    public static HotkeyBinding? Occupant(AppSettings s, HotkeyAction action, string combo)
    {
        string key = HotkeyParser.Normalize(combo);
        if (key.Length == 0) return null;
        return Bindings(s).FirstOrDefault(b =>
            b.Action != action && HotkeyParser.Normalize(b.Combo).Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Предупреждение о сочетании, которое лучше не назначать (null — всё в порядке).
    /// Это не запрет: пользователь может настоять, просто должен понимать последствия.
    /// </summary>
    public static string? Risk(string combo)
    {
        if (!HotkeyParser.TryParse(combo, out var key)) return null;
        bool anyModifier = key.ctrl || key.shift || key.alt || key.win;
        string name = HotkeyParser.KeyName(key.vk);

        // Без модификатора хук съедает клавишу во всех программах — печатать её нельзя
        if (!anyModifier && key.vk is >= 'A' and <= 'Z' or >= '0' and <= '9'
                             or 0x20 or 0x0D or 0x09 or 0x08 or 0x1B)
            return $"«{name}» без модификатора перестанет работать во всех программах — " +
                   "добавь Ctrl, Alt или Shift";

        // Alt+F4 закрывает активное окно, и перехват ломает привычное поведение
        if (key.alt && !key.ctrl && !key.shift && key.vk == 0x73)
            return "Alt+F4 закрывает окна — игры и программы больше не закроются этим сочетанием";

        if (key.vk == 0x09 && (key.alt || key.win))
            return "Переключение окон системы этим сочетанием перестанет работать";

        if (key.win)
            return "Сочетания с Win в основном заняты системой — они могут не доходить до приложения";

        // Ctrl+C/V/X/Z/A/S — базовые команды всех программ
        if (key.ctrl && !key.alt && !key.shift && key.vk is 'C' or 'V' or 'X' or 'Z' or 'A' or 'S')
            return $"Ctrl+{name} — стандартная команда программ, перехват сломает её везде";

        return null;
    }
}
