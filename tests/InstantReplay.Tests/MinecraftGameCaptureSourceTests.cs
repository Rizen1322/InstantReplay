using Aura.Core.Capture;
using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class MinecraftGameCaptureSourceTests
{
    [Fact]
    public void Foreground_starts_pending_and_only_fresh_game_frame_becomes_live()
    {
        var router = new MinecraftCaptureRouter(targetRevision: 4, minecraftForeground: true);
        long epoch = router.Current.Epoch;

        Assert.Equal(GameCaptureRoute.GamePending, router.Current.Route);
        Assert.False(router.TryAdmitMonitor(sequence: 1, out _));
        Assert.False(router.TryAdmitGame(epoch - 1, targetRevision: 4, sequence: 1, out _));
        Assert.True(router.TryAdmitGame(epoch, targetRevision: 4, sequence: 1, out var frame));
        Assert.Equal(GameCaptureInput.Game, frame.Input);
        Assert.Equal(epoch, frame.RouteEpoch);
        Assert.Equal(GameCaptureRoute.GameLive, router.Current.Route);
    }

    [Fact]
    public void Alt_tab_switches_to_monitor_and_rejects_in_flight_game_frame()
    {
        var router = LiveRouter(targetRevision: 7);
        long gameEpoch = router.Current.Epoch;

        GameCaptureRouteState monitor = router.ObserveForeground(
            minecraftForeground: false,
            targetRevision: 0);

        Assert.Equal(GameCaptureRoute.Monitor, monitor.Route);
        Assert.True(router.TryAdmitMonitor(sequence: 10, out var frame));
        Assert.Equal(monitor.Epoch, frame.RouteEpoch);
        Assert.False(router.TryAdmitGame(gameEpoch, targetRevision: 7, sequence: 2, out _));
    }

    [Fact]
    public void Returning_to_Minecraft_closes_monitor_before_hook_is_live()
    {
        var router = LiveRouter(targetRevision: 7);
        router.ObserveForeground(false, 0);
        Assert.True(router.TryAdmitMonitor(sequence: 1, out _));

        GameCaptureRouteState pending = router.ObserveForeground(true, 7);

        Assert.Equal(GameCaptureRoute.GamePending, pending.Route);
        Assert.False(router.TryAdmitMonitor(sequence: 2, out _));
        Assert.False(router.TryAdmitGame(pending.Epoch - 1, 7, sequence: 2, out _));
        Assert.True(router.TryAdmitGame(pending.Epoch, 7, sequence: 2, out _));
    }

    [Fact]
    public void Hook_failure_never_opens_monitor_while_Minecraft_is_foreground()
    {
        var router = new MinecraftCaptureRouter(targetRevision: 5, minecraftForeground: true);

        router.ObserveHookFailure();

        Assert.Equal(GameCaptureRoute.GamePending, router.Current.Route);
        Assert.False(router.TryAdmitMonitor(sequence: 100, out _));
    }

    [Fact]
    public void Replacement_target_gets_new_epoch_and_old_revision_is_rejected()
    {
        var router = LiveRouter(targetRevision: 3);
        long oldEpoch = router.Current.Epoch;

        GameCaptureRouteState replacement = router.ObserveForeground(true, targetRevision: 9);

        Assert.Equal(GameCaptureRoute.GamePending, replacement.Route);
        Assert.True(replacement.Epoch > oldEpoch);
        Assert.False(router.TryAdmitGame(oldEpoch, 3, sequence: 2, out _));
        Assert.False(router.TryAdmitGame(replacement.Epoch, 3, sequence: 2, out _));
        Assert.True(router.TryAdmitGame(replacement.Epoch, 9, sequence: 2, out _));
    }

    [Fact]
    public void Duplicate_sequence_is_never_published_twice()
    {
        var router = new MinecraftCaptureRouter(targetRevision: 4, minecraftForeground: true);
        long epoch = router.Current.Epoch;

        Assert.True(router.TryAdmitGame(epoch, 4, sequence: 20, out _));
        Assert.False(router.TryAdmitGame(epoch, 4, sequence: 20, out _));
        Assert.False(router.TryAdmitGame(epoch, 4, sequence: 19, out _));
        Assert.True(router.TryAdmitGame(epoch, 4, sequence: 21, out _));
    }

    private static MinecraftCaptureRouter LiveRouter(long targetRevision)
    {
        var router = new MinecraftCaptureRouter(targetRevision, minecraftForeground: true);
        long epoch = router.Current.Epoch;
        Assert.True(router.TryAdmitGame(epoch, targetRevision, sequence: 1, out _));
        return router;
    }
}
