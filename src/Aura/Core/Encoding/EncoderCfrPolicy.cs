namespace Aura.Core.Encoding;

/// <summary>
/// Арифметика постоянной частоты кадров: куда на сетке 1/fps встаёт пришедший кадр
/// и сколько слотов перед ним надо закрыть дубликатами.
///
/// ЗАЧЕМ ОТДЕЛЬНЫМ ТИПОМ. Это самый хитрый счёт во всём конвейере, и до сих пор он
/// жил внутри <see cref="VideoEncoder"/> — вперемешку с блокировками, пулом текстур
/// и вызовами Media Foundation, то есть в коде, который нельзя ни запустить в тесте,
/// ни даже собрать без видеокарты. Здесь тех же формул касаются только числа.
///
/// Сама сетка нужна потому, что сырые времена захвата привязаны к развёртке монитора:
/// на 144 Гц интервалы идут вперемешку по 13.9 и 20.8 мс. Плеер честно воспроизводит
/// этот джиттер, и запись выглядит дёрганой, хотя все кадры на месте.
/// </summary>
internal static class EncoderCfrPolicy
{
    /// <summary>
    /// Слот сетки для кадра с исходным временем <paramref name="ticks"/>.
    ///
    /// Занятый слот (там уже стоит дубликат от пейсера или предыдущий кадр) не повод
    /// выбрасывать настоящий кадр — он встаёт в следующий. Живое движение всегда
    /// лучше повтора: раньше на этом терялось около девяноста настоящих кадров в
    /// минуту, и вместо них в записи оставались замершие дубликаты.
    ///
    /// Но сдвигать можно не дальше <paramref name="maxLeadSlots"/> слотов от времени
    /// захвата. Раньше сдвиг был любой: под игрой кадры захвата приходили с
    /// опозданием, пейсер успевал закрыть их слоты дубликатами, и опоздавшие кадры
    /// вставали за ними — видео уезжало вперёд времени захвата. Каждый такой
    /// эпизод добавлял сдвиг, и в записи картинка отставала от звука на
    /// полсекунды-секунду. Кадр, опоздавший сильнее, не пишется (возвращается
    /// null): его слот уже закрыт повтором, а картинка пойдёт в следующие повторы.
    /// </summary>
    public static long? QuantizePts(long ticks, long baseTicks, long lastPts, int fps,
                                    int maxLeadSlots = 1)
    {
        if (fps <= 0) return ticks;

        long slot = SlotIndex(ticks, baseTicks, fps);
        long lastSlot = SlotIndex(lastPts, baseTicks, fps);
        if (slot > lastSlot) return SlotPts(slot, baseTicks, fps);
        long pushed = lastSlot + 1;
        return pushed - slot <= maxLeadSlots ? SlotPts(pushed, baseTicks, fps) : null;
    }

    /// <summary>Слот сетки, ближайший к времени захвата кадра.</summary>
    public static long NaturalSlot(long ticks, long baseTicks, int fps) =>
        fps <= 0 ? ticks : SlotPts(SlotIndex(ticks, baseTicks, fps), baseTicks, fps);

    /// <summary>Время слота, следующего за слотом <paramref name="pts"/>.</summary>
    public static long NextSlot(long pts, long baseTicks, int fps) =>
        fps <= 0 ? pts : SlotPts(SlotIndex(pts, baseTicks, fps) + 1, baseTicks, fps);

    /// <summary>
    /// Время слота по его НОМЕРУ, а не сложением длительностей.
    ///
    /// ЗАЧЕМ. Кадр 60 fps длится 166 666.67 тика, а целочисленная длительность
    /// 166 666. Раньше следующий слот получался прибавлением длительности, и за
    /// час набегало 216 000 × 0.67 тика: сетка отставала от настоящих часов на
    /// 14 мс (на 144 fps на 23 мс), а файл считает каждый кадр ровно 1/60 с.
    /// Видео медленно уезжало от звука на длинных записях. Номер слота умножается
    /// на 10 000 000 и только потом делится на частоту, так что ошибка не
    /// накапливается: не больше одного тика у каждого слота.
    /// </summary>
    public static long SlotPts(long slot, long baseTicks, int fps) =>
        baseTicks + slot * 10_000_000L / fps;

    /// <summary>
    /// Номер ближайшего слота. Округление к БЛИЖАЙШЕМУ, а не вниз: кадр, пришедший
    /// на полмиллисекунды раньше своего слота, принадлежит ему, а не предыдущему.
    /// </summary>
    public static long SlotIndex(long ticks, long baseTicks, int fps)
    {
        long scaled = (ticks - baseTicks) * fps + 5_000_000;
        long slot = scaled / 10_000_000;
        if (scaled % 10_000_000 != 0 && scaled < 0) slot--;   // деление вниз и для отрицательных
        return slot;
    }

    /// <summary>
    /// Сколько пустых слотов осталось между последним записанным кадром и новым.
    ///
    /// Их закрывают дубликатами задним числом: пейсер работает с шагом 4 мс и
    /// короткую паузу закрыть не успевает, а дыра в сетке — это провал частоты в файле.
    ///
    /// Длинные паузы сюда не попадают намеренно (<paramref name="maxSlots"/>): их
    /// закрывает пейсер в реальном времени, а вываливать сразу сотню дубликатов
    /// одним куском значит забить очередь энкодера ровно в тот момент, когда экран
    /// наконец ожил.
    /// </summary>
    public static int BackfillSlots(long lastPts, long pts, long baseTicks, int fps, int maxSlots)
    {
        if (fps <= 0 || maxSlots <= 0) return 0;

        long gap = SlotIndex(pts, baseTicks, fps) - SlotIndex(lastPts, baseTicks, fps);
        if (gap > maxSlots + 1) return 0;                          // пауза длинная — не наше дело

        long slots = gap - 1;                                      // сам кадр не считаем
        return slots <= 0 ? 0 : (int)Math.Min(slots, maxSlots);
    }
}
