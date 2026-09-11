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
    private const int RequiredBadSamples = 10;
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureQuarantine = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SwitchWindow = TimeSpan.FromMinutes(10);
    private const int MaxSwitchesPerWindow = 2;

    private readonly object _sync = new();
    private readonly Dictionary<CaptureBackend, DateTimeOffset> _transientQuarantine = [];
    private readonly HashSet<CaptureBackend> _sessionQuarantine = [];
    private readonly Queue<DateTimeOffset> _switches = [];
    private int _badWgcSamples;

    public CaptureHealthDecision Observe(CaptureHealthSample sample, DateTimeOffset now)
    {
        lock (_sync)
        {
            bool starvedWgc = sample.Backend == CaptureBackend.Wgc &&
                              sample.Uptime >= Warmup &&
                              sample.GameForeground &&
                              sample.TargetFps > 0 &&
                              sample.FramesReceived < sample.TargetFps * 0.60 &&
                              sample.FramesEncoded >= sample.TargetFps * 0.75 &&
                              sample.FramesDuplicated >= sample.FramesEncoded * 0.35;

            if (!starvedWgc)
            {
                _badWgcSamples = 0;
                return CaptureHealthDecision.Healthy;
            }

            _badWgcSamples++;
            if (_badWgcSamples < RequiredBadSamples)
                return CaptureHealthDecision.Healthy;

            _badWgcSamples = 0;
            return new(true, $"WGC голодает {RequiredBadSamples} секунд подряд");
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
        DateTimeOffset now)
    {
        if (backendForced || failureKind == CaptureFailureKind.DeviceLost)
            return active;

        Quarantine(active, now,
            failureKind == CaptureFailureKind.BackendStalled
                ? CaptureQuarantine.ProcessSession
                : CaptureQuarantine.Transient);
        CaptureBackend alternative = CaptureBackendPolicy.Alternative(active);
        return CanUse(alternative, now) && TryRecordSwitch(now)
            ? alternative
            : active;
    }
}
