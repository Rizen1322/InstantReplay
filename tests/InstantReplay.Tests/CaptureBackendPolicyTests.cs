using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureBackendPolicyTests
{
    [Fact]
    public void Verified_minecraft_java_target_selects_opengl_immediately()
    {
        CaptureBackendTargetSelection selection = CaptureBackendPolicy.SelectForForeground(
            CaptureBackend.Wgc,
            activeTarget: null,
            foregroundTarget: Target("javaw", "Minecraft", revision: 4),
            forcedBackend: false);

        Assert.True(selection.RestartRequired);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, selection.Backend);
        Assert.Equal(4, selection.Target?.Revision);
    }

    [Theory]
    [InlineData("cs2", "Counter-Strike 2")]
    [InlineData("javaw", "Desktop")]
    [InlineData("notepad", "Minecraft")]
    public void Non_minecraft_targets_never_select_injected_capture(
        string executable, string game)
    {
        CaptureBackendTargetSelection selection = CaptureBackendPolicy.SelectForForeground(
            CaptureBackend.Wgc,
            activeTarget: null,
            foregroundTarget: Target(executable, game, revision: 2),
            forcedBackend: false);

        Assert.False(selection.RestartRequired);
        Assert.Equal(CaptureBackend.Wgc, selection.Backend);
        Assert.Null(selection.Target);
    }

    [Fact]
    public void Alt_tab_keeps_live_minecraft_provider_without_restart()
    {
        GameCaptureTarget active = Target("javaw", "Minecraft", revision: 7);

        CaptureBackendTargetSelection selection = CaptureBackendPolicy.SelectForForeground(
            CaptureBackend.MinecraftOpenGl,
            active,
            foregroundTarget: null,
            forcedBackend: false);

        Assert.False(selection.RestartRequired);
        Assert.Equal(CaptureBackend.MinecraftOpenGl, selection.Backend);
        Assert.Equal(active, selection.Target);
    }

    [Fact]
    public void Replacement_minecraft_process_requires_a_new_hybrid_episode()
    {
        GameCaptureTarget active = Target("javaw", "Minecraft", revision: 7);
        GameCaptureTarget replacement = Target("javaw", "Minecraft", revision: 8) with
        {
            Hwnd = (nint)99,
            ProcessId = 902,
            ProcessStartTicks = 88_000
        };

        CaptureBackendTargetSelection selection = CaptureBackendPolicy.SelectForForeground(
            CaptureBackend.MinecraftOpenGl,
            active,
            replacement,
            forcedBackend: false);

        Assert.True(selection.RestartRequired);
        Assert.Equal(replacement, selection.Target);
    }

    [Fact]
    public void Diagnostic_monitor_override_disables_injected_capture()
    {
        CaptureBackendTargetSelection selection = CaptureBackendPolicy.SelectForForeground(
            CaptureBackend.Wgc,
            activeTarget: null,
            foregroundTarget: Target("javaw", "Minecraft", revision: 4),
            forcedBackend: true);

        Assert.False(selection.RestartRequired);
        Assert.Equal(CaptureBackend.Wgc, selection.Backend);
    }

    [Theory]
    [InlineData(22000)]
    [InlineData(22621)]
    [InlineData(26100)]
    public void Windows11DefaultsToWgc(int build)
    {
        Assert.Equal(
            new CaptureBackendSelection(CaptureBackend.Wgc, Forced: false),
            CaptureBackendPolicy.SelectInitial(build, diagnosticOverride: null));
    }

    [Fact]
    public void Windows10DefaultsToDesktopDuplication()
    {
        Assert.Equal(
            new CaptureBackendSelection(CaptureBackend.DesktopDuplication, Forced: false),
            CaptureBackendPolicy.SelectInitial(19045, diagnosticOverride: null));
    }

    [Theory]
    [InlineData("wgc", CaptureBackend.Wgc)]
    [InlineData(" WGC ", CaptureBackend.Wgc)]
    [InlineData("dda", CaptureBackend.DesktopDuplication)]
    [InlineData(" DDA ", CaptureBackend.DesktopDuplication)]
    public void DiagnosticOverrideForcesBackend(string value, CaptureBackend expected)
    {
        Assert.Equal(
            new CaptureBackendSelection(expected, Forced: true),
            CaptureBackendPolicy.SelectInitial(22621, value));
    }

    [Fact]
    public void InvalidDiagnosticOverrideDoesNotOverrideWindowsDefault()
    {
        Assert.Equal(
            new CaptureBackendSelection(CaptureBackend.Wgc, Forced: false),
            CaptureBackendPolicy.SelectInitial(22621, "broken"));
    }

    [Fact]
    public void Window_capture_is_never_an_initial_default()
    {
        Assert.NotEqual(CaptureBackend.WgcWindow, CaptureBackendPolicy.SelectInitial(19045, null).Backend);
        Assert.NotEqual(CaptureBackend.WgcWindow, CaptureBackendPolicy.SelectInitial(22621, null).Backend);
    }

    [Theory]
    [InlineData(CaptureBackend.Wgc, CaptureBackend.DesktopDuplication)]
    [InlineData(CaptureBackend.DesktopDuplication, CaptureBackend.Wgc)]
    public void AlternativeReturnsOtherBackend(CaptureBackend current, CaptureBackend expected)
    {
        Assert.Equal(expected, CaptureBackendPolicy.Alternative(current));
    }

    [Fact]
    public void Hybrid_backend_is_not_part_of_monitor_fallback_pair() =>
        Assert.Throws<ArgumentException>(() =>
            CaptureBackendPolicy.Alternative(CaptureBackend.MinecraftOpenGl));

    private static GameCaptureTarget Target(string executable, string game, long revision) => new(
        Hwnd: (nint)42,
        ProcessId: 501,
        ProcessStartTicks: 77_000,
        ExecutableName: executable,
        GameName: game,
        MonitorIndex: 0,
        ClientBounds: new PixelRect(0, 0, 1920, 1080),
        MonitorBounds: new PixelRect(0, 0, 1920, 1080),
        Revision: revision);
}
