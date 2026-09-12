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
                _badWgcSamples++;
                if (_badWgcSamples < RequiredBadWgcSamples)
                    return CaptureHealthDecision.Healthy;

                _badWgcSamples = 0;
                return new(true, $"WGC голодает {RequiredBadWgcSamples} секунд подряд");
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

        Quarantine(active, now,
            failureKind == CaptureFailureKind.BackendStalled
                ? CaptureQuarantine.ProcessSession
                : CaptureQuarantine.Transient);
        CaptureBackend alternative = CaptureBackendPolicy.Alternative(active);
        bool mayTryAlternative = CanUse(alternative, now) ||
                                 failureKind == CaptureFailureKind.BackendStalled;
        return mayTryAlternative && TryRecordSwitch(now)
            ? alternative
            : active;
    }
}
