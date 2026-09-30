namespace Aura.Core.Capture;

internal enum CaptureQuarantine
{
    Transient,
    ProcessSession
}

internal readonly record struct CaptureHealthSample(
    CaptureBackend Backend,
    int TargetFps,
    int FramesReceived,
    int FramesEncoded,
    int FramesDuplicated,
    bool GameForeground,
    TimeSpan Uptime);

internal readonly record struct CaptureHealthDecision(bool SwitchBackend, string Reason)
{
    public static CaptureHealthDecision Healthy => new(false, "");
}

/// <summary>
/// Решает, когда WGC действительно голодает под игровой нагрузкой, и хранит
/// защиту от бесконечного WGC↔DDA. Все часы приходят снаружи — логика детерминирована.
/// </summary>
internal sealed class CaptureHealthPolicy
{
    private const int RequiredBadWgcSamples = 10;
    private const int RequiredFrozenDdaSamples = 5;
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureQuarantine = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SwitchWindow = TimeSpan.FromMinutes(10);
    private const int MaxSwitchesPerWindow = 2;

    private readonly object _sync = new();
    private readonly Dictionary<CaptureBackend, DateTimeOffset> _transientQuarantine = [];
    private readonly HashSet<CaptureBackend> _sessionQuarantine = [];
    private readonly Queue<DateTimeOffset> _switches = [];
    private int _badWgcSamples;

    /// <summary>
    /// Пересборки захвата из-за «голода» подряд. Под тяжёлой игрой (Split Fiction,
    /// видеокарта 95%) WGC честно отдаёт 28–34 кадра в секунду: игра сама не рисует
    /// больше или DWM не успевает. Пересборка это не лечит, а каждая даёт дыру в
    /// записи до двух секунд, и сторож повторял её каждые 1–3 минуты. Теперь каждая
    /// неудачная попытка удваивает, сколько «голода» нужно для следующей: 10 с,
    /// 20, 40… до 10 минут. После 15 минут без пересборок счёт сбрасывается.
    /// </summary>
    private int _starvationSwitches;
    private DateTimeOffset _lastStarvationSwitch = DateTimeOffset.MinValue;
    private static readonly TimeSpan StarvationMemory = TimeSpan.FromMinutes(15);
    private const int MaxBadWgcSamples = 600;

    private int RequiredStarvationSamples =>
        Math.Min(MaxBadWgcSamples, RequiredBadWgcSamples << Math.Min(_starvationSwitches, 6));
    private int _frozenDdaSamples;

    public CaptureHealthDecision Observe(CaptureHealthSample sample, DateTimeOffset now)
    {
        lock (_sync)
        {
            bool starvedWgc = sample.Backend is CaptureBackend.Wgc or CaptureBackend.WgcWindow &&
                              sample.Uptime >= Warmup &&
                              sample.GameForeground &&
                              sample.TargetFps > 0 &&
                              sample.FramesReceived < sample.TargetFps * 0.60 &&
                              sample.FramesEncoded >= sample.TargetFps * 0.75 &&
                              sample.FramesDuplicated >= sample.FramesEncoded * 0.35;
            bool frozenDda = sample.Backend == CaptureBackend.DesktopDuplication &&
                             sample.Uptime >= Warmup &&
                             sample.GameForeground &&
                             sample.TargetFps > 0 &&
                             sample.FramesReceived <= 1 &&
                             sample.FramesEncoded >= sample.TargetFps * 0.75 &&
                             sample.FramesDuplicated >= sample.FramesEncoded * 0.80;

            if (starvedWgc)
            {
                _frozenDdaSamples = 0;
                if (now - _lastStarvationSwitch > StarvationMemory) _starvationSwitches = 0;
                _badWgcSamples++;
                int required = RequiredStarvationSamples;
                if (_badWgcSamples < required)
                    return CaptureHealthDecision.Healthy;

                _badWgcSamples = 0;
                _starvationSwitches++;
                _lastStarvationSwitch = now;
                return new(true, $"WGC голодает {required} секунд подряд" +
                                 (_starvationSwitches > 1 ? $" (пересборка №{_starvationSwitches} за 15 мин, дальше реже)" : ""));
            }

            if (frozenDda)
            {
                _badWgcSamples = 0;
                _frozenDdaSamples++;
                if (_frozenDdaSamples < RequiredFrozenDdaSamples)
                    return CaptureHealthDecision.Healthy;

                _frozenDdaSamples = 0;
                return new(true, $"DDA не обновляет игру {RequiredFrozenDdaSamples} секунд подряд");
            }

            _badWgcSamples = 0;
            _frozenDdaSamples = 0;
            return CaptureHealthDecision.Healthy;
        }
    }

    public void Quarantine(CaptureBackend backend, DateTimeOffset now, CaptureQuarantine duration)
    {
        lock (_sync)
        {
            if (duration == CaptureQuarantine.ProcessSession)
                _sessionQuarantine.Add(backend);
            else
                _transientQuarantine[backend] = now + FailureQuarantine;
        }
    }

    /// <summary>
    /// Замолчавший способ захвата отстраняется с нарастанием: 15 минут, потом
    /// час, и только с третьего раза до конца сеанса. Раньше WGC запрещался до
    /// перезапуска с первого же раза, хотя замолкал он обычно из-за одной
    /// конкретной игры или разовой заминки драйвера, и дальше весь день
    /// писался заметно более тяжёлый Desktop Duplication.
    /// </summary>
    private void QuarantineStalled(CaptureBackend backend, DateTimeOffset now)
    {
        lock (_sync)
        {
            int stalls = _stalls.TryGetValue(backend, out int n) ? n + 1 : 1;
            _stalls[backend] = stalls;
            if (stalls >= 3) _sessionQuarantine.Add(backend);
            else _transientQuarantine[backend] = now + (stalls == 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1));
        }
    }

    private readonly Dictionary<CaptureBackend, int> _stalls = [];

    public bool CanUse(CaptureBackend backend, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_sessionQuarantine.Contains(backend)) return false;
            return !_transientQuarantine.TryGetValue(backend, out var until) || now >= until;
        }
    }

    public bool TryRecordSwitch(DateTimeOffset now)
    {
        lock (_sync)
        {
            while (_switches.TryPeek(out var first) && now - first >= SwitchWindow)
                _switches.Dequeue();
            if (_switches.Count >= MaxSwitchesPerWindow) return false;
            _switches.Enqueue(now);
            return true;
        }
    }

    public CaptureBackend SelectAfterFailure(
        CaptureBackend active,
        CaptureFailureKind failureKind,
        bool backendForced,
        DateTimeOffset now,
        CaptureBackend? preferredBackend = null)
    {
        if (backendForced || failureKind == CaptureFailureKind.CaptureFormatChanged)
            return active;
        if (failureKind == CaptureFailureKind.DeviceLost)
        {
            CaptureBackend preferred = preferredBackend ?? active;
            return CanUse(preferred, now) ? preferred : active;
        }

        if (failureKind == CaptureFailureKind.BackendStalled) QuarantineStalled(active, now);
        else Quarantine(active, now, CaptureQuarantine.Transient);
        CaptureBackend alternative = CaptureBackendPolicy.Alternative(active);
        bool mayTryAlternative = CanUse(alternative, now) ||
                                 failureKind == CaptureFailureKind.BackendStalled;
        return mayTryAlternative && TryRecordSwitch(now)
            ? alternative
            : active;
    }
}
