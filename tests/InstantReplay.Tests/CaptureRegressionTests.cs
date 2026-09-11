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

    [Fact]
    public void DuplicationRecoveryRetriesTemporaryFailuresUntilSuccess()
    {
        int attempts = 0;

        var result = DuplicationRecovery.Run(
            isRunning: () => true,
            resetCurrent: () => { },
            create: () =>
            {
                attempts++;
                if (attempts < 3) throw new UnauthorizedAccessException();
            },
            delay: _ => { },
            isTemporary: ex => ex is UnauthorizedAccessException);

        Assert.Equal(DuplicationRecoveryStatus.Restored, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void DuplicationRecoveryStopsRetryingWhenCaptureStops()
    {
        bool running = true;
        int attempts = 0;

        var result = DuplicationRecovery.Run(
            isRunning: () => running,
            resetCurrent: () => { },
            create: () =>
            {
                attempts++;
                throw new UnauthorizedAccessException();
            },
            delay: milliseconds =>
            {
                if (milliseconds == DuplicationRecovery.RetryDelayMilliseconds)
                    running = false;
            },
            isTemporary: ex => ex is UnauthorizedAccessException);

        Assert.Equal(DuplicationRecoveryStatus.Stopped, result.Status);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void DuplicationRecoveryEscalatesPermanentFailureWithoutLooping()
    {
        int attempts = 0;
        var permanent = new InvalidOperationException("wrong adapter");

        var result = DuplicationRecovery.Run(
            isRunning: () => true,
            resetCurrent: () => { },
            create: () =>
            {
                attempts++;
                throw permanent;
            },
            delay: _ => { },
            isTemporary: _ => false);

        Assert.Equal(DuplicationRecoveryStatus.Failed, result.Status);
        Assert.Same(permanent, result.Error);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))] // E_ACCESSDENIED: secure desktop / смена режима
    [InlineData(unchecked((int)0x887A0004))] // DXGI_ERROR_UNSUPPORTED: текущий desktop mode
    [InlineData(unchecked((int)0x887A0022))] // DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
    [InlineData(unchecked((int)0x887A0025))] // DXGI_ERROR_MODE_CHANGE_IN_PROGRESS
    [InlineData(unchecked((int)0x887A0026))] // DXGI_ERROR_ACCESS_LOST
    [InlineData(unchecked((int)0x887A0028))] // DXGI_ERROR_SESSION_DISCONNECTED
    public void DuplicationRecoveryRecognizesTemporaryHResults(int hresult)
    {
        Assert.True(DuplicationRecovery.IsTemporaryHResult(hresult));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070057))] // E_INVALIDARG: неверный адаптер
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    public void DuplicationRecoveryEscalatesNonTemporaryHResults(int hresult)
    {
        Assert.False(DuplicationRecovery.IsTemporaryHResult(hresult));
    }
}
