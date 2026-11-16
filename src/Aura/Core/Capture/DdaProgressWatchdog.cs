namespace Aura.Core.Capture;

/// <summary>
/// Что сторож думает о продвижении потока DDA. Отличается от «нет новых кадров»:
/// на статичном рабочем столе кадров нет законно, а цикл захвата при этом обязан
/// возвращаться из AcquireNextFrame с WAIT_TIMEOUT.
/// </summary>
internal enum DdaProgressVerdict
{
    /// <summary>Данных ещё нет: поток не публиковал прогресс.</summary>
    NoData,
    /// <summary>Прогресс есть — поток жив, даже если кадров нет (статичный экран).</summary>
    Progressing,
    /// <summary>Сторож проснулся с опозданием: процесс стоял целиком. Это не зависание потока.</summary>
    Frozen,
    /// <summary>Прогресса нет, порог пройден, но подтверждений ещё мало.</summary>
    Suspect,
    /// <summary>Прогресса нет несколько наблюдений подряд — поток завис.</summary>
    Confirmed
}

/// <summary>Одно наблюдение снимка DDA. Часы — QPC, частота передаётся сторожу.</summary>
internal readonly record struct DdaProgressSample(
    long LoopSequence,
    long LastLoopProgressQpc,
    long PublishedQpc,
    DdaCapturePhase Phase,
    long Generation);

/// <summary>
/// Решает, завис ли поток Desktop Duplication. Детерминирован: время приходит
/// снаружи, поэтому проверяется без реального D3D и таймеров.
///
/// Правила:
/// • прогресс цикла (включая возврат с WAIT_TIMEOUT) продвигает счётчик и снимает
///   подозрение — статичный экран и Idle-сон при ограничении FPS зависанием не считаются;
/// • подтверждение требует нескольких наблюдений подряд: после общего фриза
///   устаревшая метка одного вызова ничего не доказывает;
/// • если сам сторож не просыпался дольше порога, наблюдение считается поздним:
///   подтверждения сбрасываются и восстановление начинается заново.
/// </summary>
internal sealed class DdaProgressWatchdog
{
    private readonly long _qpcFrequency;
    private readonly long _stallTicks;
    private readonly long _lateWakeTicks;
    private readonly int _requiredConfirmations;

    private bool _initialized;
    private long _generation;
    private long _lastLoopSequence;
    private long _lastProgressQpc;
    private long _lastObservationQpc;
    private int _confirmations;

    public DdaProgressWatchdog(
        TimeSpan stallThreshold,
        TimeSpan lateWakeGap,
        int requiredConfirmations = 2,
        long qpcFrequency = TimeSpan.TicksPerSecond)
    {
        if (stallThreshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(stallThreshold));
        if (lateWakeGap <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lateWakeGap));
        if (requiredConfirmations < 2) throw new ArgumentOutOfRangeException(nameof(requiredConfirmations));
        if (qpcFrequency <= 0) throw new ArgumentOutOfRangeException(nameof(qpcFrequency));

        _qpcFrequency = qpcFrequency;
        _stallTicks = ToTicks(stallThreshold);
        _lateWakeTicks = ToTicks(lateWakeGap);
        _requiredConfirmations = requiredConfirmations;
    }

    /// <summary>Сколько подтверждений подряд уже набрано — для лога.</summary>
    public int Confirmations => _confirmations;

    public DdaProgressVerdict Observe(in DdaProgressSample sample, long nowQpc)
    {
        if (sample.LastLoopProgressQpc <= 0)
        {
            Reset();
            return DdaProgressVerdict.NoData;
        }

        if (!_initialized || sample.Generation != _generation)
        {
            _initialized = true;
            _generation = sample.Generation;
            _lastLoopSequence = sample.LoopSequence;
            _lastProgressQpc = sample.LastLoopProgressQpc;
            _lastObservationQpc = nowQpc;
            _confirmations = 0;
            return DdaProgressVerdict.NoData;
        }

        long sinceLastObservation = nowQpc - _lastObservationQpc;
        _lastObservationQpc = nowQpc;

        bool advanced = sample.LoopSequence != _lastLoopSequence ||
                        sample.LastLoopProgressQpc > _lastProgressQpc;
        _lastLoopSequence = sample.LoopSequence;
        _lastProgressQpc = sample.LastLoopProgressQpc;
        if (advanced)
        {
            _confirmations = 0;
            return DdaProgressVerdict.Progressing;
        }

        // Мы сами не просыпались дольше порога: процесс (или весь ПК) стоял.
        // Считаем это наблюдение новым отсчётом, а не доказательством зависания.
        if (sinceLastObservation > _lateWakeTicks)
        {
            _confirmations = 0;
            return DdaProgressVerdict.Frozen;
        }

        long stalledTicks = nowQpc - sample.LastLoopProgressQpc;
        if (stalledTicks < _stallTicks)
        {
            _confirmations = 0;
            return DdaProgressVerdict.Progressing;
        }

        _confirmations++;
        return _confirmations < _requiredConfirmations
            ? DdaProgressVerdict.Suspect
            : DdaProgressVerdict.Confirmed;
    }

    public void Reset()
    {
        _initialized = false;
        _generation = 0;
        _lastLoopSequence = 0;
        _lastProgressQpc = 0;
        _lastObservationQpc = 0;
        _confirmations = 0;
    }

    private long ToTicks(TimeSpan span) =>
        (long)Math.Round(span.TotalSeconds * _qpcFrequency);
}
