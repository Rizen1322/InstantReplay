namespace Aura.Core.Encoding;

/// <summary>
/// Счётчики прямого NVENC на весь процесс. Энкодер пересоздаётся при каждой
/// пересборке конвейера, а итог нужен за всю запись, поэтому счётчики живут
/// отдельно: движок берёт снимок в начале записи и печатает разницу в конце.
/// </summary>
internal static class NvencStats
{
    /// <summary>nvEncEncodePicture ответил ENCODER_BUSY (каждый ответ, включая повторы).</summary>
    public static long BusyCount;
    /// <summary>nvEncEncodePicture ответил NEED_MORE_INPUT: кадр принят, выход позже.</summary>
    public static long NeedMoreInputCount;
    /// <summary>Входные кадры, так и не отправленные в NVENC.</summary>
    public static long DroppedInputFrames;
    /// <summary>Выходы, не отданные дальше: не заблокировался буфер или ждали ключевой.</summary>
    public static long DroppedOutputFrames;
    /// <summary>Кадры, отправленные с просьбой IDR.</summary>
    public static long ForcedIdrCount;
    /// <summary>Сессии, сломанные неустранимой ошибкой NVENC.</summary>
    public static long FatalErrors;
    /// <summary>Энкодеры, собранные на Media Foundation, потому что прямой NVENC уже запрещён.</summary>
    public static long FallbackCount;

    public readonly record struct Snapshot(long Busy, long NeedMoreInput, long DroppedInput,
                                           long DroppedOutput, long ForcedIdr, long Fatal, long Fallback)
    {
        public Snapshot Since(Snapshot start) => new(
            Busy - start.Busy, NeedMoreInput - start.NeedMoreInput, DroppedInput - start.DroppedInput,
            DroppedOutput - start.DroppedOutput, ForcedIdr - start.ForcedIdr, Fatal - start.Fatal,
            Fallback - start.Fallback);

        public override string ToString() =>
            $"nvencBusyCount={Busy}, nvencNeedMoreInputCount={NeedMoreInput}, " +
            $"nvencDroppedInputFrames={DroppedInput}, nvencDroppedOutputFrames={DroppedOutput}, " +
            $"nvencForcedIdrCount={ForcedIdr}, nvencFatalErrors={Fatal}, nvencFallbackCount={Fallback}";
    }

    public static Snapshot Take() => new(
        Interlocked.Read(ref BusyCount), Interlocked.Read(ref NeedMoreInputCount),
        Interlocked.Read(ref DroppedInputFrames), Interlocked.Read(ref DroppedOutputFrames),
        Interlocked.Read(ref ForcedIdrCount), Interlocked.Read(ref FatalErrors),
        Interlocked.Read(ref FallbackCount));
}
