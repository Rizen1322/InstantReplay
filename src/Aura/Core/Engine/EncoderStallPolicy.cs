namespace Aura.Core.Engine;

/// <summary>
/// Сколько секунд энкодер может молчать, прежде чем сторож сочтёт конвейер вставшим.
///
/// ЗАЧЕМ РАЗНЫЕ ПОРОГИ. Взаимная блокировка на видеокарте выглядит так: энкодер
/// выдавал 60 кадров в секунду и вдруг ноль. Голодание выглядит иначе: игра
/// забрала видеокарту (CS2 на 88% при включённом HAGS), кадры выходят из NVENC
/// через секунды, и выход сначала падает до 5–15 кадров в секунду, а потом может
/// замолчать на 4–5 с. Это не поломка, и пересборка тут вредна: переход на MFT
/// очищает буфер повтора, а второй такой эпизод выключал повтор совсем.
///
/// Поэтому при недавнем медленном выходе ждём дольше, а выключаем повтор только
/// после 20 с полной тишины: голодающий энкодер столько не молчит, мёртвый молчит
/// всегда.
/// </summary>
internal static class EncoderStallPolicy
{
    public const double DeadlockSeconds = 4;
    public const double StarvedSeconds = 15;
    public const double GiveUpSeconds = 20;

    /// <summary>Недавний выход медленнее этой доли частоты — признак голодания.</summary>
    public const double SlowFraction = 0.75;

    /// <summary>Сколько секунд после медленного выхода считать видеокарту занятой.</summary>
    public const double StarvationMemorySeconds = 60;

    /// <summary>Сколько медленных секунд за минуту считается устойчивым голоданием.</summary>
    public const int SlowWindowsForStarvation = 3;

    /// <summary>
    /// Видеокарта занята, а не мертва: выход был медленным несколько секунд за
    /// последнюю минуту, или игра грузит 3D почти целиком. Одна медленная секунда
    /// не в счёт: она бывает и в начале настоящего зависания, когда кадры
    /// обрываются посреди секунды. Загрузка наших собственных движков тоже не
    /// признак: захват и перевод кадров работают и при вставшем энкодере.
    /// Отрицательное значение замера — замера нет.
    /// </summary>
    public static bool IsStarved(int recentSlowWindows, double allGraphicsPercent) =>
        recentSlowWindows >= SlowWindowsForStarvation || allGraphicsPercent >= 80;

    public static double ThresholdSeconds(int previousEpisodes, bool starving) =>
        previousEpisodes > 0 ? GiveUpSeconds : starving ? StarvedSeconds : DeadlockSeconds;

    /// <summary>Выход за окно медленный, но не нулевой.</summary>
    public static bool IsSlow(long framesEncoded, double seconds, int fps) =>
        fps > 0 && seconds >= 0.9 && framesEncoded > 0 && framesEncoded < fps * seconds * SlowFraction;
}
