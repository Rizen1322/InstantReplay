using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class VideoOutputGeometryTests
{
    [Fact]
    public void Narrow_window_is_centered_in_sixteen_by_nine_canvas()
    {
        VideoRect result = VideoOutputGeometry.Fit(1280, 1024, 1920, 1080);

        Assert.Equal(new VideoRect(285, 0, 1350, 1080), result);
    }

    [Fact]
    public void Wide_window_is_letterboxed()
    {
        VideoRect result = VideoOutputGeometry.Fit(3440, 1440, 1920, 1080);

        Assert.Equal(new VideoRect(0, 138, 1920, 804), result);
    }

    [Fact]
    public void Exact_aspect_uses_entire_canvas()
    {
        Assert.Equal(
            new VideoRect(0, 0, 1920, 1080),
            VideoOutputGeometry.Fit(2560, 1440, 1920, 1080));
    }

    [Theory]
    [InlineData(1, 100, 1920, 1080)]
    [InlineData(100, 1, 1920, 1080)]
    [InlineData(100, 100, 1919, 1079)]
    public void Destination_dimensions_are_positive_even_and_inside_canvas(
        int sourceWidth,
        int sourceHeight,
        int canvasWidth,
        int canvasHeight)
    {
        VideoRect result = VideoOutputGeometry.Fit(
            sourceWidth, sourceHeight, canvasWidth, canvasHeight);

        Assert.True(result.Width > 0 && result.Height > 0);
        Assert.Equal(0, result.Width % 2);
        Assert.Equal(0, result.Height % 2);
        Assert.InRange(result.X, 0, canvasWidth - result.Width);
        Assert.InRange(result.Y, 0, canvasHeight - result.Height);
    }

    [Theory]
    [InlineData(0, 1080, 1920, 1080)]
    [InlineData(1920, 0, 1920, 1080)]
    [InlineData(1920, 1080, 0, 1080)]
    [InlineData(1920, 1080, 1920, 0)]
    public void Zero_dimension_is_rejected(
        int sourceWidth,
        int sourceHeight,
        int canvasWidth,
        int canvasHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoOutputGeometry.Fit(sourceWidth, sourceHeight, canvasWidth, canvasHeight));
    }
}
