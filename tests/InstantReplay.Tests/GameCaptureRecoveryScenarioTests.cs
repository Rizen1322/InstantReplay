using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameCaptureRecoveryScenarioTests
{
    [Fact]
    public void Fullscreen_wgc_stall_selects_window_and_blocks_monitor_frames()
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
            windowEpisode: true);

        Assert.Equal(CaptureBackend.WgcWindow, decision.Backend);
        Assert.False(gate.Accept(12, 0, CaptureSurfaceScope.Monitor));
        Assert.False(gate.Accept(12, 8, CaptureSurfaceScope.GameWindow));
        Assert.False(gate.Accept(11, 9, CaptureSurfaceScope.GameWindow));
        Assert.True(gate.Accept(12, 9, CaptureSurfaceScope.GameWindow));
    }

    [Fact]
    public void Dda_storm_selects_same_verified_window_target()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 4));

        Assert.True(coordinator.TryDecide(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm,
            out CaptureRecoveryDecision decision));

        Assert.Equal(CaptureBackend.WgcWindow, decision.Backend);
        Assert.Equal(4, decision.TargetRevision);
    }

    [Fact]
    public void Window_failure_enters_hold_and_retries_window()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 6));

        Assert.True(coordinator.TryDecide(
            CaptureBackend.WgcWindow,
            CaptureFailureKind.BackendStalled,
            out CaptureRecoveryDecision decision));

        Assert.Equal(CaptureRecoveryAction.HoldForGameWindow, decision.Action);
        Assert.Equal(CaptureBackend.WgcWindow, decision.Backend);
        Assert.True(decision.RetryDelay > TimeSpan.Zero);
    }

    [Fact]
    public void Alt_tab_clears_episode_and_returns_window_capture_to_monitor()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveTarget(Minecraft(revision: 3));
        coordinator.TryDecide(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            out _);

        coordinator.ObserveTarget(null);
        Assert.True(coordinator.TryDecide(
            CaptureBackend.WgcWindow,
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
