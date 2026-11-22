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

        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.DesktopDuplication));
    }

    [Fact]
    public void Monitor_wgc_stall_in_tf2_switches_to_desktop_duplication()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.Wgc,
            CaptureFailureKind.BackendStalled,
            ForcedBackend: false,
            Target: Game(revision: 3) with { ExecutableName = "tf_win64", GameName = "Team Fortress 2" },
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
        Assert.Equal(0, decision.TargetRevision);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.Wgc));
    }

    [Fact]
    public void Minecraft_hook_failure_uses_desktop_duplication()
    {
        GameCaptureTarget target = Minecraft(revision: 11);

        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.BackendUnavailable,
            ForcedBackend: false,
            Target: target,
            Episode: CaptureEpisode.ForTarget(target),
            PreferredMonitorBackend: CaptureBackend.Wgc));

        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
        Assert.Equal(0, decision.TargetRevision);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.MinecraftOpenGl));
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

        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
        Assert.Equal(CaptureEpisode.Empty, decision.Episode);
    }

    // ---------- Windows 10: WGC запрещён, иначе в записи жёлтая рамка ----------

    [Fact]
    public void Without_wgc_desktop_stall_restarts_desktop_duplication()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendStalled,
            ForcedBackend: false,
            Target: null,
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.DesktopDuplication,
            AllowWgc: false));

        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
    }

    [Fact]
    public void Without_wgc_game_storm_stays_on_desktop_duplication()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm,
            ForcedBackend: false,
            Target: Game(revision: 2),
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.DesktopDuplication,
            AllowWgc: false));

        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
    }

    [Fact]
    public void Without_wgc_minecraft_target_still_uses_opengl_hook()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.DesktopDuplication,
            CaptureFailureKind.BackendTransitionStorm,
            ForcedBackend: false,
            Target: Minecraft(revision: 5),
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.DesktopDuplication,
            AllowWgc: false));

        Assert.Equal(CaptureBackend.MinecraftOpenGl, decision.Backend);
        Assert.Equal(5, decision.TargetRevision);
    }

    [Fact]
    public void Without_wgc_failed_minecraft_hook_falls_back_to_desktop_duplication()
    {
        var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
            CaptureBackend.MinecraftOpenGl,
            CaptureFailureKind.BackendUnavailable,
            ForcedBackend: false,
            Target: Minecraft(revision: 5),
            Episode: CaptureEpisode.Empty,
            PreferredMonitorBackend: CaptureBackend.DesktopDuplication,
            AllowWgc: false));

        Assert.Equal(CaptureBackend.DesktopDuplication, decision.Backend);
        Assert.True(decision.Episode.IsQuarantined(CaptureBackend.MinecraftOpenGl));
    }

    [Theory]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureFailureKind.BackendStalled)]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureFailureKind.BackendTransitionStorm)]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureFailureKind.BackendUnavailable)]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureFailureKind.DeviceLost)]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureFailureKind.CaptureFormatChanged)]
    [InlineData(CaptureBackend.MinecraftOpenGl, CaptureFailureKind.BackendUnavailable)]
    public void Without_wgc_no_decision_ever_picks_wgc(CaptureBackend active, CaptureFailureKind failure)
    {
        foreach (GameCaptureTarget? target in new GameCaptureTarget?[] { null, Game(revision: 3), Minecraft(revision: 3) })
        {
            var decision = CaptureRecoveryPolicy.Decide(new CaptureRecoveryContext(
                active,
                failure,
                ForcedBackend: false,
                Target: target,
                Episode: CaptureEpisode.Empty,
                PreferredMonitorBackend: CaptureBackend.DesktopDuplication,
                AllowWgc: false));

            Assert.NotEqual(CaptureBackend.Wgc, decision.Backend);
        }
    }

    private static GameCaptureTarget Game(long revision) => new(
        Hwnd: (nint)77,
        ProcessId: 902,
        ProcessStartTicks: 88_000,
        ExecutableName: "ProjectZomboid64",
        GameName: "Project Zomboid",
        MonitorIndex: 0,
        ClientBounds: new PixelRect(0, 0, 1920, 1080),
        MonitorBounds: new PixelRect(0, 0, 1920, 1080),
        Revision: revision);

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
