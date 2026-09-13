using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureSourceRequestTests
{
    [Fact]
    public void Window_request_requires_verified_target()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptureSourceRequest.Create(CaptureBackend.WgcWindow, monitorIndex: 0, target: null));
    }

    [Theory]
    [InlineData(CaptureBackend.Wgc)]
    [InlineData(CaptureBackend.DesktopDuplication)]
    public void Monitor_request_rejects_window_target(CaptureBackend backend)
    {
        Assert.Throws<ArgumentException>(() =>
            CaptureSourceRequest.Create(backend, monitorIndex: 0, target: Target()));
    }

    [Fact]
    public void Window_request_rejects_target_from_another_monitor()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptureSourceRequest.Create(
                CaptureBackend.WgcWindow,
                monitorIndex: 1,
                target: Target()));
    }

    [Fact]
    public void Valid_window_request_preserves_target_revision()
    {
        CaptureSourceRequest request = CaptureSourceRequest.Create(
            CaptureBackend.WgcWindow,
            monitorIndex: 0,
            target: Target());

        Assert.Equal(CaptureBackend.WgcWindow, request.Backend);
        Assert.Equal(0, request.MonitorIndex);
        Assert.Equal(12, request.TargetRevision);
    }

    [Fact]
    public void Minecraft_opengl_request_requires_verified_target()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptureSourceRequest.Create(
                CaptureBackend.MinecraftOpenGl,
                monitorIndex: 0,
                target: null));
    }

    [Fact]
    public void Minecraft_opengl_rejects_non_minecraft_target()
    {
        Assert.Throws<ArgumentException>(() =>
            CaptureSourceRequest.Create(
                CaptureBackend.MinecraftOpenGl,
                monitorIndex: 0,
                target: Target() with { ExecutableName = "cs2", GameName = "Counter-Strike 2" }));
    }

    [Fact]
    public void Valid_minecraft_opengl_request_preserves_identity()
    {
        CaptureSourceRequest request = CaptureSourceRequest.Create(
            CaptureBackend.MinecraftOpenGl,
            monitorIndex: 0,
            target: Target());

        Assert.Equal(CaptureBackend.MinecraftOpenGl, request.Backend);
        Assert.Equal(Target(), request.Target);
        Assert.Equal(12, request.TargetRevision);
    }

    [Fact]
    public void Monitor_request_has_no_target_revision()
    {
        CaptureSourceRequest request = CaptureSourceRequest.Create(
            CaptureBackend.Wgc,
            monitorIndex: 0,
            target: null);

        Assert.Equal(0, request.TargetRevision);
    }

    private static GameCaptureTarget Target() => new(
        Hwnd: (nint)42,
        ProcessId: 501,
        ProcessStartTicks: 77_000,
        ExecutableName: "javaw",
        GameName: "Minecraft",
        MonitorIndex: 0,
        ClientBounds: new PixelRect(0, 0, 1920, 1080),
        MonitorBounds: new PixelRect(0, 0, 1920, 1080),
        Revision: 12);
}
