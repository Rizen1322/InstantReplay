using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureHealthPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OneBadWgcSecondDoesNotSwitch()
    {
        var policy = new CaptureHealthPolicy();

        Assert.False(policy.Observe(StarvedWgc(), Now).SwitchBackend);
    }

    [Fact]
    public void TenStarvedWgcSecondsInGameSwitchToDda()
    {
        var policy = new CaptureHealthPolicy();
        CaptureHealthDecision decision = default;

        for (int i = 0; i < 10; i++)
            decision = policy.Observe(StarvedWgc(), Now.AddSeconds(i));

        Assert.True(decision.SwitchBackend);
        Assert.Contains("10", decision.Reason);
    }

    [Fact]
    public void StaticDesktopResetsStarvationStreak()
    {
        var policy = new CaptureHealthPolicy();
        for (int i = 0; i < 9; i++) policy.Observe(StarvedWgc(), Now.AddSeconds(i));

        policy.Observe(StarvedWgc() with { GameForeground = false }, Now.AddSeconds(9));

        Assert.False(policy.Observe(StarvedWgc(), Now.AddSeconds(10)).SwitchBackend);
    }

    [Fact]
    public void EncoderStarvationDoesNotBlameCapture()
    {
        var policy = new CaptureHealthPolicy();
        var sample = StarvedWgc() with { FramesEncoded = 30, FramesDuplicated = 20 };

        CaptureHealthDecision decision = default;
        for (int i = 0; i < 20; i++) decision = policy.Observe(sample, Now.AddSeconds(i));

        Assert.False(decision.SwitchBackend);
    }

    [Fact]
    public void WarmupSamplesDoNotCount()
    {
        var policy = new CaptureHealthPolicy();
        var warmup = StarvedWgc() with { Uptime = TimeSpan.FromSeconds(9) };

        for (int i = 0; i < 20; i++)
            Assert.False(policy.Observe(warmup, Now.AddSeconds(i)).SwitchBackend);
    }

    [Fact]
    public void DdaLowFrameRateDoesNotTriggerSoftSwitch()
    {
        var policy = new CaptureHealthPolicy();
        var sample = StarvedWgc() with { Backend = CaptureBackend.DesktopDuplication };

        CaptureHealthDecision decision = default;
        for (int i = 0; i < 20; i++) decision = policy.Observe(sample, Now.AddSeconds(i));

        Assert.False(decision.SwitchBackend);
    }

    [Fact]
    public void FiveFrozenDdaSecondsInGameTriggerSoftSwitch()
    {
        var policy = new CaptureHealthPolicy();
        CaptureHealthDecision decision = default;

        for (int i = 0; i < 5; i++)
            decision = policy.Observe(FrozenDda(), Now.AddSeconds(i));

        Assert.True(decision.SwitchBackend);
        Assert.Contains("DDA", decision.Reason);
    }

    [Fact]
    public void FrozenDdaOnStaticDesktopDoesNotSwitch()
    {
        var policy = new CaptureHealthPolicy();
        CaptureHealthDecision decision = default;

        for (int i = 0; i < 20; i++)
            decision = policy.Observe(
                FrozenDda() with { GameForeground = false }, Now.AddSeconds(i));

        Assert.False(decision.SwitchBackend);
    }

    [Fact]
    public void BackendFailureQuarantinesForTenMinutes()
    {
        var policy = new CaptureHealthPolicy();
        policy.Quarantine(CaptureBackend.Wgc, Now, CaptureQuarantine.Transient);

        Assert.False(policy.CanUse(CaptureBackend.Wgc, Now.AddMinutes(9)));
        Assert.True(policy.CanUse(CaptureBackend.Wgc, Now.AddMinutes(10)));
    }

    [Fact]
    public void SoftWgcFailureQuarantinesForProcessSession()
    {
        var policy = new CaptureHealthPolicy();
        policy.Quarantine(CaptureBackend.Wgc, Now, CaptureQuarantine.ProcessSession);

        Assert.False(policy.CanUse(CaptureBackend.Wgc, Now.AddDays(30)));
    }

    [Fact]
    public void ThirdSwitchWithinTenMinutesIsRejected()
    {
        var policy = new CaptureHealthPolicy();

        Assert.True(policy.TryRecordSwitch(Now));
        Assert.True(policy.TryRecordSwitch(Now.AddMinutes(1)));
        Assert.False(policy.TryRecordSwitch(Now.AddMinutes(2)));
        Assert.True(policy.TryRecordSwitch(Now.AddMinutes(11)));
    }

    [Fact]
    public void BackendUnavailableSelectsAlternativeAndQuarantinesFailedBackend()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend selected = policy.SelectAfterFailure(
            CaptureBackend.Wgc, CaptureFailureKind.BackendUnavailable,
            backendForced: false, Now);

        Assert.Equal(CaptureBackend.DesktopDuplication, selected);
        Assert.False(policy.CanUse(CaptureBackend.Wgc, Now.AddMinutes(1)));
    }

    [Fact]
    public void DeviceLossRebuildsSameBackendWithoutQuarantine()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend selected = policy.SelectAfterFailure(
            CaptureBackend.Wgc, CaptureFailureKind.DeviceLost,
            backendForced: false, Now);

        Assert.Equal(CaptureBackend.Wgc, selected);
        Assert.True(policy.CanUse(CaptureBackend.Wgc, Now));
    }

    [Fact]
    public void DeviceLossReturnsToConfiguredPreferredBackend()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend selected = policy.SelectAfterFailure(
            CaptureBackend.DesktopDuplication, CaptureFailureKind.DeviceLost,
            backendForced: false, Now, preferredBackend: CaptureBackend.Wgc);

        Assert.Equal(CaptureBackend.Wgc, selected);
        Assert.True(policy.CanUse(CaptureBackend.DesktopDuplication, Now));
    }

    [Fact]
    public void CaptureFormatChangeRebuildsSameBackendWithoutQuarantine()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend selected = policy.SelectAfterFailure(
            CaptureBackend.DesktopDuplication, CaptureFailureKind.CaptureFormatChanged,
            backendForced: false, Now);

        Assert.Equal(CaptureBackend.DesktopDuplication, selected);
        Assert.True(policy.CanUse(CaptureBackend.DesktopDuplication, Now));
    }

    [Fact]
    public void DiagnosticOverrideNeverChangesBackend()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend selected = policy.SelectAfterFailure(
            CaptureBackend.Wgc, CaptureFailureKind.BackendUnavailable,
            backendForced: true, Now);

        Assert.Equal(CaptureBackend.Wgc, selected);
    }

    [Fact]
    public void SoftStallSelectsAlternativeAndQuarantinesWgcForProcessSession()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend selected = policy.SelectAfterFailure(
            CaptureBackend.Wgc, CaptureFailureKind.BackendStalled,
            backendForced: false, Now);

        Assert.Equal(CaptureBackend.DesktopDuplication, selected);
        Assert.False(policy.CanUse(CaptureBackend.Wgc, Now.AddDays(30)));
    }

    [Fact]
    public void DdaStallCanUseQuarantinedWgcOnceWithoutPingPong()
    {
        var policy = new CaptureHealthPolicy();

        CaptureBackend afterWgcStall = policy.SelectAfterFailure(
            CaptureBackend.Wgc, CaptureFailureKind.BackendStalled,
            backendForced: false, Now);
        CaptureBackend afterDdaStall = policy.SelectAfterFailure(
            afterWgcStall, CaptureFailureKind.BackendStalled,
            backendForced: false, Now.AddMinutes(1));
        CaptureBackend afterSecondWgcStall = policy.SelectAfterFailure(
            afterDdaStall, CaptureFailureKind.BackendStalled,
            backendForced: false, Now.AddMinutes(2));

        Assert.Equal(CaptureBackend.DesktopDuplication, afterWgcStall);
        Assert.Equal(CaptureBackend.Wgc, afterDdaStall);
        Assert.Equal(CaptureBackend.Wgc, afterSecondWgcStall);
    }

    private static CaptureHealthSample StarvedWgc() => new(
        Backend: CaptureBackend.Wgc,
        TargetFps: 60,
        FramesReceived: 30,
        FramesEncoded: 55,
        FramesDuplicated: 25,
        GameForeground: true,
        Uptime: TimeSpan.FromSeconds(20));

    private static CaptureHealthSample FrozenDda() => new(
        Backend: CaptureBackend.DesktopDuplication,
        TargetFps: 60,
        FramesReceived: 0,
        FramesEncoded: 60,
        FramesDuplicated: 59,
        GameForeground: true,
        Uptime: TimeSpan.FromSeconds(20));
}
