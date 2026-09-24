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
    /// Округляем к БЛИЖАЙШЕМУ слоту, а не вниз: кадр, пришедший на полмиллисекунды
    /// раньше своего слота, принадлежит ему, а не предыдущему.
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
    public static long? QuantizePts(long ticks, long baseTicks, long lastPts, long frameDurationTicks,
                                    int maxLeadSlots = 1)
    {
        if (frameDurationTicks <= 0) return ticks;

        long pts = NaturalSlot(ticks, baseTicks, frameDurationTicks);
        if (pts > lastPts) return pts;
        long pushed = lastPts + frameDurationTicks;
        return pushed - pts <= maxLeadSlots * frameDurationTicks ? pushed : null;
    }

    /// <summary>Слот сетки, ближайший к времени захвата кадра.</summary>
    public static long NaturalSlot(long ticks, long baseTicks, long frameDurationTicks)
    {
        if (frameDurationTicks <= 0) return ticks;
        long slots = (ticks - baseTicks + frameDurationTicks / 2) / frameDurationTicks;
        return baseTicks + slots * frameDurationTicks;
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
    public static int BackfillSlots(long lastPts, long pts, long frameDurationTicks, int maxSlots)
    {
        if (frameDurationTicks <= 0 || maxSlots <= 0) return 0;

        long gap = pts - lastPts;
        if (gap > frameDurationTicks * (maxSlots + 1)) return 0;   // пауза длинная — не наше дело

        long slots = gap / frameDurationTicks - 1;                 // сам кадр не считаем
        return slots <= 0 ? 0 : (int)Math.Min(slots, maxSlots);
    }
}
