using Aura.Core.Capture;
using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureRegressionTests
{
    [Fact]
    public void PacerMayFillWhenMftIsAlreadyWaitingForAFrame()
    {
        Assert.False(EncoderPacingPolicy.IsBehind(
            requestsPerSecond: 0,
            targetFps: 60,
            inputRequestWaitingForFrame: true));
    }

    [Fact]
    public void PacerStopsFillingWhenMftRequestRateIsActuallyLow()
    {
        Assert.True(EncoderPacingPolicy.IsBehind(
            requestsPerSecond: 20,
            targetFps: 60,
            inputRequestWaitingForFrame: false));
    }

    [Fact]
    public void MaskedColorKeepsRgbForXorPixels()
    {
        // Два BGRA-пикселя: первый заменяет фон, второй XOR-ится с ним.
        byte[] shape =
        [
            10, 20, 30, 0,
            40, 50, 60, 255
        ];

        byte[] packed = CursorShapePixels.ExpandMaskedColor(shape, width: 2, height: 1, pitch: 8);

        Assert.Equal(shape, packed);
    }

    [Fact]
    public void MonochromeShapePacksAndMaskWithTheSameXorInEveryColorChannel()
    {
        // Первый пиксель: AND=1, XOR=0; второй: AND=0, XOR=1.
        byte[] packed = CursorShapePixels.ExpandMonochrome(
            [0b1000_0000, 0b0100_0000], width: 2, height: 1, pitch: 1);

        Assert.Equal(new byte[]
        {
            0,   0,   0,   255,
            255, 255, 255, 0
        }, packed);
    }

    [Fact]
    public void DxgiPointerPositionIsAlreadyTheShapeTopLeft()
    {
        var origin = CursorShapePixels.GetDrawOrigin(
            pointerX: 640, pointerY: 360,
            hotspotX: 7, hotspotY: 4);

        Assert.Equal((640, 360), origin);
    }

    [Fact]
    public void CursorOnlyUpdateIsCapturedOnAnOtherwiseStaticDesktop()
    {
        Assert.True(DesktopFramePolicy.ShouldCapture(
            accumulatedFrames: 0,
            firstFrameSinceStart: false,
            cursorChanged: true));
    }

    [Fact]
    public void EmptyDesktopUpdateWithoutCursorChangeIsSkipped()
    {
        Assert.False(DesktopFramePolicy.ShouldCapture(
            accumulatedFrames: 0,
            firstFrameSinceStart: false,
            cursorChanged: false));
    }
}
