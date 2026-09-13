using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameCaptureRoutePolicyTests
{
    [Fact]
    public void Fresh_game_frame_is_required_to_promote_pending_route_to_live()
    {
        GameCaptureRouteState initial = GameCaptureRoutePolicy.CreateInitial();
        GameCaptureRouteState pending = GameCaptureRoutePolicy.ObserveForeground(
            initial,
            minecraftForeground: true,
            targetRevision: 7);

        Assert.Equal(GameCaptureRoute.Monitor, initial.Route);
        Assert.Equal(GameCaptureRoute.GamePending, pending.Route);
        Assert.Equal(initial.Epoch + 1, pending.Epoch);
        Assert.Equal(7, pending.TargetRevision);
        Assert.Equal(
            GameCaptureRouteAdmission.MonitorBlocked,
            GameCaptureRoutePolicy.Evaluate(pending, GameCaptureInput.Monitor, pending.Epoch, 0));

        GameCaptureRouteFrameDecision accepted = GameCaptureRoutePolicy.ObserveGameFrame(
            pending,
            pending.Epoch,
            targetRevision: 7);

        Assert.Equal(GameCaptureRouteAdmission.Admit, accepted.Admission);
        Assert.Equal(GameCaptureRoute.GameLive, accepted.State.Route);
        Assert.Equal(pending.Epoch, accepted.State.Epoch);
    }

    [Fact]
    public void Alt_tab_immediately_enters_monitor_and_rejects_old_game_callback()
    {
        GameCaptureRouteState live = MakeLive(targetRevision: 11);
        GameCaptureRouteState monitor = GameCaptureRoutePolicy.ObserveForeground(
            live,
            minecraftForeground: false,
            targetRevision: 0);

        Assert.Equal(GameCaptureRoute.Monitor, monitor.Route);
        Assert.Equal(live.Epoch + 1, monitor.Epoch);
        Assert.Equal(0, monitor.TargetRevision);
        Assert.Equal(
            GameCaptureRouteAdmission.Admit,
            GameCaptureRoutePolicy.Evaluate(monitor, GameCaptureInput.Monitor, monitor.Epoch, 0));
        Assert.Equal(
            GameCaptureRouteAdmission.StaleEpoch,
            GameCaptureRoutePolicy.Evaluate(monitor, GameCaptureInput.Game, live.Epoch, 11));
    }

    [Fact]
    public void Returning_to_game_closes_monitor_before_new_game_frame_arrives()
    {
        GameCaptureRouteState live = MakeLive(targetRevision: 4);
        GameCaptureRouteState monitor = GameCaptureRoutePolicy.ObserveForeground(
            live,
            minecraftForeground: false,
            targetRevision: 0);
        GameCaptureRouteState pending = GameCaptureRoutePolicy.ObserveForeground(
            monitor,
            minecraftForeground: true,
            targetRevision: 4);

        Assert.Equal(GameCaptureRoute.GamePending, pending.Route);
        Assert.Equal(monitor.Epoch + 1, pending.Epoch);
        Assert.Equal(
            GameCaptureRouteAdmission.StaleEpoch,
            GameCaptureRoutePolicy.Evaluate(pending, GameCaptureInput.Monitor, monitor.Epoch, 0));
        Assert.Equal(
            GameCaptureRouteAdmission.MonitorBlocked,
            GameCaptureRoutePolicy.Evaluate(pending, GameCaptureInput.Monitor, pending.Epoch, 0));
    }

    [Fact]
    public void Target_revision_change_starts_a_new_pending_epoch()
    {
        GameCaptureRouteState live = MakeLive(targetRevision: 3);
        GameCaptureRouteState replacement = GameCaptureRoutePolicy.ObserveForeground(
            live,
            minecraftForeground: true,
            targetRevision: 9);

        Assert.Equal(GameCaptureRoute.GamePending, replacement.Route);
        Assert.Equal(live.Epoch + 1, replacement.Epoch);
        Assert.Equal(9, replacement.TargetRevision);
        Assert.Equal(
            GameCaptureRouteAdmission.StaleEpoch,
            GameCaptureRoutePolicy.ObserveGameFrame(replacement, live.Epoch, 3).Admission);
        Assert.Equal(
            GameCaptureRouteAdmission.StaleTarget,
            GameCaptureRoutePolicy.ObserveGameFrame(replacement, replacement.Epoch, 3).Admission);
    }

    [Fact]
    public void Repeated_observation_of_same_focus_and_target_is_idempotent()
    {
        GameCaptureRouteState live = MakeLive(targetRevision: 5);

        Assert.Equal(
            live,
            GameCaptureRoutePolicy.ObserveForeground(live, minecraftForeground: true, 5));

        GameCaptureRouteState monitor = GameCaptureRoutePolicy.ObserveForeground(
            live,
            minecraftForeground: false,
            targetRevision: 0);
        Assert.Equal(
            monitor,
            GameCaptureRoutePolicy.ObserveForeground(monitor, minecraftForeground: false, 0));
    }

    [Fact]
    public void Invalid_target_revision_cannot_open_a_game_epoch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GameCaptureRoutePolicy.ObserveForeground(
                GameCaptureRoutePolicy.CreateInitial(),
                minecraftForeground: true,
                targetRevision: 0));
    }

    private static GameCaptureRouteState MakeLive(long targetRevision)
    {
        GameCaptureRouteState pending = GameCaptureRoutePolicy.ObserveForeground(
            GameCaptureRoutePolicy.CreateInitial(),
            minecraftForeground: true,
            targetRevision);
        return GameCaptureRoutePolicy.ObserveGameFrame(
            pending,
            pending.Epoch,
            targetRevision).State;
    }
}
