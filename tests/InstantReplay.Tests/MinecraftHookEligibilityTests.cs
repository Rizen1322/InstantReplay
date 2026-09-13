using Aura.Core.Capture;
using Aura.Core.Capture.GameHook;
using Xunit;

namespace InstantReplay.Tests;

public sealed class MinecraftHookEligibilityTests
{
    [Fact]
    public void Fully_verified_x64_Minecraft_is_the_only_allowed_shape()
    {
        GameCaptureTarget target = Target();
        MinecraftHookEligibilityResult result = MinecraftHookEligibility.Evaluate(target, Snapshot());

        Assert.True(result.Allowed);
        Assert.Equal(MinecraftHookEligibilityReason.Allowed, result.Reason);
        Assert.Equal(target.ProcessId, result.Identity!.Value.ProcessId);
        Assert.Equal(target.ProcessStartTicks, result.Identity.Value.ProcessStartTicks);
        Assert.Equal(target.Hwnd, result.Identity.Value.Hwnd);
        Assert.Equal(target.Revision, result.Identity.Value.TargetRevision);
        Assert.Equal(target.MonitorIndex, result.Identity.Value.MonitorIndex);
    }

    [Fact]
    public void Cs2_is_permanently_denied_even_if_all_other_fields_are_forged_as_valid()
    {
        GameCaptureTarget target = Target() with
        {
            ExecutableName = "cs2.exe",
            GameName = "Minecraft"
        };
        MinecraftHookEligibilitySnapshot snapshot = Snapshot() with
        {
            ExecutableName = "cs2.exe",
            GameName = "Minecraft"
        };

        AssertRejected(
            target,
            snapshot,
            MinecraftHookEligibilityReason.ForbiddenExecutable);
    }

    [Fact]
    public void Every_unverified_process_or_window_property_is_rejected()
    {
        GameCaptureTarget target = Target();
        MinecraftHookEligibilitySnapshot valid = Snapshot();

        var cases = new (MinecraftHookEligibilityReason Reason, MinecraftHookEligibilitySnapshot Snapshot)[]
        {
            (MinecraftHookEligibilityReason.ExecutableNotJavaw, valid with { ExecutableName = "java.exe" }),
            (MinecraftHookEligibilityReason.NotMinecraft, valid with { GameName = "Some Java App" }),
            (MinecraftHookEligibilityReason.ProcessNotAlive, valid with { IsProcessAlive = false }),
            (MinecraftHookEligibilityReason.StaleProcessIdentity, valid with { ProcessId = 999 }),
            (MinecraftHookEligibilityReason.StaleProcessIdentity, valid with { ProcessStartTicks = 124 }),
            (MinecraftHookEligibilityReason.WindowMismatch, valid with { Hwnd = (nint)0x9999 }),
            (MinecraftHookEligibilityReason.WindowMismatch, valid with { RootHwnd = (nint)0x9999 }),
            (MinecraftHookEligibilityReason.NotForeground, valid with { ForegroundHwnd = (nint)0x9999 }),
            (MinecraftHookEligibilityReason.MonitorMismatch, valid with { MonitorIndex = 2 }),
            (MinecraftHookEligibilityReason.ArchitectureMismatch, valid with { Is64BitProcess = false }),
            (MinecraftHookEligibilityReason.ArchitectureMismatch, valid with { Is64BitController = false }),
            (MinecraftHookEligibilityReason.DifferentUser, valid with { ProcessUserSid = "S-1-5-21-other" }),
            (MinecraftHookEligibilityReason.HigherIntegrity, valid with { ProcessIntegrityLevel = 0x3000 }),
            (MinecraftHookEligibilityReason.ProtectedProcess, valid with { IsProtectedProcess = true }),
            (MinecraftHookEligibilityReason.OpenGlNotLoaded, valid with { LoadedModuleNames = ["java.dll"] }),
            (MinecraftHookEligibilityReason.AntiCheatDetected, valid with
            {
                LoadedModuleNames = ["opengl32.dll", "EasyAntiCheat_x64.dll"]
            })
        };

        foreach ((MinecraftHookEligibilityReason reason, MinecraftHookEligibilitySnapshot snapshot) in cases)
            AssertRejected(target, snapshot, reason);
    }

    [Fact]
    public void Target_classification_and_executable_must_match_the_fresh_snapshot()
    {
        AssertRejected(
            Target() with { ExecutableName = "other.exe" },
            Snapshot(),
            MinecraftHookEligibilityReason.TargetSnapshotMismatch);
        AssertRejected(
            Target() with { GameName = "Other" },
            Snapshot(),
            MinecraftHookEligibilityReason.TargetSnapshotMismatch);
    }

    private static void AssertRejected(
        in GameCaptureTarget target,
        in MinecraftHookEligibilitySnapshot snapshot,
        MinecraftHookEligibilityReason expected)
    {
        MinecraftHookEligibilityResult result = MinecraftHookEligibility.Evaluate(target, snapshot);
        Assert.False(result.Allowed);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Identity);
    }

    private static GameCaptureTarget Target() => new(
        Hwnd: (nint)0x1234,
        ProcessId: 321,
        ProcessStartTicks: 123,
        ExecutableName: "javaw.exe",
        GameName: "Minecraft",
        MonitorIndex: 1,
        ClientBounds: new PixelRect(0, 0, 2560, 1440),
        MonitorBounds: new PixelRect(0, 0, 2560, 1440),
        Revision: 7);

    private static MinecraftHookEligibilitySnapshot Snapshot() => new(
        Hwnd: (nint)0x1234,
        RootHwnd: (nint)0x1234,
        ForegroundHwnd: (nint)0x1234,
        ProcessId: 321,
        ProcessStartTicks: 123,
        ExecutableName: "javaw.exe",
        GameName: "Minecraft",
        MonitorIndex: 1,
        IsProcessAlive: true,
        Is64BitProcess: true,
        Is64BitController: true,
        ProcessUserSid: "S-1-5-21-current",
        CurrentUserSid: "S-1-5-21-current",
        ProcessIntegrityLevel: 0x2000,
        CurrentIntegrityLevel: 0x2000,
        IsProtectedProcess: false,
        LoadedModuleNames: ["java.dll", "opengl32.dll"]);
}
