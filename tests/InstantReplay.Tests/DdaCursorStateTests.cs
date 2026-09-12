using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaCursorStateTests
{
    [Fact]
    public void RejectsUnknownShapeType()
    {
        Assert.False(DdaCursorShape.TryCreate(99, 32, 32, 128, new byte[4096],
            out var shape, out string reason));
        Assert.Null(shape);
        Assert.Contains("тип", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsTruncatedColorPayload()
    {
        Assert.False(DdaCursorShape.TryCreate(2, 32, 32, 128, new byte[4095],
            out _, out string reason));
        Assert.Contains("буфер", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResetPreventsShapeCrossingProviderGeneration()
    {
        Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4, [1, 2, 3, 255],
            out var shape, out _));
        var state = new DdaCursorState();
        state.Reset(7);
        Assert.True(state.Apply(7, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, true, true, 40, 50, shape)));

        state.Reset(8);

        Assert.False(state.Current.Visible);
        Assert.Null(state.Current.Shape);
        Assert.False(state.Apply(7, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, true, true, 1, 2, shape)));
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(-1, 32)]
    [InlineData(1025, 32)]
    [InlineData(32, 0)]
    [InlineData(32, 1025)]
    public void RejectsImpossibleDimensions(int width, int height) =>
        Assert.False(DdaCursorShape.TryCreate(2, width, height, 4096,
            new byte[4096], out _, out _));

    [Fact]
    public void RejectsPitchAndPayloadOverflow()
    {
        Assert.False(DdaCursorShape.TryCreate(2, 32, 32, 127,
            new byte[4096], out _, out _));
        Assert.False(DdaCursorShape.TryCreate(2, 32, 32, int.MaxValue,
            Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void AcceptsTwoMaskMonochromeShape()
    {
        Assert.True(DdaCursorShape.TryCreate(1, 2, 2, 1,
            [0b1000_0000, 0b0100_0000], out var shape, out _));
        Assert.Equal(1, shape!.Height);
        Assert.Equal(8, shape.Pixels.Length);
    }

    [Fact]
    public void PositionOnlyUpdateKeepsValidShape()
    {
        Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4,
            [1, 2, 3, 255], out var shape, out _));
        var state = new DdaCursorState();
        state.Reset(4);
        state.Apply(4, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, true, true, 10, 20, shape));

        state.Apply(4, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, true, true, 30, 40, null));

        Assert.Same(shape, state.Current.Shape);
        Assert.Equal((30, 40), (state.Current.X, state.Current.Y));
    }

    [Fact]
    public void ResetUpdateClearsCachedShapeWithinSameGeneration()
    {
        Assert.True(DdaCursorShape.TryCreate(2, 1, 1, 4,
            [1, 2, 3, 255], out var shape, out _));
        var state = new DdaCursorState();
        state.Reset(12);
        state.Apply(12, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, true, true, 10, 20, shape));

        Assert.True(state.Apply(12, new CaptureCursorUpdate(
            CaptureCursorMode.Separate, false, false, 0, 0, null, ResetState: true)));

        Assert.False(state.Current.Visible);
        Assert.Equal((0, 0), (state.Current.X, state.Current.Y));
        Assert.Null(state.Current.Shape);
    }
}
