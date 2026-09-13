namespace Aura.Core.Capture.GameHook;

internal enum GameHookHealthFailure
{
    None,
    NativeFailure,
    MissingHeartbeat,
    StaleHeartbeat,
    StalledFrames
}

internal readonly record struct GameHookHealthSample(
    bool CaptureEnabled,
    bool GraceElapsed,
    bool HeartbeatPresent,
    TimeSpan HeartbeatAge,
    TimeSpan FrameProgressAge,
    GameHookState State,
    GameHookError Error);

internal static class GameHookHealthPolicy
{
    private static readonly TimeSpan MaximumHeartbeatAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumFrameProgressAge = TimeSpan.FromSeconds(3);

    public static GameHookHealthFailure Evaluate(in GameHookHealthSample sample)
    {
        if (sample.State == GameHookState.Failed || sample.Error != GameHookError.None)
            return GameHookHealthFailure.NativeFailure;
        if (!sample.GraceElapsed) return GameHookHealthFailure.None;
        if (!sample.HeartbeatPresent) return GameHookHealthFailure.MissingHeartbeat;
        if (sample.HeartbeatAge > MaximumHeartbeatAge)
            return GameHookHealthFailure.StaleHeartbeat;
        if (sample.CaptureEnabled && sample.FrameProgressAge > MaximumFrameProgressAge)
            return GameHookHealthFailure.StalledFrames;
        return GameHookHealthFailure.None;
    }
}
