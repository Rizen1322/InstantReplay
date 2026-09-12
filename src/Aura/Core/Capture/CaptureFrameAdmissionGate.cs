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
    UnexpectedWindow
}

/// <summary>Не допускает кадр старого provider/target в новый GPU-брокер.</summary>
internal sealed class CaptureFrameAdmissionGate
{
    private readonly long _generation;
    private readonly long _targetRevision;
    private readonly bool _windowEpisode;

    public CaptureFrameAdmissionGate(
        long generation,
        long targetRevision,
        bool windowEpisode)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (windowEpisode && targetRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRevision));
        if (!windowEpisode && targetRevision != 0)
            throw new ArgumentException("Мониторный эпизод не имеет target revision", nameof(targetRevision));

        _generation = generation;
        _targetRevision = targetRevision;
        _windowEpisode = windowEpisode;
    }

    public CaptureFrameAdmission Evaluate(
        long generation,
        long targetRevision,
        CaptureSurfaceScope scope)
    {
        if (generation != _generation) return CaptureFrameAdmission.StaleGeneration;

        if (_windowEpisode)
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
        CaptureSurfaceScope scope) =>
        Evaluate(generation, targetRevision, scope) == CaptureFrameAdmission.Admit;
}
