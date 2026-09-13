using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureFrameAdmissionGateTests
{
    [Fact]
    public void Monitor_frame_is_rejected_during_window_episode()
    {
        var gate = new CaptureFrameAdmissionGate(
            generation: 12,
            targetRevision: 4,
            windowEpisode: true);

        Assert.Equal(
            CaptureFrameAdmission.MonitorBlocked,
            gate.Evaluate(12, 0, CaptureSurfaceScope.Monitor));
        Assert.Equal(
            CaptureFrameAdmission.Admit,
            gate.Evaluate(12, 4, CaptureSurfaceScope.GameWindow));
    }

    [Fact]
    public void Stale_generation_is_rejected_before_scope_checks()
    {
        var gate = new CaptureFrameAdmissionGate(12, 4, windowEpisode: true);

        Assert.Equal(
            CaptureFrameAdmission.StaleGeneration,
            gate.Evaluate(11, 4, CaptureSurfaceScope.GameWindow));
    }

    [Fact]
    public void Stale_target_revision_is_rejected()
    {
        var gate = new CaptureFrameAdmissionGate(12, 4, windowEpisode: true);

        Assert.Equal(
            CaptureFrameAdmission.StaleTarget,
            gate.Evaluate(12, 3, CaptureSurfaceScope.GameWindow));
    }

    [Fact]
    public void Window_frame_is_rejected_outside_window_episode()
    {
        var gate = new CaptureFrameAdmissionGate(12, 0, windowEpisode: false);

        Assert.Equal(
            CaptureFrameAdmission.UnexpectedWindow,
            gate.Evaluate(12, 4, CaptureSurfaceScope.GameWindow));
        Assert.True(gate.Accept(12, 0, CaptureSurfaceScope.Monitor));
    }

    [Fact]
    public void Hybrid_episode_accepts_both_scopes_but_rejects_old_route_epoch()
    {
        var gate = new CaptureFrameAdmissionGate(
            generation: 12,
            targetRevision: 4,
            CaptureFrameAdmissionMode.Hybrid);

        Assert.Equal(
            CaptureFrameAdmission.Admit,
            gate.Evaluate(12, 0, CaptureSurfaceScope.Monitor, routeEpoch: 3));
        Assert.Equal(
            CaptureFrameAdmission.Admit,
            gate.Evaluate(12, 4, CaptureSurfaceScope.GameWindow, routeEpoch: 4));
        Assert.Equal(
            CaptureFrameAdmission.StaleRoute,
            gate.Evaluate(12, 0, CaptureSurfaceScope.Monitor, routeEpoch: 3));
        Assert.Equal(
            CaptureFrameAdmission.StaleTarget,
            gate.Evaluate(12, 3, CaptureSurfaceScope.GameWindow, routeEpoch: 4));
    }
}
