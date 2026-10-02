using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class EncoderCfrPolicyTests
{
    /// <summary>60 fps: один кадр — 166 666.6 тика по 100 нс.</summary>
    private const long Frame60 = 10_000_000L / 60;
    private const int Fps = 60;

    /// <summary>Время слота номер <paramref name="n"/> на сетке 60 fps от нуля.</summary>
    private static long S(long n) => n * 10_000_000L / Fps;

    [Fact]
    public void Frame_on_the_grid_keeps_its_slot()
    {
        long baseTicks = 1_000_000;
        Assert.Equal(
            baseTicks + S(3),
            EncoderCfrPolicy.QuantizePts(
                ticks: baseTicks + S(3),
                baseTicks: baseTicks,
                lastPts: baseTicks + S(2),
                fps: Fps));
    }

    [Fact]
    public void Jitter_rounds_to_the_nearest_slot_in_both_directions()
    {
        long baseTicks = 0;
        long expected = S(4);

        // Кадр монитора 144 Гц приходит то раньше своего слота, то позже.
        Assert.Equal(expected, EncoderCfrPolicy.QuantizePts(
            expected - Frame60 / 3, baseTicks, S(3), Fps));
        Assert.Equal(expected, EncoderCfrPolicy.QuantizePts(
            expected + Frame60 / 3, baseTicks, S(3), Fps));
    }

    [Fact]
    public void Real_frame_takes_the_next_slot_when_its_own_is_occupied()
    {
        // Слот 3 уже занят дубликатом пейсера — настоящий кадр не выбрасываем.
        long lastPts = S(3);
        Assert.Equal(
            S(4),
            EncoderCfrPolicy.QuantizePts(S(3), baseTicks: 0, lastPts, Fps));
    }

    [Fact]
    public void Frame_late_behind_duplicates_is_dropped_instead_of_shifting_video()
    {
        // Пейсер закрыл слоты 3..6 повторами, пока кадр захвата шёл с опозданием.
        // Кадр слота 3 не должен вставать в слот 7: видео уехало бы от звука.
        long lastPts = S(6);
        Assert.Null(EncoderCfrPolicy.QuantizePts(S(3), baseTicks: 0, lastPts, Fps));
        // Свой слот 7 занимает свой кадр
        Assert.Equal(S(7), EncoderCfrPolicy.QuantizePts(S(7), 0, lastPts, Fps));
    }

    [Fact]
    public void Shift_never_accumulates_over_many_hiccups()
    {
        // Сто эпизодов: каждый раз кадр приходит в уже занятый слот. Сдвиг
        // относительно времени захвата не должен превышать одного слота.
        long last = 0;
        for (int slot = 1; slot < 1000; slot++)
        {
            long ticks = S(slot);
            if (slot % 10 == 0) last = Math.Max(last, ticks + S(3));   // повторы ушли вперёд
            if (EncoderCfrPolicy.QuantizePts(ticks, 0, last, Fps) is long pts)
            {
                Assert.True(pts - ticks <= Frame60 + 1, $"сдвиг {(pts - ticks) / (double)Frame60:F1} слота");
                last = pts;
            }
        }
    }

    [Fact]
    public void Adjacent_frames_need_no_backfill()
    {
        Assert.Equal(0, EncoderCfrPolicy.BackfillSlots(
            lastPts: S(2), pts: S(3), 0, Fps, maxSlots: 8));
    }

    [Fact]
    public void Short_gap_is_filled_with_duplicates()
    {
        // Между вторым и пятым слотами пустуют третий и четвёртый.
        Assert.Equal(2, EncoderCfrPolicy.BackfillSlots(
            lastPts: S(2), pts: S(5), 0, Fps, maxSlots: 8));
    }

    [Fact]
    public void Long_pause_is_left_to_the_pacer()
    {
        // Девять пустых слотов при пределе в восемь — дозаполнять не наше дело.
        Assert.Equal(0, EncoderCfrPolicy.BackfillSlots(
            lastPts: 0, pts: S(10), 0, Fps, maxSlots: 8));
    }

    [Fact]
    public void Backfill_never_exceeds_the_limit()
    {
        // Ровно на границе: девять слотов разрыва, восемь из них наши.
        Assert.Equal(8, EncoderCfrPolicy.BackfillSlots(
            lastPts: 0, pts: S(9), 0, Fps, maxSlots: 8));
    }

    [Fact]
    public void Zero_frame_duration_is_survivable()
    {
        // Частота кадров ещё не задана — счёт не должен делить на ноль.
        Assert.Equal(12345L, EncoderCfrPolicy.QuantizePts(12345, 0, 0, fps: 0));
        Assert.Equal(0, EncoderCfrPolicy.BackfillSlots(0, 12345, 0, fps: 0, maxSlots: 8));
    }

    [Fact]
    public void Grid_does_not_drift_from_real_time_over_hours()
    {
        // Три часа при 60 fps: слот номер N обязан стоять ровно на N/60 секунды.
        // Раньше слоты складывались из целой длительности 166 666 тиков и
        // отставали от часов на 14 мс в час.
        long slots = 3L * 3600 * Fps;
        long pts = 0;
        for (long i = 0; i < slots; i++) pts = EncoderCfrPolicy.NextSlot(pts, 0, Fps);
        Assert.Equal(3L * 3600 * 10_000_000, pts);

        // И обратно: время захвата через три часа попадает в свой слот
        Assert.Equal(slots, EncoderCfrPolicy.SlotIndex(3L * 3600 * 10_000_000 + 1000, 0, Fps));
    }

    [Theory]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(30)]
    public void Every_slot_is_within_one_tick_of_exact_time(int fps)
    {
        for (long n = 0; n < 100_000; n += 997)
        {
            long exact = (long)Math.Round(n * 10_000_000.0 / fps);
            Assert.InRange(EncoderCfrPolicy.SlotPts(n, 0, fps), exact - 1, exact + 1);
            Assert.Equal(n, EncoderCfrPolicy.SlotIndex(EncoderCfrPolicy.SlotPts(n, 0, fps), 0, fps));
        }
    }

    [Fact]
    public void Frame_before_base_rounds_down_not_toward_zero()
    {
        Assert.Equal(-1, EncoderCfrPolicy.SlotIndex(-S(1), 0, Fps));
        Assert.Equal(0, EncoderCfrPolicy.SlotIndex(-S(1) / 3, 0, Fps));
    }

    [Fact]
    public void Capture_limits_to_two_frames_per_slot()
    {
        const long interval = 10_000_000 / 60;
        Assert.False(EncoderCfrPolicy.CaptureTooSoon(5_000_000, 0, 60));
        Assert.True(EncoderCfrPolicy.CaptureTooSoon(5_000_000 + interval / 4, 5_000_000, 60));
        Assert.False(EncoderCfrPolicy.CaptureTooSoon(5_000_000 + interval / 2, 5_000_000, 60));
    }

    [Fact]
    public void Frame_near_slot_border_is_not_redundant()
    {
        const long interval = 10_000_000 / 60;
        Assert.True(EncoderCfrPolicy.RedundantInSlot(1_000 + interval / 3, 0, 0, 1_000, 60));
        Assert.False(EncoderCfrPolicy.RedundantInSlot(interval * 2 / 5, 0, 0, interval * 2 / 5 - interval * 4 / 5, 60));
        Assert.True(EncoderCfrPolicy.RedundantInSlot(interval / 10, 0, 0, interval / 10 - interval * 4 / 5, 60));
        Assert.False(EncoderCfrPolicy.RedundantInSlot(1_000, interval, 0, 1_000, 60));
    }

    /// <summary>
    /// Игра чаще частоты записи, сетка энкодера сдвинута на полслота от первого кадра
    /// захвата (так было у друга в L4D2: 65% настоящих кадров). Каждый слот должен
    /// получить настоящий кадр, и видео не уезжает вперёд больше чем на слот.
    /// </summary>
    [Theory]
    [InlineData(75.0)]
    [InlineData(90.0)]
    [InlineData(144.0)]
    [InlineData(240.0)]
    [InlineData(59.94)]
    [InlineData(114.0)]
    [InlineData(60.0)]
    public void Every_slot_gets_a_real_frame_when_game_is_faster(double gameFps)
    {
        const int fps = 60;
        const long interval = 10_000_000 / fps;
        var random = new Random(7);
        long start = 1_000_000_000;
        long baseTicks = start + interval / 2;
        long lastAccepted = 0, lastPts = long.MinValue, lastRealTicks = 0;
        long leadSum = 0;
        var filled = new HashSet<long>();
        for (int i = 0; i < (int)(gameFps * 30); i++)
        {
            long ticks = start + (long)(i * 10_000_000 / gameFps) + random.Next(-3_000, 3_000);
            if (EncoderCfrPolicy.CaptureTooSoon(ticks, lastAccepted, fps)) continue;
            lastAccepted = ticks;
            long natural = EncoderCfrPolicy.NaturalSlot(ticks, baseTicks, fps);
            if (EncoderCfrPolicy.RedundantInSlot(ticks, natural, lastPts, lastRealTicks, fps)) continue;
            long? placed = lastPts == long.MinValue
                ? natural
                : EncoderCfrPolicy.QuantizePts(ticks, baseTicks, lastPts, fps);
            lastRealTicks = ticks;
            if (placed is not long pts) continue;
            Assert.True(pts - natural <= interval + 1, "видео ушло вперёд больше чем на слот");
            leadSum += pts - ticks;
            filled.Add(EncoderCfrPolicy.SlotIndex(pts, baseTicks, fps));
            lastPts = pts;
        }
        long span = filled.Max() - filled.Min() + 1;
        Assert.True(filled.Count >= span - 2, $"настоящих кадров {filled.Count} из {span}");
        Assert.True(leadSum / filled.Count < interval / 2, "видео постоянно позже звука на слот");
    }
}
