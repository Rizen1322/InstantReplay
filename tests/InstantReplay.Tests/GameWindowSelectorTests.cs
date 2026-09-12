using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class GameWindowSelectorTests
{
    [Fact]
    public void Select_accepts_verified_fullscreen_game_window()
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft();

        GameCaptureTarget? target = GameWindowSelector.Select(snapshot, previous: null);

        Assert.NotNull(target);
        Assert.Equal((nint)42, target.Value.Hwnd);
        Assert.Equal(501, target.Value.ProcessId);
        Assert.Equal("javaw", target.Value.ExecutableName);
        Assert.Equal("Minecraft", target.Value.GameName);
        Assert.Equal(1, target.Value.Revision);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public void Select_rejects_ineligible_window_state(
        bool visible,
        bool minimized,
        bool cloaked,
        bool processAlive)
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft() with
        {
            IsVisible = visible,
            IsMinimized = minimized,
            IsCloaked = cloaked,
            IsProcessAlive = processAlive
        };

        Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
    }

    [Fact]
    public void Select_rejects_non_root_foreground_window()
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft() with
        {
            ForegroundHwnd = (nint)84,
            RootOwnerHwnd = (nint)42
        };

        Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
    }

    [Fact]
    public void Select_rejects_desktop_classification()
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft() with { GameName = "Desktop" };

        Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
    }

    [Fact]
    public void Select_rejects_empty_client_area()
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft() with
        {
            ClientBounds = new PixelRect(0, 0, 0, 1080)
        };

        Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
    }

    [Fact]
    public void Select_rejects_window_below_ninety_percent_monitor_coverage()
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft() with
        {
            ClientBounds = new PixelRect(100, 100, 1280, 720)
        };

        Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
    }

    [Fact]
    public void Select_rejects_window_on_different_selected_monitor()
    {
        WindowCaptureSnapshot snapshot = ValidMinecraft() with { SelectedMonitorIndex = 1 };

        Assert.Null(GameWindowSelector.Select(snapshot, previous: null));
    }

    [Fact]
    public void Select_preserves_revision_for_same_process_identity()
    {
        GameCaptureTarget previous = GameWindowSelector.Select(ValidMinecraft(), null)!.Value;

        GameCaptureTarget current = GameWindowSelector.Select(ValidMinecraft(), previous)!.Value;

        Assert.Equal(previous.Revision, current.Revision);
    }

    [Fact]
    public void Select_increments_revision_when_hwnd_is_reused_by_another_process()
    {
        GameCaptureTarget previous = GameWindowSelector.Select(ValidMinecraft(), null)!.Value;
        WindowCaptureSnapshot reused = ValidMinecraft() with
        {
            ProcessId = 777,
            ProcessStartTicks = 123_999
        };

        GameCaptureTarget current = GameWindowSelector.Select(reused, previous)!.Value;

        Assert.Equal(previous.Revision + 1, current.Revision);
        Assert.Equal(777, current.ProcessId);
    }

    private static WindowCaptureSnapshot ValidMinecraft() => new(
        Hwnd: (nint)42,
        ForegroundHwnd: (nint)42,
        RootOwnerHwnd: (nint)42,
        ProcessId: 501,
        ProcessStartTicks: 123_456,
        ExecutableName: "javaw",
        GameName: "Minecraft",
        MonitorIndex: 0,
        SelectedMonitorIndex: 0,
        ClientBounds: new PixelRect(0, 0, 1920, 1080),
        MonitorBounds: new PixelRect(0, 0, 1920, 1080),
        IsVisible: true,
        IsMinimized: false,
        IsCloaked: false,
        IsProcessAlive: true);
}
