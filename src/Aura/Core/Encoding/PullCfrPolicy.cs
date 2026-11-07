namespace Aura.Core.Encoding;

/// <summary>
/// Сетка выходных слотов для pull-модели: номер слота и его время считаются от
/// часов ВЫХОДНОГО видео, а не от момента, когда захват принёс кадр.
///
/// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="EncoderCfrPolicy"/>. Там кадр сам занимает слот по
/// времени своего содержимого (push): сколько кадров принёс захват, столько слотов
/// и будет занято. Отсюда две болезни, которые видно только на частотах, не кратных
/// записи (144 и 165 Гц): при 144 Гц полуслотное ограничение захвата даёт 72 кадра
/// в секунду, а слотов в секунду 60, и каждый шестой слот получает прыжок
/// содержимого вдвое больше обычного.
///
/// Здесь наоборот (pull): слот существует по часам, и в него кладётся самый свежий
/// готовый кадр на момент слота. Сколько захват ни принесёт — выход строго 1/fps.
///
/// ЧЕГО ЭТО НЕ ЛЕЧИТ. Дискретизацию движения: если экран обновляется 144 раза в
/// секунду, а слот идёт раз в 16.67 мс, то между соседними слотами содержимое
/// сдвигается на 13.9 или 20.8 мс — это свойство выборки, а не планировщика.
/// Планировщик отвечает только за то, чтобы слоты шли ровно и без дыр.
///
/// ГРАНИЦЫ СЛОТОВ ОКРУГЛЯЮТСЯ ВВЕРХ. При делении вниз (как в push-сетке)
/// SlotPts(k) оказывается на тик МЕНЬШЕ честного времени k/fps, и тогда
/// SlotIndex(SlotPts(k)) даёт k-1: сетка перестаёт быть обратной самой себе.
/// На этом ломается дозаполнение — в проверке такой сетки цикл «догнать слот»
/// не заканчивается никогда. Округление вверх этот инвариант держит по
/// построению: см. PullCfrPolicyTests.
/// </summary>
internal static class PullCfrPolicy
{
    public const long TicksPerSecond = 10_000_000L;

    /// <summary>Время слота по его номеру. Границы только растут, дрейфа нет.</summary>
    public static long SlotPts(long slot, long baseTicks, int fps) =>
        fps <= 0
            ? baseTicks + slot
            : baseTicks + (slot * TicksPerSecond + fps - 1) / fps;

    /// <summary>
    /// Номер слота, в который попадает момент <paramref name="ticks"/>: наибольший
    /// k, для которого <c>SlotPts(k) &lt;= ticks</c>. Обратная к <see cref="SlotPts"/>.
    /// </summary>
    public static long SlotIndex(long ticks, long baseTicks, int fps)
    {
        if (fps <= 0) return 0;

        long distance = ticks - baseTicks;
        if (distance <= 0) return 0;

        // Начальное приближение делением, дальше — не больше шага в каждую сторону:
        // округление границ вверх сдвигает оценку не больше чем на тик.
        long slot = distance * fps / TicksPerSecond;
        if (slot < 0) slot = 0;
        while (slot > 0 && SlotPts(slot, baseTicks, fps) > ticks) slot--;
        while (SlotPts(slot + 1, baseTicks, fps) <= ticks) slot++;
        return slot;
    }

    /// <summary>
    /// Сколько слотов пора закрыть к моменту <paramref name="now"/>.
    ///
    /// Возвращает число, а не список: закрываются слоты
    /// <c>lastClosedSlot + 1 ... lastClosedSlot + count</c> подряд. Ноль означает
    /// «ещё рано»; значение всегда положительное при отставании, поэтому цикл
    /// вызывающего сдвигает <c>lastClosedSlot</c> и обязан закончиться.
    /// </summary>
    /// <param name="maxBurst">
    /// Потолок за один тик. Без него после заморозки процесса мы вываливали бы в
    /// энкодер сотни повторов сразу — ровно в тот момент, когда картинка ожила.
    /// </param>
    public static int DueCount(long now, long lastClosedSlot, long baseTicks, int fps, int maxBurst)
    {
        if (fps <= 0 || maxBurst <= 0) return 0;
        // До начала сетки закрывать нечего: первый слот начинается вместе с базой.
        if (now < baseTicks) return 0;

        long due = SlotIndex(now, baseTicks, fps) - lastClosedSlot;
        if (due <= 0) return 0;
        return (int)Math.Min(due, maxBurst);
    }
}

