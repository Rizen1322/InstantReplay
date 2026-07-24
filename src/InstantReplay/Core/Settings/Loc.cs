namespace InstantReplay.Core.Settings;

/// <summary>Минимальная локализация RU/EN для строк уведомлений и трея.</summary>
public static class Loc
{
    public static AppLanguage Lang { get; set; } = AppLanguage.Ru;

    private static readonly Dictionary<string, (string ru, string en)> S = new()
    {
        ["replay_on"]      = ("Instant Replay включен", "Instant Replay is on"),
        ["replay_off"]     = ("Instant Replay выключен", "Instant Replay is off"),
        ["rec_stopped"]    = ("Запись остановлена", "Recording stopped"),
        ["saved"]          = ("Последние {0} сохранены", "Last {0} saved"),
        ["save_failed"]    = ("Не удалось сохранить повтор", "Failed to save replay"),
        ["low_space"]      = ("Мало места на диске", "Low disk space"),
        ["tray_open"]      = ("Открыть", "Open"),
        ["tray_toggle"]    = ("Вкл/выкл Instant Replay", "Toggle Instant Replay"),
        ["tray_turn_on"]   = ("Включить Instant Replay", "Turn Instant Replay on"),
        ["tray_turn_off"]  = ("Выключить Instant Replay", "Turn Instant Replay off"),
        ["tray_save"]      = ("Сохранить повтор", "Save replay"),
        ["tray_screenshot"]= ("Сделать скриншот", "Take screenshot"),
        ["tray_folder"]    = ("Открыть папку записей", "Open recordings folder"),
        ["tray_exit"]      = ("Выход", "Exit"),
        ["tip_recording"]  = ("Instant Replay — идёт запись в файл", "Instant Replay — recording to file"),
        ["tip_buffering"]  = ("Instant Replay — буфер пишется", "Instant Replay — buffering"),
        ["tip_off"]        = ("Instant Replay — выключено", "Instant Replay — off"),
        ["update_found"]   = ("Доступно обновление {0} — вкладка «Приложение»",
                              "Update {0} available — see the App tab"),
        ["rec_started"]    = ("Идёт запись", "Recording started"),
        ["rec_saved"]      = ("Запись сохранена ({0})", "Recording saved ({0})"),
        ["shot_saved"]     = ("Скриншот сохранён", "Screenshot saved"),
        ["shot_failed"]    = ("Не удалось сделать скриншот", "Screenshot failed"),
    };

    public static string T(string key, params object[] args)
    {
        if (!S.TryGetValue(key, out var v)) return key;
        var s = Lang == AppLanguage.Ru ? v.ru : v.en;
        return args.Length > 0 ? string.Format(s, args) : s;
    }

    /// <summary>"3 минуты" / "45 секунд" для уведомления о сохранении.</summary>
    public static string Duration(int seconds)
    {
        if (Lang == AppLanguage.En)
            return seconds >= 60 ? $"{seconds / 60} min {(seconds % 60 > 0 ? seconds % 60 + " sec" : "")}".Trim()
                                 : $"{seconds} sec";
        if (seconds < 60) return $"{seconds} сек.";
        int m = seconds / 60, s = seconds % 60;
        return s == 0 ? $"{m} мин." : $"{m} мин. {s} сек.";
    }
}
