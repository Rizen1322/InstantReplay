namespace Aura.Core.Capture;

/// <summary>
/// Фаза, в которой сейчас находится поток захвата Desktop Duplication.
///
/// ЗАЧЕМ. По логу «нет новых кадров» нельзя понять, где именно встал поток: на
/// AcquireNextFrame, внутри обработчика кадра (там синхронно работает брокер) или
/// на ReleaseFrame. Снимок фазы отвечает на этот вопрос, даже когда весь поток
/// висит внутри драйвера и пишет в лог уже нечего.
/// </summary>
internal enum DdaCapturePhase
{
    None = 0,
    CreateDevice,
    DuplicateOutput1,
    DuplicateOutput,
    Prime,
    Acquire,
    Cursor,
    Rotate,
    FrameArrived,
    ReleaseFrame,
    Idle,
    Stopping
}

/// <summary>
/// Согласованный во времени снимок состояния потока DDA. Все поля относятся к
/// одному моменту: публикуется целиком через seqlock, а не набором независимых
/// полей, которые читатель мог бы увидеть из разных мгновений.
///
/// Снимок не трогает ни один замок захвата и не вызывает D3D/DXGI: сторож,
/// который его читает, не должен повиснуть на том же замке, что и поток захвата.
/// </summary>
internal readonly record struct CaptureDiagnosticsSnapshot(
    string Source,
    DdaCapturePhase Phase,
    long PhaseSinceQpc,
    long LastLoopProgressQpc,
    long LoopSequence,
    long FramesReceived,
    long FramesAccepted,
    int LastHResult,
    int ManagedThreadId,
    uint NativeThreadId,
    long Generation,
    string MonitorDeviceId,
    long PublishedQpc)
{
    public static CaptureDiagnosticsSnapshot Empty => default;

    public bool HasData => Source.Length > 0 && Phase != DdaCapturePhase.None;

    public static double Seconds(long ticks, long qpcFrequency) =>
        qpcFrequency <= 0 ? 0 : ticks / (double)qpcFrequency;

    /// <summary>Сколько секунд поток находится в текущей фазе.</summary>
    public double PhaseSeconds(long nowQpc, long qpcFrequency) =>
        Seconds(Math.Max(0, nowQpc - PhaseSinceQpc), qpcFrequency);

    /// <summary>Сколько секунд назад поток последний раз возвращался из цикла.</summary>
    public double SinceLoopProgressSeconds(long nowQpc, long qpcFrequency) =>
        LastLoopProgressQpc <= 0 ? 0 : Seconds(Math.Max(0, nowQpc - LastLoopProgressQpc), qpcFrequency);

    public string HResultText => LastHResult == 0
        ? "S_OK"
        : $"0x{unchecked((uint)LastHResult):X8}";

    /// <summary>
    /// Строка для лога и файла-контекста дампа. Часы передаются снаружи: снимок
    /// детерминирован и в тестах считается без реального времени.
    /// </summary>
    public string Describe(long nowQpc, long qpcFrequency)
    {
        if (!HasData) return "снимок DDA недоступен (поток ещё не публиковал состояние)";
        return $"DDA {Source}: фаза {Phase} {PhaseSeconds(nowQpc, qpcFrequency):F1} с, " +
               $"цикл {LoopSequence}, с последнего возврата {SinceLoopProgressSeconds(nowQpc, qpcFrequency):F1} с, " +
               $"получено {FramesReceived}, доставлено {FramesAccepted}, HRESULT {HResultText}, " +
               $"поток managed {ManagedThreadId}/native {NativeThreadId}, генерация {Generation}, " +
               $"выход {MonitorDeviceId}";
    }
}

/// <summary>
/// Источник захвата, который умеет отдать согласованный снимок своего потока без
/// блокировок и без обращений к D3D/DXGI. Реализуется DDA (и обёртками, внутри
/// которых он работает, например мониторной частью Minecraft).
/// </summary>
internal interface ICaptureDiagnostics
{
    bool TryGetDiagnostics(out CaptureDiagnosticsSnapshot snapshot);
}