/// <summary>
/// План на один тик пейсера: сколько слотов закрыть и берёт ли свежий кадр последний
/// из них.
/// </summary>
/// <param name="CloseCount">Сколько слотов закрыть подряд, начиная со следующего.</param>
/// <param name="UseCandidate">
/// Отдать свежий кадр ПОСЛЕДНЕМУ закрываемому слоту. Остальные — повторы.
/// </param>
internal readonly record struct PullSlotPlan(int CloseCount, bool UseCandidate)
{
    public static PullSlotPlan None => new(0, false);
}

/// <summary>
/// Решение «что положить в слот» для pull-модели.
///
/// ГЛАВНОЕ ПРАВИЛО: свежий кадр достаётся только последнему закрываемому слоту, а
/// предыдущие догоняющие слоты получают повтор. Иначе при отставании пейсера
/// (энкодер задержался на 80–150 мс) свежий кадр попал бы в старый слот, а
/// следующие слоты — повторы более старого кадра: содержимое поехало бы назад по
/// времени, и в записи получился бы рывок вперёд с откатом.
///
/// Второе правило: кандидат берётся, только если он новее содержимого прошлого
/// слота. Иначе (кадр не пришёл, захват отстал) — повтор, ровный CFR важнее
/// свежести.
/// </summary>
internal static class PullCfrScheduler
{
    public static PullSlotPlan Plan(
        long now,
        long lastClosedSlot,
        long baseTicks,
        int fps,
        int maxBurst,
        bool hasCandidate,
        long candidateCaptureTicks,
        long lastContentTicks)
    {
        int count = PullCfrPolicy.DueCount(now, lastClosedSlot, baseTicks, fps, maxBurst);
        if (count == 0) return PullSlotPlan.None;

        bool useCandidate = hasCandidate && candidateCaptureTicks > lastContentTicks;
        return new PullSlotPlan(count, useCandidate);
    }
}

/// <summary>
/// Pull-пейсинг, как видеопоток OBS: тик 60 Гц берёт последний пришедший кадр, а не
/// раскладывает кадры по слотам по их меткам времени. Режим по умолчанию: под тяжёлой
/// игрой метки WGC сбиваются в кучи, и push-отбор выбрасывал настоящие кадры
/// (Outlast, GPU ~90%: уникальных 81–90% против 70–81%, дыр в записи в разы меньше,
/// 7 ноября 2026). AURA_PULL_PACING=0 возвращает push. Не меняется на ходу: у пейсера
/// и у захвата на всю сессию должна быть одна сетка.
/// </summary>
internal static class PullPacing
{
    public static bool Enabled { get; } = !string.Equals(
        Environment.GetEnvironmentVariable("AURA_PULL_PACING"), "0", StringComparison.Ordinal);

