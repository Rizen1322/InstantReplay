using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class MinecraftHookInjectorPolicyTests
{
    [Fact]
    public void Injector_requests_only_the_documented_process_rights()
    {
        GameHookProcessAccess expected =
            GameHookProcessAccess.CreateThread |
            GameHookProcessAccess.QueryLimitedInformation |
            GameHookProcessAccess.VmOperation |
            GameHookProcessAccess.VmWrite |
            GameHookProcessAccess.VmRead;

        Assert.Equal(MinecraftHookInjectionPolicy.RequiredProcessAccess, expected);
        Assert.False(MinecraftHookInjectionPolicy.RequiredProcessAccess.HasFlag(GameHookProcessAccess.AllAccess));
        Assert.False(MinecraftHookInjectionPolicy.RequiredProcessAccess.HasFlag(GameHookProcessAccess.Terminate));
        Assert.False(MinecraftHookInjectionPolicy.RequiredProcessAccess.HasFlag(GameHookProcessAccess.SuspendResume));
    }

    [Fact]
    public void Failed_identity_is_quarantined_but_a_restarted_process_is_not()
    {
        var quarantine = new MinecraftHookInjectionQuarantine();
        var failed = new MinecraftHookProcessIdentity(ProcessId: 321, ProcessStartTicks: 123);

        Assert.False(quarantine.Contains(failed));
        quarantine.Add(failed);
        Assert.True(quarantine.Contains(failed));
        Assert.False(quarantine.Contains(failed with { ProcessStartTicks = 124 }));
        Assert.False(quarantine.Contains(failed with { ProcessId = 322 }));
    }

    [Theory]
    [InlineData((int)MinecraftHookInjectionResult.OpenProcessDenied)]
    [InlineData((int)MinecraftHookInjectionResult.RemoteAllocationFailed)]
    [InlineData((int)MinecraftHookInjectionResult.RemoteWriteFailed)]
    [InlineData((int)MinecraftHookInjectionResult.RemoteThreadFailed)]
    [InlineData((int)MinecraftHookInjectionResult.TimedOut)]
    [InlineData((int)MinecraftHookInjectionResult.LoadLibraryFailed)]
    public void Runtime_injection_failures_are_quarantined(int rawResult)
    {
        var result = (MinecraftHookInjectionResult)rawResult;
        Assert.True(MinecraftHookInjectionPolicy.MustQuarantine(result));
    }

    [Theory]
    [InlineData((int)MinecraftHookInjectionResult.Success)]
    [InlineData((int)MinecraftHookInjectionResult.Ineligible)]
    [InlineData((int)MinecraftHookInjectionResult.Quarantined)]
    [InlineData((int)MinecraftHookInjectionResult.InvalidHookBinary)]
    public void Non_attempts_and_success_do_not_add_a_new_quarantine(int rawResult)
    {
        var result = (MinecraftHookInjectionResult)rawResult;
        Assert.False(MinecraftHookInjectionPolicy.MustQuarantine(result));
    }
}
