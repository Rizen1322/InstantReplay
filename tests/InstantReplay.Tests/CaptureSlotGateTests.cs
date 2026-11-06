using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Отбор кадров захвата по сетке слотов.
///
/// Зачем он вообще: прежний отбор по времени (полслота) терял кадры ПАЧКАМИ — на
/// 165-Гц мониторе из 4144 присланных кадров в записи участвовали 2810, потому что
/// кадры приходят неровно: два через 5 мс, потом пауза. Второй из пары выбрасывался,
/// а следующий слот оставался без кандидата.
/// </summary>
public sealed class CaptureSlotGateTests
{
    /// <summary>Ровно один кадр на слот: столько кандидатов и нужно сетке 60 fps.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void One_per_slot_gives_the_output_rate(int hz)
    {
        int admitted = Count(hz, seconds: 3, perSlot: 1);
        Assert.InRange(admitted / 3.0, 58, 61);
    }

    /// <summary>Два кадра на слот — режим pull-модели: запас в один кадр на выбор.</summary>
    [Theory]
    [InlineData(75)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void Two_per_slot_double_the_rate(int hz)
    {
        int admitted = Count(hz, seconds: 3, perSlot: 2);
        Assert.InRange(admitted / 3.0, 61, 121);
    }

    /// <summary>
    /// Кадры приходят неровно (джиттер вперёд-назад), но ни один слот не получает
    /// больше разрешённого, и ни один кадр не «открывает» слот дважды.
    /// </summary>
    [Theory]
    [InlineData(144, 1)]
    [InlineData(144, 2)]
    [InlineData(165, 2)]
    [InlineData(60, 1)]
    public void No_slot_gets_more_frames_than_allowed(int hz, int perSlot)
    {
        const int fps = 60;
        const int seconds = 3;
        long start = 10_000_000;
        double interval = PullCfrPolicy.TicksPerSecond / (double)hz;
        var rnd = new Random(4242);

        var gate = new CaptureSlotGate(fps, perSlot);
        long baseTicks = -1;
        var perSlotCount = new Dictionary<long, int>();

        for (int i = 0; i < hz * seconds; i++)
        {
            long ticks = start + (long)Math.Round(i * interval + (rnd.NextDouble() * 2 - 1) * 2000);
            if (!gate.Admit(ticks)) continue;
            if (baseTicks < 0) baseTicks = ticks;

            long slot = (ticks - baseTicks + PullCfrPolicy.TicksPerSecond / fps / 2) * fps
                        / PullCfrPolicy.TicksPerSecond;
            perSlotCount[slot] = perSlotCount.TryGetValue(slot, out int n) ? n + 1 : 1;
        }

        Assert.NotEmpty(perSlotCount);
        Assert.All(perSlotCount.Values, count => Assert.InRange(count, 1, perSlot));
    }

    /// <summary>Без ограничения по частоте (fps = 0) берём всё: сетки нет.</summary>
    [Fact]
    public void Without_a_target_rate_everything_is_admitted()
    {
        var gate = new CaptureSlotGate(fps: 0);
        Assert.True(gate.Admit(1_000_000));
        Assert.True(gate.Admit(1_000_001));
        Assert.True(gate.Admit(1_000_002));
    }

    private static int Count(int hz, int seconds, int perSlot)
    {
        double interval = PullCfrPolicy.TicksPerSecond / (double)hz;
        const long start = 5_000_000;
        var gate = new CaptureSlotGate(fps: 60, perSlot);
        int admitted = 0;
        for (int i = 0; i < hz * seconds; i++)
        {
            if (gate.Admit(start + (long)Math.Round(i * interval))) admitted++;
        }
        return admitted;
    }
}
