namespace Aura.Core.Capture;

internal enum CaptureSurfaceScope
{
    Monitor,
    GameWindow
}

internal enum CaptureFrameAdmission
{
    Admit,
    StaleGeneration,
    StaleTarget,
    MonitorBlocked,
    UnexpectedWindow,
    StaleRoute
}

internal enum CaptureFrameAdmissionMode
{
    Monitor,
    Window,
    Hybrid
}

/// <summary>Не допускает кадр старого provider/target в новый GPU-брокер.</summary>
internal sealed class CaptureFrameAdmissionGate
{
    private readonly long _generation;
    private readonly long _targetRevision;
    private readonly CaptureFrameAdmissionMode _mode;
    private long _latestRouteEpoch;

    public CaptureFrameAdmissionGate(
        long generation,
        long targetRevision,
        bool windowEpisode)
        : this(
            generation,
            targetRevision,
            windowEpisode ? CaptureFrameAdmissionMode.Window : CaptureFrameAdmissionMode.Monitor)
    {
    }

    public CaptureFrameAdmissionGate(
        long generation,
        long targetRevision,
        CaptureFrameAdmissionMode mode)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (mode != CaptureFrameAdmissionMode.Monitor && targetRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRevision));
        if (mode == CaptureFrameAdmissionMode.Monitor && targetRevision != 0)
            throw new ArgumentException("Мониторный эпизод не имеет target revision", nameof(targetRevision));

        _generation = generation;
        _targetRevision = targetRevision;
        _mode = mode;
    }

    public CaptureFrameAdmission Evaluate(
        long generation,
        long targetRevision,
        CaptureSurfaceScope scope,
        long routeEpoch = 0)
    {
        if (generation != _generation) return CaptureFrameAdmission.StaleGeneration;

        if (_mode == CaptureFrameAdmissionMode.Hybrid)
        {
            if (routeEpoch <= 0 || routeEpoch < _latestRouteEpoch)
                return CaptureFrameAdmission.StaleRoute;
            if (routeEpoch > _latestRouteEpoch)
                _latestRouteEpoch = routeEpoch;

            if (scope == CaptureSurfaceScope.GameWindow)
                return targetRevision == _targetRevision
                    ? CaptureFrameAdmission.Admit
                    : CaptureFrameAdmission.StaleTarget;
            return targetRevision == 0
                ? CaptureFrameAdmission.Admit
                : CaptureFrameAdmission.StaleTarget;
        }

        if (_mode == CaptureFrameAdmissionMode.Window)
        {
            if (scope == CaptureSurfaceScope.Monitor)
                return CaptureFrameAdmission.MonitorBlocked;
            if (targetRevision != _targetRevision)
                return CaptureFrameAdmission.StaleTarget;
            return CaptureFrameAdmission.Admit;
        }

        return scope == CaptureSurfaceScope.GameWindow
            ? CaptureFrameAdmission.UnexpectedWindow
            : CaptureFrameAdmission.Admit;
    }

    public bool Accept(
        long generation,
        long targetRevision,
        CaptureSurfaceScope scope,
        long routeEpoch = 0) =>
        Evaluate(generation, targetRevision, scope, routeEpoch) == CaptureFrameAdmission.Admit;
}
