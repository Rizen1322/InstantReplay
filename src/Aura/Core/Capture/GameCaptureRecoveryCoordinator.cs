namespace Aura.Core.Capture;

/// <summary>Потокобезопасно связывает foreground target и чистую recovery-policy.</summary>
internal sealed class GameCaptureRecoveryCoordinator
{
    private readonly CaptureBackend _preferredMonitorBackend;
    private readonly bool _forcedBackend;
    private readonly bool _allowWgc;
    private readonly CaptureHealthPolicy _health;
    private readonly object _sync = new();
    private GameCaptureTarget? _target;
    private CaptureEpisode _episode;
    private CaptureEpisode _lastHookFailure;
    private bool _stopped;

    public GameCaptureRecoveryCoordinator(
        CaptureBackend preferredMonitorBackend,
        bool forcedBackend,
        bool allowWgc = true,
        CaptureHealthPolicy? health = null)
    {
        _allowWgc = allowWgc;
        _health = health ?? new CaptureHealthPolicy();
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

    /// <summary>
    /// До следующего включения повтора WGC не выбирать ни в каком виде, как на
    /// Windows 10. Ставится, когда запись ушла с WGC ради OpenGL-игры: иначе
    /// первая же потеря дупликации на Alt-Tab возвращала WGC (монитора или окна),
    /// и курсор в игре пропадал снова.
    /// </summary>
    public bool AvoidWgc
    {
        get { lock (_sync) return _avoidWgc; }
        set { lock (_sync) _avoidWgc = value; }
    }
    private bool _avoidWgc;

    public void Resume()
    {
        lock (_sync)
        {
            _avoidWgc = false;
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
        out CaptureRecoveryDecision decision,
        DateTimeOffset? now = null)
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
                _allowWgc && !_avoidWgc));
            if (_target is null && _allowWgc && !_avoidWgc &&
                activeBackend is CaptureBackend.Wgc or CaptureBackend.DesktopDuplication)
            {
                var backend = _health.SelectAfterFailure(activeBackend, failureKind,
                    _forcedBackend, now ?? DateTimeOffset.UtcNow, activeBackend);
                decision = decision with { Backend = backend };
            }
            // A successful start followed by an immediate failure is a new recovery,
            // so its local attempt counter alone cannot limit repeated rebuilds.
            if (decision.Action == CaptureRecoveryAction.Restart && decision.Backend == activeBackend &&
                failureKind is not (CaptureFailureKind.DeviceLost or CaptureFailureKind.CaptureFormatChanged))
                decision = decision with { RetryDelay = TimeSpan.FromSeconds(3) };
            _episode = decision.Episode;
            if (_episode.IsQuarantined(CaptureBackend.MinecraftOpenGl))
                _lastHookFailure = _episode;
            return true;
        }
    }

    public bool TryReturnToPreferred(CaptureBackend active, DateTimeOffset now, out CaptureRecoveryDecision decision)
    {
        lock (_sync)
        {
            decision = default;
            if (_stopped || _forcedBackend || _target is not null || _avoidWgc ||
                active == _preferredMonitorBackend ||
                active is not (CaptureBackend.Wgc or CaptureBackend.DesktopDuplication) ||
                (!_allowWgc && _preferredMonitorBackend == CaptureBackend.Wgc) ||
                !_health.CanUse(_preferredMonitorBackend, now) || !_health.TryRecordSwitch(now)) return false;
            decision = new(CaptureRecoveryAction.Restart, _preferredMonitorBackend, 0,
                TimeSpan.Zero, CaptureEpisode.Empty);
            return true;
        }
    }
}
