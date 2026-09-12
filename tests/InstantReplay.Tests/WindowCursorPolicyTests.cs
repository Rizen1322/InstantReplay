using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class WindowCursorPolicyTests
{
    [Fact]
    public void Maps_screen_hotspot_to_client_draw_origin()
    {
        CursorDrawPosition mapped = WindowCursorPolicy.MapPosition(
            screenX: 620,
            screenY: 340,
            clientLeft: 100,
            clientTop: 40,
            hotspotX: 5,
            hotspotY: 7);

        Assert.Equal(new CursorDrawPosition(515, 293), mapped);
    }

    [Fact]
    public void Mapping_supports_negative_desktop_coordinates()
    {
        CursorDrawPosition mapped = WindowCursorPolicy.MapPosition(
            screenX: -1700,
            screenY: 120,
            clientLeft: -1920,
            clientTop: 0,
            hotspotX: 4,
            hotspotY: 6);

        Assert.Equal(new CursorDrawPosition(216, 114), mapped);
    }

    [Theory]
    [InlineData(100, 50, true)]
    [InlineData(899, 649, true)]
    [InlineData(900, 649, false)]
    [InlineData(99, 50, false)]
    public void Hotspot_must_be_inside_target_client(
        int screenX,
        int screenY,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowCursorPolicy.IsInsideTarget(
                screenX,
                screenY,
                new PixelRect(100, 50, 800, 600)));
    }

    [Theory]
    [InlineData(100, 100, true, false)]
    [InlineData(100, 101, true, true)]
    [InlineData(100, 100, false, true)]
    public void Shape_is_reused_only_for_same_handle_with_cached_pixels(
        long previousHandle,
        long currentHandle,
        bool hasCachedShape,
        bool expectedRefresh)
    {
        Assert.Equal(
            expectedRefresh,
            WindowCursorPolicy.ShouldRefreshShape(
                (nint)previousHandle,
                (nint)currentHandle,
                hasCachedShape));
    }

    [Theory]
    [InlineData(4, 4, false)]
    [InlineData(4, 5, true)]
    [InlineData(0, 1, true)]
    public void Cursor_state_resets_when_target_revision_changes(
        long previousRevision,
        long currentRevision,
        bool expected)
    {
        Assert.Equal(
            expected,
            WindowCursorPolicy.ShouldReset(previousRevision, currentRevision));
    }
}