    internal static bool IsEnabled(string variable) =>
        string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal) ||
        string.Equals(Environment.GetEnvironmentVariable(variable), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Как часто захват отдаёт кадры. Переключатель НЕЗАВИСИМ от пейсера.</summary>
internal enum CaptureGateMode
{
    /// <summary>
    /// Не чаще одного кадра за полслота по времени — прежнее поведение.
    /// На 144 Гц это 72 кадра в секунду, на 165 Гц — 82.
    ///
    /// Почему больше не по умолчанию: порог по времени не совпадает с сеткой слотов
    /// и теряет кадры ПАЧКАМИ. В логе с 165-Гц монитором принято 2810 кадров из 4144 —
    /// система отдавала 69 кадров в секунду, а отбор пропускал 47, потому что кадры
    /// приходят неровно: два подряд через 5 мс, потом пауза. Двух подряд хватало,
    /// чтобы второй выбросить, а следующего слота кадр так и не получал.
    /// </summary>
    HalfSlot,

    /// <summary>
    /// Ровно один кадр на слот сетки захвата: 60 кадров в секунду при 60 fps.
    /// Столько кандидатов и нужно выходной сетке — не больше и не меньше.
    /// </summary>
    OnePerSlot,

    /// <summary>
    /// Не больше двух кадров каждого слота. Нужен pull-модели: свежий кадр должен
    /// успеть появиться в каждом слоте, чтобы пейсеру было из чего выбирать.
    /// На 144 Гц это 120 кадров, на 240 Гц — тоже 120, на 60 Гц — 60.
    /// </summary>
    TwoPerSlot
}

/// <summary>
/// Отбор кадров захвата по сетке слотов.
///
/// ПОЧЕМУ ОТДЕЛЬНЫЙ ПЕРЕКЛЮЧАТЕЛЬ ОТ ПЕЙСЕРА. Пейсер решает, как расставить кадры по
/// слотам, захват — сколько кандидатов вообще придёт. Это разные вещи, и разбирать
/// их надо по отдельности: AURA_PULL_PACING и AURA_CAPTURE_GATE.
///
/// AURA_CAPTURE_GATE=0 (off) — прежний отбор по времени; =2 — по два кадра на слот
/// (для pull-модели); не задана или =1 — один кадр на слот.
/// </summary>
internal static class CaptureGate
{
    public static CaptureGateMode Mode { get; } = ReadMode();

    /// <summary>Сколько кадров на слот разрешаем.</summary>
    public static int PerSlot =>
        Mode == CaptureGateMode.TwoPerSlot ? 2 : Mode == CaptureGateMode.OnePerSlot ? 1 : 0;

    /// <summary>Отбор идёт по сетке слотов, а не по интервалу времени.</summary>
    public static bool UsesSlotGate => Mode != CaptureGateMode.HalfSlot;

    /// <summary>
    /// В режиме двух кадров на слот система не должна ограничивать себя сама: иначе
    /// WGC отдаст один кадр слота и второй брать будет неоткуда.
    /// </summary>
    public static bool WantsUnlimitedDelivery => Mode == CaptureGateMode.TwoPerSlot;

    private static CaptureGateMode ReadMode()
    {
        string? raw = Environment.GetEnvironmentVariable("AURA_CAPTURE_GATE");
        if (string.Equals(raw, "0", StringComparison.Ordinal) ||
            string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
        {
            return CaptureGateMode.HalfSlot;
        }
        if (string.Equals(raw, "2", StringComparison.Ordinal)) return CaptureGateMode.TwoPerSlot;
        return CaptureGateMode.OnePerSlot;
    }
}

/// <summary>
/// Отбор кадров по сетке слотов: не больше <c>perSlot</c> кадров на каждый слот.
///
/// Сетка своя, от первого принятого кадра, и её номер используется только для
/// сравнения «этот же слот или следующий» — обратная функция здесь не нужна, поэтому
/// и ловушек с округлением границ нет.
/// </summary>
internal sealed class CaptureSlotGate
{
    private readonly int _fps;
    private readonly int _perSlot;
    private long _baseTicks = -1;
    private long _slot = -1;
    private int _inSlot;

    public CaptureSlotGate(int fps, int perSlot = 2)
    {
        _fps = fps;
        _perSlot = Math.Max(1, perSlot);
    }

    /// <summary>true — кадр нужно взять.</summary>
    public bool Admit(long ticks)
    {
        if (_fps <= 0) return true;

        if (_baseTicks < 0)
        {
            _baseTicks = ticks;
            _slot = 0;
            _inSlot = 1;
            return true;
        }

        long slot = SlotOf(ticks);
        if (slot > _slot)
        {
            _slot = slot;
            _inSlot = 1;
            return true;
        }

        // Метка ушла назад (джиттер или пересоздание сессии): не берём, чтобы не
        // открыть слот дважды.
        if (slot < _slot) return false;

        if (_inSlot >= _perSlot) return false;
        _inSlot++;
        return true;
    }

    private long SlotOf(long ticks)
    {
        // Полслота сдвига к базе: сетка отсчитывается от ПЕРВОГО принятого кадра, и без
        // сдвига ровные 60 Гц попадают на тик раньше границы слота — каждый второй кадр
        // уходил в предыдущий слот, и отбор резал частоту вдвое.
        long distance = ticks - _baseTicks + PullCfrPolicy.TicksPerSecond / _fps / 2;
        if (distance <= 0) return 0;
        return distance * _fps / PullCfrPolicy.TicksPerSecond;
    }
}
