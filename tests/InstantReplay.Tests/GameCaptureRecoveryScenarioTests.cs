using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameCaptureRecoveryScenarioTests
{
    [Fact]
    public void Healthy_fallback_returns_after_quarantine_but_not_during_an_opengl_game()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new CaptureHealthPolicy();
        var coordinator = new GameCaptureRecoveryCoordinator(CaptureBackend.Wgc, false, health: health);
        coordinator.TryDecide(CaptureBackend.Wgc, CaptureFailureKind.BackendUnavailable, out _, now);
        Assert.False(coordinator.TryReturnToPreferred(CaptureBackend.DesktopDuplication, now.AddMinutes(1), out _));
        coordinator.AvoidWgc = true;
        Assert.False(coordinator.TryReturnToPreferred(CaptureBackend.DesktopDuplication, now.AddMinutes(11), out _));
        coordinator.AvoidWgc = false;
        Assert.True(coordinator.TryReturnToPreferred(CaptureBackend.DesktopDuplication, now.AddMinutes(11), out var decision));
        Assert.Equal(CaptureBackend.Wgc, decision.Backend);
        coordinator.Stop();
        Assert.False(coordinator.TryReturnToPreferred(CaptureBackend.DesktopDuplication, now.AddMinutes(22), out _));
    }
    [Fact]
    public void Repeated_monitor_failures_do_not_ping_pong_and_same_backend_retries_wait()
    {
        var health = new CaptureHealthPolicy();
        var coordinator = new GameCaptureRecoveryCoordinator(CaptureBackend.Wgc, false, health: health);
        var now = DateTimeOffset.UtcNow;
        Assert.True(coordinator.TryDecide(CaptureBackend.Wgc, CaptureFailureKind.BackendUnavailable,
            out var first, now));
        Assert.Equal(CaptureBackend.DesktopDuplication, first.Backend);
        for (int i = 0; i < 100; i++)
        {
            coordinator.TryDecide(first.Backend, CaptureFailureKind.BackendUnavailable, out var next, now);
            Assert.Equal(first.Backend, next.Backend);
            Assert.True(next.RetryDelay >= TimeSpan.FromSeconds(3));
        }
        coordinator.TryDecide(first.Backend, CaptureFailureKind.BackendUnavailable,
            out var expired, now.AddMinutes(11));
        Assert.Equal(CaptureBackend.Wgc, expired.Backend);
    }

    [Fact]
    public void Recovery_obeys_shared_startup_switch_budget_even_for_stalled_backends()
    {
        var health = new CaptureHealthPolicy();
        var now = DateTimeOffset.UtcNow;
        health.TryRecordSwitch(now);
        health.TryRecordSwitch(now);
        var coordinator = new GameCaptureRecoveryCoordinator(CaptureBackend.Wgc, false, health: health);
        coordinator.TryDecide(CaptureBackend.DesktopDuplication, CaptureFailureKind.BackendStalled,
            out var decision, now);
        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
        Assert.True(decision.RetryDelay > TimeSpan.Zero);
    }

    [Fact]
    public void Fullscreen_wgc_stall_selects_hybrid_and_epoch_gate_blocks_stale_frames()
    {
        var coordinator = CreateCoordinator();
        GameCaptureTarget target = Minecraft(revision: 9);
        coordinator.ObserveTarget(target);

        Assert.True(coordinator.TryDecide(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            out CaptureRecoveryDecision decision));
        var gate = new CaptureFrameAdmissionGate(
            generation: 12,
            targetRevision: decision.TargetRevision,
            mode: CaptureFrameAdmissionMode.Hybrid);

        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.True(gate.Accept(12, 0, CaptureSurfaceScope.Monitor, routeEpoch: 1));
        Assert.False(gate.Accept(12, 8, CaptureSurfaceScope.GameWindow, routeEpoch: 2));
        Assert.False(gate.Accept(11, 9, CaptureSurfaceScope.GameWindow, routeEpoch: 2));
        Assert.True(gate.Accept(12, 9, CaptureSurfaceScope.GameWindow, routeEpoch: 2));
        Assert.False(gate.Accept(12, 0, CaptureSurfaceScope.Monitor, routeEpoch: 1));
    }

    [Fact]
    public void Dda_storm_selects_verified_minecraft_hook()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 4));

        Assert.True(coordinator.TryDecide(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm,
            out CaptureRecoveryDecision decision));

        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.Equal(4, decision.TargetRevision);
    }

    [Fact]
    public void Alt_tab_clears_episode_and_returns_minecraft_capture_to_monitor()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 3));
        coordinator.TryDecide(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            out _);

        coordinator.ObserveTarget(null);
        Assert.True(coordinator.TryDecide(
            CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.CaptureTargetClosed,
            out CaptureRecoveryDecision decision));

        Assert.Equal(CaptureEpisode.Empty, coordinator.Episode);
        Assert.Equal(CaptureBackend.Wgc, decision.Backend);
        Assert.Equal(0, decision.TargetRevision);
    }

    [Fact]
    public void New_window_identity_starts_fresh_episode()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 3));
        coordinator.TryDecide(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm,
            out CaptureRecoveryDecision oldDecision);
        GameCaptureTarget replacement = Minecraft(revision: 4) with
        {
            Hwnd = (nint)99,
            ProcessStartTicks = 88_000
        };

        coordinator.ObserveTarget(replacement);

        Assert.Equal(4, coordinator.Episode.TargetRevision);
        Assert.False(coordinator.Episode.IsQuarantined(CaptureBackend.DesktopDuplication));
        Assert.True(oldDecision.Episode.IsQuarantined(CaptureBackend.DesktopDuplication));
    }

    [Fact]
    public void User_stop_prevents_all_later_recovery_decisions()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 2));

        coordinator.Stop();

        Assert.False(coordinator.TryDecide(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            out _));
    }

    private static GameCaptureRecoveryCoordinator CreateCoordinator() => new(
        preferredMonitorBackend: CaptureBackend.Wgc,
        forcedBackend: false);

    /// <summary>
    /// Ушли с WGC ради OpenGL-игры: Alt-Tab рвёт дупликацию, и раньше восстановление
    /// возвращало WGC, и курсор в игре снова пропадал.
    /// </summary>
    [Fact]
    public void After_leaving_wgc_alt_tab_storms_never_bring_wgc_back()
    {
        var coordinator = CreateCoordinator();
        GameCaptureTarget zomboid = Minecraft(revision: 2) with
        {
            ExecutableName = "ProjectZomboid64", GameName = "Project Zomboid"
        };
        coordinator.AvoidWgc = true;

        coordinator.ObserveTarget(null);   // свернули игру
        Assert.True(coordinator.TryDecide(CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm, out var onDesktop));
        Assert.Equal(CaptureBackend.DesktopDuplication, onDesktop.Backend);

        coordinator.ObserveTarget(zomboid);   // вернулись в игру
        Assert.True(coordinator.TryDecide(CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm, out var inGame));
        Assert.Equal(CaptureBackend.DesktopDuplication, inGame.Backend);
        Assert.True(coordinator.TryDecide(CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendStalled, out var stalled));
        Assert.Equal(CaptureBackend.DesktopDuplication, stalled.Backend);

        // Новое включение повтора начинает с чистого листа
        coordinator.Resume();
        Assert.False(coordinator.AvoidWgc);
    }

    [Fact]
    public void Tf2_stays_on_desktop_duplication_if_it_also_stalls_in_the_same_game()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 3) with
        {
            ExecutableName = "tf_win64",
            GameName = "Team Fortress 2"
        });

        Assert.True(coordinator.TryDecide(CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled, out var first));
        Assert.Equal(CaptureBackend.DesktopDuplication, first.Backend);
        Assert.Equal(0, first.TargetRevision);

        Assert.True(coordinator.TryDecide(first.Backend,
            CaptureFailureKind.BackendTransitionStorm, out var retry));
        Assert.Equal(CaptureBackend.DesktopDuplication, retry.Backend);
        Assert.Equal(0, retry.TargetRevision);
        Assert.True(retry.RetryDelay > TimeSpan.Zero);
    }

    [Fact]
    public void Failed_hook_is_not_reselected_after_later_monitor_failure_in_same_game()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 6));
        coordinator.TryDecide(CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.BackendUnavailable, out CaptureRecoveryDecision fallback);
        Assert.Equal(CaptureBackend.DesktopDuplication, fallback.Backend);

        var foreground = CaptureBackendPolicy.SelectForForeground(CaptureBackend.DesktopDuplication,
            null, coordinator.Target, false, coordinator.Episode);
        Assert.False(foreground.RestartRequired);
        Assert.Equal(CaptureBackend.DesktopDuplication, foreground.Backend);

        coordinator.TryDecide(CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled, out CaptureRecoveryDecision retry);
        Assert.Equal(CaptureBackend.DesktopDuplication, retry.Backend);

        coordinator.TryDecide(CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendStalled, out CaptureRecoveryDecision retryDda);
        Assert.Equal(CaptureBackend.DesktopDuplication, retryDda.Backend);
        Assert.True(retryDda.RetryDelay > TimeSpan.Zero);
        Assert.True(retryDda.Episode.IsQuarantined(CaptureBackend.MinecraftOpenGl));
    }

    [Fact]
    public void Alt_tab_and_geometry_revision_do_not_reinject_failed_hook_in_same_process()
    {
        var coordinator = CreateCoordinator();
        var game = Minecraft(revision: 6);
        coordinator.ObserveTarget(game);
        coordinator.TryDecide(CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.BackendUnavailable, out _);
        coordinator.ObserveTarget(null);
        coordinator.TryDecide(CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.CaptureTargetClosed, out var desktop);
        Assert.Equal(CaptureBackend.Wgc, desktop.Backend);

        game = game with { Revision = 7 };
        coordinator.ObserveTarget(game);
        var selection = CaptureBackendPolicy.SelectForForeground(CaptureBackend.Wgc,
            null, game, false, coordinator.Episode);
        Assert.False(selection.RestartRequired);
        Assert.Equal(CaptureBackend.Wgc, selection.Backend);
        Assert.True(coordinator.Episode.IsQuarantined(CaptureBackend.MinecraftOpenGl));

        var replacement = game with { ProcessStartTicks = 88_000 };
        coordinator.ObserveTarget(replacement);
        selection = CaptureBackendPolicy.SelectForForeground(CaptureBackend.Wgc,
            null, replacement, false, coordinator.Episode);
        Assert.True(selection.RestartRequired);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, selection.Backend);
    }

    [Fact]
    public void User_resume_allows_a_fresh_hook_attempt_after_previous_failure()
    {
        var coordinator = CreateCoordinator();
        var game = Minecraft(revision: 6);
        coordinator.ObserveTarget(game);
        coordinator.TryDecide(CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.BackendUnavailable, out _);
        coordinator.Stop();
        coordinator.Resume();
        coordinator.ObserveTarget(game);
        var selection = CaptureBackendPolicy.SelectForForeground(CaptureBackend.Wgc,
            null, game, false, coordinator.Episode);
        Assert.True(selection.RestartRequired);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, selection.Backend);
    }

    private static GameCaptureTarget Minecraft(long revision) => new(
        Hwnd: (nint)42,
        ProcessId: 501,
        ProcessStartTicks: 77_000,
        ExecutableName: "javaw",
        GameName: "Minecraft",
        MonitorIndex: 0,
        ClientBounds: new PixelRect(0, 0, 1920, 1080),
        MonitorBounds: new PixelRect(0, 0, 1920, 1080),
        Revision: revision);
}
