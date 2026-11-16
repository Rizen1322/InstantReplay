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

    [Fact]
    public void Buffer_without_keyframes_restarts_clean_and_asks_for_keyframe()
    {
        // Единственный ключевой кадр в начале, дальше одни P-кадры (сломанный GOP).
        // Арену заполняем ровно, следующий P-кадр требует места — вытеснение съедает
        // ключевой кадр, и в кольце не остаётся ни одного. Раньше голова оставалась
        // P-кадром: буфер формально непустой, но декодировать его нельзя, и повтор
        // молча начинался с «каши». Теперь буфер обязан очиститься и попросить ключ.
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 3600 * Second };
        buffer.Allocate(1_000_000, seconds: 1);
        int asked = 0;
        buffer.KeyframeNeeded += () => asked++;

        long capacity = buffer.CapacityBytes;
        int frameSize = (int)Math.Min(ArenaStorage.ChunkBytes, capacity / 4);
        Assert.Equal(0L, capacity % frameSize);
        var data = new byte[frameSize];

        buffer.Add(new EncodedFrame(data, 0, frameSize, 0, Second, IsKeyframe: true));
        for (int i = 1; i < capacity / frameSize; i++)
            buffer.Add(new EncodedFrame(data, 0, frameSize, i * Second, Second, IsKeyframe: false));
        Assert.Equal(capacity, buffer.TotalBytes);
        Assert.Equal(0, asked);
        long dropped = buffer.DroppedFrames;

        buffer.Add(new EncodedFrame(data, 0, frameSize, capacity / frameSize * Second, Second,
                                    IsKeyframe: false));

        Assert.Equal(1, asked);
        Assert.Equal(dropped + 1, buffer.DroppedFrames);
        Assert.Equal(0, buffer.TotalBytes);
        Assert.Equal(0, buffer.BufferedDurationTicks);
        using (var empty = buffer.TakeSnapshot(Second, out _))
            Assert.Empty(empty.Frames);

        // Следующий ключевой кадр принимается — буфер снова накапливает кадры.
        buffer.Add(new EncodedFrame(data, 0, frameSize, 100 * Second, Second, IsKeyframe: true));
        buffer.Add(new EncodedFrame(data, 0, frameSize, 101 * Second, Second, IsKeyframe: false));
        Assert.Equal(2L * frameSize, buffer.TotalBytes);

        using var snapshot = buffer.TakeSnapshot(Second, out _);
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot[0].IsKeyframe, "новый повтор обязан начинаться с ключевого кадра");
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
