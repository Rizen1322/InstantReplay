using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameHookHealthPolicyTests
{
    [Fact]
    public void Idle_route_does_not_require_present_progress()
    {
        GameHookHealthFailure failure = GameHookHealthPolicy.Evaluate(new(
            CaptureEnabled: false,
            GraceElapsed: true,
            HeartbeatPresent: true,
            HeartbeatAge: TimeSpan.FromMilliseconds(20),
            FrameProgressAge: TimeSpan.FromMinutes(1),
            State: GameHookState.Ready,
            Error: GameHookError.None));

        Assert.Equal(GameHookHealthFailure.None, failure);
    }

    [Theory]
    [InlineData(false, 0, 0, (int)GameHookState.Ready, (int)GameHookError.None,
        (int)GameHookHealthFailure.MissingHeartbeat)]
    [InlineData(true, 3000, 0, (int)GameHookState.Capturing, (int)GameHookError.None,
        (int)GameHookHealthFailure.StaleHeartbeat)]
    [InlineData(true, 20, 4000, (int)GameHookState.Capturing, (int)GameHookError.None,
        (int)GameHookHealthFailure.StalledFrames)]
    [InlineData(true, 20, 20, (int)GameHookState.Failed, (int)GameHookError.CaptureFailed,
        (int)GameHookHealthFailure.NativeFailure)]
    public void Unhealthy_live_hook_is_reported(
        bool heartbeatPresent,
        int heartbeatAgeMs,
        int frameAgeMs,
        int state,
        int error,
        int expected)
    {
        GameHookHealthFailure failure = GameHookHealthPolicy.Evaluate(new(
            CaptureEnabled: true,
            GraceElapsed: true,
            HeartbeatPresent: heartbeatPresent,
            HeartbeatAge: TimeSpan.FromMilliseconds(heartbeatAgeMs),
            FrameProgressAge: TimeSpan.FromMilliseconds(frameAgeMs),
            State: (GameHookState)state,
            Error: (GameHookError)error));

        Assert.Equal((GameHookHealthFailure)expected, failure);
    }

    [Fact]
    public void Startup_grace_suppresses_missing_heartbeat_and_frames()
    {
        GameHookHealthFailure failure = GameHookHealthPolicy.Evaluate(new(
            CaptureEnabled: true,
            GraceElapsed: false,
            HeartbeatPresent: false,
            HeartbeatAge: TimeSpan.MaxValue,
            FrameProgressAge: TimeSpan.MaxValue,
            State: GameHookState.Empty,
            Error: GameHookError.None));

        Assert.Equal(GameHookHealthFailure.None, failure);
    }
}
