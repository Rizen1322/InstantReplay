using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public sealed class CaptureFrameBrokerTests
{
    [Fact]
    public void PublishesAndLeasesNewestFrame()
    {
        using var broker = CreateBroker();
        broker.Reset(3);
        Assert.True(broker.Publish(3, 100, s => s.Value = 10));
        Assert.True(broker.Publish(3, 200, s => s.Value = 20));

        Assert.True(broker.TryLeaseLatest(3, out var lease));
        using (CaptureFrameLease<FakeSlot> current = Assert.IsType<CaptureFrameLease<FakeSlot>>(lease))
        {
            Assert.Equal(20, current.Slot.Value);
            Assert.Equal(200, current.Timestamp);
            Assert.Equal(3, current.Generation);
        }
    }

    [Fact]
    public void FourthFrameReplacesOldestUnleasedFrame()
    {
        using var broker = CreateBroker();
        broker.Reset(5);
        broker.Publish(5, 10, s => s.Value = 1);
        broker.Publish(5, 20, s => s.Value = 2);
        broker.Publish(5, 30, s => s.Value = 3);
        broker.Publish(5, 40, s => s.Value = 4);

        Assert.True(broker.TryLeaseLatest(5, out var newest));
        Assert.True(broker.TryLeaseLatest(5, out var middle));
        Assert.True(broker.TryLeaseLatest(5, out var oldest));
        using (CaptureFrameLease<FakeSlot> newestLease = Assert.IsType<CaptureFrameLease<FakeSlot>>(newest))
        using (CaptureFrameLease<FakeSlot> middleLease = Assert.IsType<CaptureFrameLease<FakeSlot>>(middle))
        using (CaptureFrameLease<FakeSlot> oldestLease = Assert.IsType<CaptureFrameLease<FakeSlot>>(oldest))
        {
            Assert.Equal([4, 3, 2],
                new[] { newestLease.Slot.Value, middleLease.Slot.Value, oldestLease.Slot.Value });
        }
    }

    [Fact]
    public void NeverOverwritesLeasedSlot()
    {
        using var broker = CreateBroker();
        broker.Reset(1);
        broker.Publish(1, 1, s => s.Value = 1);
        broker.TryLeaseLatest(1, out var held);

        using (CaptureFrameLease<FakeSlot> heldLease = Assert.IsType<CaptureFrameLease<FakeSlot>>(held))
        {
            broker.Publish(1, 2, s => s.Value = 2);
            broker.Publish(1, 3, s => s.Value = 3);
            broker.Publish(1, 4, s => s.Value = 4);
            Assert.Equal(1, heldLease.Slot.Value);
        }
    }

    [Fact]
    public void RejectsStaleGenerationWithoutRunningWriter()
    {
        using var broker = CreateBroker();
        broker.Reset(8);
        bool writerRan = false;

        Assert.False(broker.Publish(7, 10, _ => writerRan = true));
        Assert.False(writerRan);
        Assert.False(broker.TryLeaseLatest(7, out _));
    }

    [Fact]
    public async Task FreshestLeaseWaitsForFramePublishedAfterRequest()
    {
        using var broker = CreateBroker();
        broker.Reset(11);
        broker.Publish(11, 100, slot => slot.Value = 10);
        using var waiting = new ManualResetEventSlim();

        Task<(bool Leased, bool Fresh, int Value, long Timestamp)> reader = Task.Run(() =>
        {
            waiting.Set();
            bool leased = broker.TryLeaseFreshest(
                11,
                TimeSpan.FromSeconds(2),
                out CaptureFrameLease<FakeSlot>? lease,
                out bool fresh);
            using (lease)
                return (leased, fresh, lease?.Slot.Value ?? -1, lease?.Timestamp ?? -1);
        });

        Assert.True(waiting.Wait(TimeSpan.FromSeconds(1)));
        await Task.Delay(75);
        Assert.True(broker.Publish(11, 200, slot => slot.Value = 20));

        var result = await reader.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.Leased);
        Assert.True(result.Fresh);
        Assert.Equal(20, result.Value);
        Assert.Equal(200, result.Timestamp);
    }

    [Fact]
    public void FreshestLeaseFallsBackToCachedFrameAfterTimeout()
    {
        using var broker = CreateBroker();
        broker.Reset(12);
        broker.Publish(12, 300, slot => slot.Value = 30);

        Assert.True(broker.TryLeaseFreshest(
            12,
            TimeSpan.FromMilliseconds(30),
            out CaptureFrameLease<FakeSlot>? lease,
            out bool fresh));

        using (CaptureFrameLease<FakeSlot> current =
               Assert.IsType<CaptureFrameLease<FakeSlot>>(lease))
        {
            Assert.False(fresh);
            Assert.Equal(30, current.Slot.Value);
            Assert.Equal(300, current.Timestamp);
        }
    }

    [Fact]
    public void ResetClearsReadyFramesAndKeepsLifetimeCounters()
    {
        using var broker = CreateBroker();
        broker.Reset(1);
        broker.Publish(1, 10, s => s.Value = 1);

        broker.Reset(2);

        Assert.False(broker.TryLeaseLatest(2, out _));
        Assert.Equal(1, broker.FramesPublished);
        Assert.Equal(0, broker.LatestTimestamp);
        Assert.Equal(2, broker.Generation);
    }

    [Fact]
    public void LeaseFromOldGenerationReturnsSafelyAfterReset()
    {
        using var broker = CreateBroker();
        broker.Reset(1);
        broker.Publish(1, 10, s => s.Value = 10);
        broker.TryLeaseLatest(1, out var oldLease);

        broker.Reset(2);
        broker.Publish(2, 20, s => s.Value = 20);
        broker.Publish(2, 30, s => s.Value = 30);
        CaptureFrameLease<FakeSlot> returnedLease = Assert.IsType<CaptureFrameLease<FakeSlot>>(oldLease);
        returnedLease.Dispose();
        returnedLease.Dispose();

        Assert.True(broker.Publish(2, 40, s => s.Value = 40));
        Assert.True(broker.TryLeaseLatest(2, out var current));
        using (CaptureFrameLease<FakeSlot> currentLease = Assert.IsType<CaptureFrameLease<FakeSlot>>(current))
            Assert.Equal(40, currentLease.Slot.Value);
    }

    [Fact]
    public void WriterExceptionRollsSlotBackToFree()
    {
        using var broker = CreateBroker();
        broker.Reset(9);

        Assert.Throws<InvalidOperationException>(() =>
            broker.Publish(9, 1, _ => throw new InvalidOperationException("copy failed")));

        Assert.True(broker.Publish(9, 2, s => s.Value = 22));
        Assert.True(broker.TryLeaseLatest(9, out var lease));
        using (CaptureFrameLease<FakeSlot> current = Assert.IsType<CaptureFrameLease<FakeSlot>>(lease))
            Assert.Equal(22, current.Slot.Value);
        Assert.Equal(1, broker.FramesPublished);
    }

    [Fact]
    public void CountsDropWhenAllThreeSlotsAreLeased()
    {
        using var broker = CreateBroker();
        broker.Reset(4);
        var leases = new List<CaptureFrameLease<FakeSlot>>();

        for (int i = 1; i <= 3; i++)
        {
            Assert.True(broker.Publish(4, i, s => s.Value = i));
            Assert.True(broker.TryLeaseLatest(4, out var lease));
            leases.Add(lease!);
        }

        Assert.False(broker.Publish(4, 4, s => s.Value = 4));
        Assert.Equal(1, broker.FramesDroppedNoSlot);
        foreach (var lease in leases) lease.Dispose();
    }

    [Fact]
    public void DisposesExactlyThreeSlotsExactlyOnce()
    {
        var slots = new List<FakeSlot>();
        var broker = new CaptureFrameBroker<FakeSlot>(
            () =>
            {
                var slot = new FakeSlot();
                slots.Add(slot);
                return slot;
            },
            slot => slot.DisposeCount++);

        Assert.Equal(3, slots.Count);
        broker.Dispose();
        broker.Dispose();

        Assert.All(slots, slot => Assert.Equal(1, slot.DisposeCount));
    }

    private static CaptureFrameBroker<FakeSlot> CreateBroker() =>
        new(() => new FakeSlot(), slot => slot.DisposeCount++);

    private sealed class FakeSlot
    {
        public int Value { get; set; }
        public int DisposeCount { get; set; }
    }
}
