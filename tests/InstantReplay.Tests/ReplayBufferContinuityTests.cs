using Aura.Core.Buffering;
using Xunit;

namespace InstantReplay.Tests;

public sealed class ReplayBufferContinuityTests
{
    private const long Second = 10_000_000;
    private const long Bitrate = 10_000_000;

    [Fact]
    public void CompatibleCaptureRestartKeepsEncodedFramesInRam()
    {
        var buffer = CreatePopulatedBuffer();
        long bytes = buffer.TotalBytes;
        long duration = buffer.BufferedDurationTicks;

        bool retained = buffer.PrepareForCaptureRestart(
            formatCompatible: true, Bitrate, seconds: 5);

        Assert.True(retained);
        Assert.Equal(bytes, buffer.TotalBytes);
        Assert.Equal(duration, buffer.BufferedDurationTicks);
    }

    [Fact]
    public void IncompatibleCaptureRestartClearsVideoInRamWithoutSnapshotting()
    {
        var buffer = CreatePopulatedBuffer();

        bool retained = buffer.PrepareForCaptureRestart(
            formatCompatible: false, Bitrate, seconds: 5);

        Assert.False(retained);
        Assert.Equal(0, buffer.TotalBytes);
        Assert.Equal(0, buffer.BufferedDurationTicks);
    }

    private static ReplayVideoBuffer CreatePopulatedBuffer()
    {
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 5 * Second };
        buffer.Allocate(Bitrate, seconds: 5);
        byte[] data = new byte[64];
        buffer.Add(new EncodedFrame(data, 0, data.Length, 0, Second, IsKeyframe: true));
        buffer.Add(new EncodedFrame(data, 0, data.Length, Second, Second, IsKeyframe: false));
        return buffer;
    }
}
