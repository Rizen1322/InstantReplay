namespace Aura.Core.Capture.GameHook;

internal enum GameCaptureRoute
{
    Monitor,
    GamePending,
    GameLive
}

internal enum GameCaptureInput
{
    Monitor,
    Game
}

internal enum GameCaptureRouteAdmission
{
    Admit,
    StaleEpoch,
    StaleTarget,
    MonitorBlocked,
    GamePending,
    UnexpectedGame
}

internal readonly record struct GameCaptureRouteState(
    GameCaptureRoute Route,
    long Epoch,
    long TargetRevision);

internal readonly record struct GameCaptureRouteFrameDecision(
    GameCaptureRouteState State,
    GameCaptureRouteAdmission Admission);

/// <summary>
/// Чистая политика маршрута. Epoch закрывает уже летящие callbacks предыдущего
/// источника до того, как они смогут опубликовать текстуру в GPU-брокер.
/// </summary>
internal static class GameCaptureRoutePolicy
{
    public static GameCaptureRouteState CreateInitial() =>
        new(GameCaptureRoute.Monitor, Epoch: 1, TargetRevision: 0);

    public static GameCaptureRouteState ObserveForeground(
        in GameCaptureRouteState state,
        bool minecraftForeground,
        long targetRevision)
    {
        ValidateState(state);

        if (!minecraftForeground)
        {
            if (targetRevision != 0)
                throw new ArgumentException(
                    "Мониторный маршрут не имеет target revision",
                    nameof(targetRevision));

            return state.Route == GameCaptureRoute.Monitor
                ? state
                : new GameCaptureRouteState(
                    GameCaptureRoute.Monitor,
                    checked(state.Epoch + 1),
                    TargetRevision: 0);
        }

        if (targetRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRevision));

        bool sameGameEpisode =
            state.Route is GameCaptureRoute.GamePending or GameCaptureRoute.GameLive &&
            state.TargetRevision == targetRevision;
        if (sameGameEpisode)
            return state;

        return new GameCaptureRouteState(
            GameCaptureRoute.GamePending,
            checked(state.Epoch + 1),
            targetRevision);
    }

    public static GameCaptureRouteAdmission Evaluate(
        in GameCaptureRouteState state,
        GameCaptureInput input,
        long frameEpoch,
        long targetRevision)
    {
        ValidateState(state);

        if (frameEpoch != state.Epoch)
            return GameCaptureRouteAdmission.StaleEpoch;

        if (input == GameCaptureInput.Monitor)
        {
            return state.Route == GameCaptureRoute.Monitor
                ? GameCaptureRouteAdmission.Admit
                : GameCaptureRouteAdmission.MonitorBlocked;
        }

        if (state.Route == GameCaptureRoute.Monitor)
            return GameCaptureRouteAdmission.UnexpectedGame;
        if (targetRevision != state.TargetRevision)
            return GameCaptureRouteAdmission.StaleTarget;

        return state.Route == GameCaptureRoute.GameLive
            ? GameCaptureRouteAdmission.Admit
            : GameCaptureRouteAdmission.GamePending;
    }

    public static GameCaptureRouteFrameDecision ObserveGameFrame(
        in GameCaptureRouteState state,
        long frameEpoch,
        long targetRevision)
    {
        GameCaptureRouteAdmission admission = Evaluate(
            state,
            GameCaptureInput.Game,
            frameEpoch,
            targetRevision);

        if (admission != GameCaptureRouteAdmission.GamePending)
            return new GameCaptureRouteFrameDecision(state, admission);

        return new GameCaptureRouteFrameDecision(
            state with { Route = GameCaptureRoute.GameLive },
            GameCaptureRouteAdmission.Admit);
    }

    private static void ValidateState(in GameCaptureRouteState state)
    {
        if (state.Epoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Route epoch должен быть положительным");

        bool hasTarget = state.TargetRevision > 0;
        if ((state.Route == GameCaptureRoute.Monitor && hasTarget) ||
            (state.Route != GameCaptureRoute.Monitor && !hasTarget))
        {
            throw new ArgumentException("Route и target revision противоречат друг другу", nameof(state));
        }
    }
}
