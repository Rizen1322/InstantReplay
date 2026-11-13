using Aura.Core.Capture;
using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class FrameConversionPacingTests
{
    private const long Start = 100_000_000;
    private const long Second = 10_000_000;

    [Theory]
    [InlineData(96, 60)]
    [InlineData(144, 60)]
    [InlineData(240, 60)]
    [InlineData(144, 30)]
    [InlineData(30, 60)]
    public void One_output_clock_selects_latest_without_extra_repeats_under_conversion_jitter(int captureFps, int outputFps)
    {
        var result = RunOutput(captureFps, outputFps, stall: false);
        Assert.True(result.Samples.Count <= Math.Min(captureFps, outputFps) * 10 + 1);
        Assert.Equal(result.Samples.Count, result.Samples.Select(t => PullCfrPolicy.SlotIndex(t, Start, outputFps)).Distinct().Count());
        Assert.True(result.LatestWasSelected);
        Assert.True(result.Display.Zip(result.Display.Skip(1), (a, b) => b >= a).All(x => x));
        if (captureFps >= outputFps) Assert.Equal(0, result.RepeatsAfterWarmup);
        else Assert.InRange(result.RepeatsAfterWarmup, outputFps * 8 / 2 - 2, outputFps * 8 / 2 + 2);
    }

    [Fact]
    public void Independent_conversion_clock_reproduces_repeats_that_one_output_clock_avoids()
    {
        // Old worker: 60 Hz conversion completions straddle the encoder's 4 ms poll deadlines.
        // Identical capture, conversion durations and output scheduler; only ordering differs.
        int legacyRepeats = RunIndependentClocks();
        Assert.True(legacyRepeats > 20, $"legacy repeats: {legacyRepeats}");
        Assert.Equal(0, RunOutput(144, 60, stall: false).RepeatsAfterWarmup);
    }

    [Fact]
    public void Conversion_within_frame_budget_does_not_lose_fps_to_the_polling_sleep()
    {
        var result = RunOutput(144, 60, stall: false, longConversion: 150_000);
        Assert.InRange(result.Samples.Count, 599, 601);
        Assert.Equal(0, result.RepeatsAfterWarmup);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void Slow_conversion_skips_preparation_slots_without_a_catch_up_gpu_burst(int fps)
    {
        var result = RunOutput(240, fps, stall: true);
        Assert.Equal(result.Samples.Count, result.Samples.Select(t => PullCfrPolicy.SlotIndex(t, Start, fps)).Distinct().Count());
        Assert.True(result.Samples.Count < fps * 10 - fps / 10);
        Assert.True(result.LatestWasSelected);
        Assert.True(result.Display.Zip(result.Display.Skip(1), (a, b) => b >= a).All(x => x));
    }

    [Fact]
    public void Prepared_completion_slot_blocks_retries_while_output_catches_up()
    {
        long afterStall = Start + 3_000_000;
        long prepared = PullCfrPolicy.SlotIndex(afterStall, Start, 60);
        Assert.False(PullCfrPolicy.ShouldPrepareFrame(afterStall, 3, prepared, Start, 60));
        Assert.False(PullCfrPolicy.ShouldPrepareFrame(afterStall + 40_000, 7, prepared, Start, 60));
        Assert.True(PullCfrPolicy.ShouldPrepareFrame(Start + 3_200_000, prepared, prepared, Start, 60));
    }

    private sealed record OutputResult(List<long> Samples, List<long> Display, int RepeatsAfterWarmup, bool LatestWasSelected);

    private static OutputResult RunOutput(int captureFps, int outputFps, bool stall, long longConversion = 60_000)
    {
        using var broker = new CaptureFrameBroker<Slot>(() => new Slot(), _ => { });
        broker.Reset(1);
        long now = Start, end = Start + 10 * Second;
        long lastClosed = -1, lastPrepared = -1, lastConverted = long.MinValue;
        long candidate = long.MinValue, lastContent = long.MinValue, newest = long.MinValue;
        int input = 0, repeats = 0;
        var samples = new List<long>();
        var display = new List<long>();
        bool latestWasSelected = true;

        void PublishUntil(long wall)
        {
            while (Start + (long)input * Second / captureFps <= wall)
            {
                long captured = Start + (long)input++ * Second / captureFps;
                Assert.True(broker.Publish(1, captured, s => s.Timestamp = captured));
                newest = captured;
            }
        }

        while (now < end)
        {
            PublishUntil(now);
            if (PullCfrPolicy.ShouldPrepareFrame(now, lastClosed, lastPrepared, Start, outputFps))
            {
                if (broker.TryLeaseLatest(1, out var lease))
                    using (lease!)
                    {
                        if (lease!.Timestamp > lastConverted)
                        {
                            latestWasSelected &= lease.Timestamp == newest;
                            samples.Add(now);
                            // Real conversion holds the input lease while new frames continue arriving.
                            now += stall && samples.Count == 40 ? 3_000_000 : samples.Count % 2 == 0 ? longConversion : 10_000;
                            PublishUntil(now);
                            candidate = lastConverted = lease.Timestamp;
                        }
                    }
                lastPrepared = PullCfrPolicy.SlotIndex(now, Start, outputFps);
            }
            PullSlotPlan plan = PullCfrScheduler.Plan(now, lastClosed, Start, outputFps, 4,
                candidate != long.MinValue, candidate, lastContent);
            for (int i = 0; i < plan.CloseCount; i++)
            {
                long slot = ++lastClosed;
                bool fresh = i == plan.CloseCount - 1 && plan.UseCandidate;
                if (fresh) lastContent = candidate;
                if (slot >= outputFps && slot < outputFps * 9 && !fresh) repeats++;
                display.Add(lastContent);
            }
            now += PullCfrPolicy.PacerWaitTicks(now, lastClosed, Start, outputFps, 40_000);
        }
        return new OutputResult(samples, display, repeats, latestWasSelected);
    }

    private static int RunIndependentClocks()
    {
        const int fps = 60, captureFps = 144;
        var completions = Enumerable.Range(0, fps * 10).Select(i =>
        {
            long began = PullCfrPolicy.SlotPts(i, Start, fps);
            long capture = Start + (began - Start) * captureFps / Second * Second / captureFps;
            return (Ready: began + (i % 2 == 0 ? 10_000 : 60_000), Capture: capture);
        }).OrderBy(x => x.Ready).ToArray();
        int next = 0, repeats = 0;
        long candidate = long.MinValue, lastContent = long.MinValue, lastClosed = -1;
        for (long now = Start; now < Start + 10 * Second; now += 40_000)
        {
            while (next < completions.Length && completions[next].Ready <= now)
                candidate = completions[next++].Capture;
            PullSlotPlan plan = PullCfrScheduler.Plan(now, lastClosed, Start, fps, 4,
                candidate != long.MinValue, candidate, lastContent);
            for (int i = 0; i < plan.CloseCount; i++)
            {
                long slot = ++lastClosed;
                bool fresh = i == plan.CloseCount - 1 && plan.UseCandidate;
                if (fresh) lastContent = candidate;
                if (slot >= fps && slot < fps * 9 && !fresh) repeats++;
            }
        }
        return repeats;
    }

    private sealed class Slot { public long Timestamp; }
}
