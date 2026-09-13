using Aura.Core.Capture.GameHook;

namespace Aura.Core.Capture;

internal readonly record struct HybridCaptureFrameToken(
    GameCaptureInput Input,
    long RouteEpoch,
    long TargetRevision,
    long Sequence);

/// <summary>Сериализует focus transitions и frame admission одним lock.</summary>
internal sealed class MinecraftCaptureRouter
{
    private readonly object _sync = new();
    private GameCaptureRouteState _state;
    private long _lastMonitorSequence;
    private long _lastGameSequence;

    public MinecraftCaptureRouter(long targetRevision, bool minecraftForeground)
    {
        if (targetRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRevision));

        _state = GameCaptureRoutePolicy.CreateInitial();
        if (minecraftForeground)
            _state = GameCaptureRoutePolicy.ObserveForeground(_state, true, targetRevision);
    }

    public GameCaptureRouteState Current
    {
        get { lock (_sync) return _state; }
    }

    public GameCaptureRouteState ObserveForeground(bool minecraftForeground, long targetRevision)
    {
        lock (_sync)
        {
            long previousTarget = _state.TargetRevision;
            _state = GameCaptureRoutePolicy.ObserveForeground(
                _state,
                minecraftForeground,
                targetRevision);
            if (minecraftForeground && previousTarget != 0 && previousTarget != targetRevision)
                _lastGameSequence = 0;
            return _state;
        }
    }

    public void ObserveHookFailure()
    {
        lock (_sync)
        {
            // Fail closed: foreground Minecraft never falls through to monitor frames.
        }
    }

    public bool TryAdmitMonitor(long sequence, out HybridCaptureFrameToken frame)
    {
        lock (_sync)
        {
            frame = default;
            if (sequence <= _lastMonitorSequence) return false;
            if (GameCaptureRoutePolicy.Evaluate(
                    _state,
                    GameCaptureInput.Monitor,
                    _state.Epoch,
                    0) != GameCaptureRouteAdmission.Admit) return false;

            _lastMonitorSequence = sequence;
            frame = new HybridCaptureFrameToken(
                GameCaptureInput.Monitor,
                _state.Epoch,
                TargetRevision: 0,
                sequence);
            return true;
        }
    }

    public bool TryAdmitGame(
        long routeEpoch,
        long targetRevision,
        long sequence,
        out HybridCaptureFrameToken frame)
    {
        lock (_sync)
        {
            frame = default;
            if (sequence <= _lastGameSequence) return false;

            GameCaptureRouteFrameDecision decision = GameCaptureRoutePolicy.ObserveGameFrame(
                _state,
                routeEpoch,
                targetRevision);
            if (decision.Admission != GameCaptureRouteAdmission.Admit) return false;

            _state = decision.State;
            _lastGameSequence = sequence;
            frame = new HybridCaptureFrameToken(
                GameCaptureInput.Game,
                routeEpoch,
                targetRevision,
                sequence);
            return true;
        }
    }
}
