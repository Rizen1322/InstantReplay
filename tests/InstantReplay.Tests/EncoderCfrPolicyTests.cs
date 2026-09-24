using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class EncoderCfrPolicyTests
{
    /// <summary>60 fps: один кадр — 166 666.6 тика по 100 нс.</summary>
    private const long Frame60 = 10_000_000L / 60;

    [Fact]
    public void Frame_on_the_grid_keeps_its_slot()
    {
        long baseTicks = 1_000_000;
        Assert.Equal(
            baseTicks + Frame60 * 3,
            EncoderCfrPolicy.QuantizePts(
                ticks: baseTicks + Frame60 * 3,
                baseTicks: baseTicks,
                lastPts: baseTicks + Frame60 * 2,
                frameDurationTicks: Frame60));
    }

    [Fact]
    public void Jitter_rounds_to_the_nearest_slot_in_both_directions()
    {
        long baseTicks = 0;
        long expected = Frame60 * 4;

        // Кадр монитора 144 Гц приходит то раньше своего слота, то позже.
        Assert.Equal(expected, EncoderCfrPolicy.QuantizePts(
            expected - Frame60 / 3, baseTicks, Frame60 * 3, Frame60));
        Assert.Equal(expected, EncoderCfrPolicy.QuantizePts(
            expected + Frame60 / 3, baseTicks, Frame60 * 3, Frame60));
    }

    [Fact]
    public void Real_frame_takes_the_next_slot_when_its_own_is_occupied()
    {
        // Слот 3 уже занят дубликатом пейсера — настоящий кадр не выбрасываем.
        long lastPts = Frame60 * 3;
        Assert.Equal(
            Frame60 * 4,
            EncoderCfrPolicy.QuantizePts(Frame60 * 3, baseTicks: 0, lastPts, Frame60));
    }

    [Fact]
    public void Frame_late_behind_duplicates_is_dropped_instead_of_shifting_video()
    {
        // Пейсер закрыл слоты 3..6 повторами, пока кадр захвата шёл с опозданием.
        // Кадр слота 3 не должен вставать в слот 7: видео уехало бы от звука.
        long lastPts = Frame60 * 6;
        Assert.Null(EncoderCfrPolicy.QuantizePts(Frame60 * 3, baseTicks: 0, lastPts, Frame60));
        // Свой слот 7 занимает свой кадр
        Assert.Equal(Frame60 * 7, EncoderCfrPolicy.QuantizePts(Frame60 * 7, 0, lastPts, Frame60));
    }

    [Fact]
    public void Shift_never_accumulates_over_many_hiccups()
    {
        // Сто эпизодов: каждый раз кадр приходит в уже занятый слот. Сдвиг
        // относительно времени захвата не должен превышать одного слота.
        long last = 0;
        for (int slot = 1; slot < 1000; slot++)
        {
            long ticks = Frame60 * slot;
            if (slot % 10 == 0) last = Math.Max(last, ticks + Frame60 * 3);   // повторы ушли вперёд
            if (EncoderCfrPolicy.QuantizePts(ticks, 0, last, Frame60) is long pts)
            {
                Assert.True(pts - ticks <= Frame60, $"сдвиг {(pts - ticks) / (double)Frame60:F1} слота");
                last = pts;
            }
        }
    }

    [Fact]
    public void Adjacent_frames_need_no_backfill()
    {
        Assert.Equal(0, EncoderCfrPolicy.BackfillSlots(
            lastPts: Frame60 * 2, pts: Frame60 * 3, Frame60, maxSlots: 8));
    }

    [Fact]
    public void Short_gap_is_filled_with_duplicates()
    {
        // Между вторым и пятым слотами пустуют третий и четвёртый.
        Assert.Equal(2, EncoderCfrPolicy.BackfillSlots(
            lastPts: Frame60 * 2, pts: Frame60 * 5, Frame60, maxSlots: 8));
    }

    [Fact]
    public void Long_pause_is_left_to_the_pacer()
    {
        // Девять пустых слотов при пределе в восемь — дозаполнять не наше дело.
        Assert.Equal(0, EncoderCfrPolicy.BackfillSlots(
            lastPts: 0, pts: Frame60 * 10, Frame60, maxSlots: 8));
    }

    [Fact]
    public void Backfill_never_exceeds_the_limit()
    {
        // Ровно на границе: девять слотов разрыва, восемь из них наши.
        Assert.Equal(8, EncoderCfrPolicy.BackfillSlots(
            lastPts: 0, pts: Frame60 * 9, Frame60, maxSlots: 8));
    }

    [Fact]
    public void Zero_frame_duration_is_survivable()
    {
        // Частота кадров ещё не задана — счёт не должен делить на ноль.
        Assert.Equal(12345L, EncoderCfrPolicy.QuantizePts(12345, 0, 0, frameDurationTicks: 0));
        Assert.Equal(0, EncoderCfrPolicy.BackfillSlots(0, 12345, frameDurationTicks: 0, maxSlots: 8));
    }
}
