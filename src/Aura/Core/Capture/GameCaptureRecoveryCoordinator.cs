namespace Aura.Core.Capture;

/// <summary>Потокобезопасно связывает foreground target и чистую recovery-policy.</summary>
internal sealed class GameCaptureRecoveryCoordinator
{
    private readonly CaptureBackend _preferredMonitorBackend;
    private readonly bool _forcedBackend;
    private readonly bool _allowWgc;
    private readonly object _sync = new();
    private GameCaptureTarget? _target;
    private CaptureEpisode _episode;
    private CaptureEpisode _lastHookFailure;
    private bool _stopped;

    public GameCaptureRecoveryCoordinator(
        CaptureBackend preferredMonitorBackend,
        bool forcedBackend,
        bool allowWgc = true)
    {
        _allowWgc = allowWgc;
        if (preferredMonitorBackend is CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl)
            throw new ArgumentException("Предпочтительный backend должен захватывать монитор", nameof(preferredMonitorBackend));
        _preferredMonitorBackend = preferredMonitorBackend;
        _forcedBackend = forcedBackend;
    }

    public CaptureEpisode Episode
    {
        get { lock (_sync) return _episode; }
    }

    public GameCaptureTarget? Target
    {
        get { lock (_sync) return _target; }
    }

    public void Resume()
    {
        lock (_sync)
        {
            _stopped = false;
            _target = null;
            _episode = CaptureEpisode.Empty;
            _lastHookFailure = CaptureEpisode.Empty;
        }
    }

    public void Stop()
    {
        lock (_sync) _stopped = true;
    }

    public void ObserveTarget(GameCaptureTarget? target)
    {
        lock (_sync)
        {
            _target = target;
            _episode = target is GameCaptureTarget current
                ? _episode.Align(current)
                : CaptureEpisode.Empty;
            // Alt-Tab сбрасывает мониторный эпизод, но не лечит отказавший хук.
            // Ревизия геометрии тоже не означает перезапуск javaw/его DLL.
            if (target is GameCaptureTarget returned &&
                _lastHookFailure.TargetHwnd == returned.Hwnd &&
                _lastHookFailure.TargetProcessId == returned.ProcessId &&
                _lastHookFailure.TargetProcessStartTicks == returned.ProcessStartTicks)
                _episode = _episode.Quarantine(CaptureBackend.MinecraftOpenGl);
        }
    }

    public bool TryDecide(
        CaptureBackend activeBackend,
        CaptureFailureKind failureKind,
        out CaptureRecoveryDecision decision)
    {
        lock (_sync)
        {
            if (_stopped)
            {
                decision = default;
                return false;
            }

            decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
                activeBackend,
                failureKind,
                _forcedBackend,
                _target,
                _episode,
                _preferredMonitorBackend,
                _allowWgc));
            _episode = decision.Episode;
            if (_episode.IsQuarantined(CaptureBackend.MinecraftOpenGl))
                _lastHookFailure = _episode;
            return true;
        }
    }
}
