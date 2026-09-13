using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureRecoveryPolicyTests
{
    [Fact]
    public void Monitor_wgc_stall_routes_directly_to_verified_minecraft_opengl()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            ActiveBackend: CaptureBackend.Wgc,
            FailureKind: CaptureFailureKind.BackendStalled,
            ForcedBackend: false,
            Target: Minecraft(revision: 7),
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.Equal(7, decision.TargetRevision);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.Wgc));
    }

    [Fact]
    public void Dda_transition_storm_routes_directly_to_verified_minecraft_opengl()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm,
            ForcedBackend: false,
            Target: Minecraft(revision: 3),
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.DesktopDuplication));
    }

    [Fact]
    public void Window_failure_holds_same_target_instead_of_showing_monitor()
    {
        GameCaptureTarget target = Minecraft(revision: 9);
        CaptureEpisode episode = CaptureEpisode.ForTarget(target)
            .Quarantine(CaptureBackend.Wgc)
            .Quarantine(CaptureBackend.DesktopDuplication);

        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.WgcWindow,
            CaptureFailureKind.BackendUnavailable,
            ForcedBackend: false,
            Target: target,
            Episode: episode,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.HoldForGameWindow, decision.Action);
        Assert.Equal(CaptureBackend.WgcWindow, decision.Backend);
        Assert.Equal(9, decision.TargetRevision);
        Assert.True(decision.RetryDelay > TimeSpan.Zero);
    }

    [Fact]
    public void Minecraft_hook_failure_holds_same_target_instead_of_exposing_monitor()
    {
        GameCaptureTarget target = Minecraft(revision: 11);

        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.BackendUnavailable,
            ForcedBackend: false,
            Target: target,
            Episode: CaptureEpisode.ForTarget(target),
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.HoldForGameWindow, decision.Action);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.Equal(11, decision.TargetRevision);
        Assert.True(decision.RetryDelay > TimeSpan.Zero);
    }

    [Fact]
    public void Closed_minecraft_process_returns_to_preferred_monitor_provider()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.CaptureTargetClosed,
            ForcedBackend: false,
            Target: null,
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
        Assert.Equal(CaptureBackend.Wgc, decision.Backend);
        Assert.Equal(0, decision.TargetRevision);
    }

    [Fact]
    public void New_target_identity_clears_previous_episode_quarantines()
    {
        GameCaptureTarget first = Minecraft(revision: 4);
        CaptureEpisode oldEpisode = CaptureEpisode.ForTarget(first)
            .Quarantine(CaptureBackend.Wgc)
            .Quarantine(CaptureBackend.DesktopDuplication);
        GameCaptureTarget second = Minecraft(revision: 5) with
        {
            Hwnd = (nint)99,
            ProcessId = 902,
            ProcessStartTicks = 88_000
        };

        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            ForcedBackend: false,
            Target: second,
            Episode: oldEpisode,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(5, decision.Episode.TargetRevision);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.Wgc));
        Assert.False(decision.Episode.IsQuarantined(CaptureBackend.DesktopDuplication));
    }

    [Fact]
    public void Diagnostic_override_restarts_forced_monitor_backend()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            ForcedBackend: true,
            Target: Minecraft(revision: 2),
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
        Assert.Equal(CaptureBackend.Wgc, decision.Backend);
        Assert.Equal(0, decision.TargetRevision);
    }

    [Fact]
    public void Failure_without_game_target_uses_other_monitor_provider()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendUnavailable,
            ForcedBackend: false,
            Target: null,
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
        Assert.Equal(CaptureEpisode.Empty, decision.Episode);
    }

    [Fact]
    public void Lost_window_target_returns_to_preferred_monitor_provider()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.WgcWindow,
            CaptureFailureKind.CaptureTargetClosed,
            ForcedBackend: false,
            Target: null,
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureRecoveryAction.Restart, decision.Action);
        Assert.Equal(CaptureBackend.Wgc, decision.Backend);
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
