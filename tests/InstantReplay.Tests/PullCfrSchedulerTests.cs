using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Что пейсер кладёт в слоты при отставании. Главная опасность: свежий кадр,
/// поставленный в ПРОСРОЧЕННЫЙ слот, — после него идут повторы более старого
/// кадра, и содержимое едет назад по времени.
/// </summary>
public sealed class PullCfrSchedulerTests
{
    private const int Fps = 60;
    private const int MaxBurst = 4;
    private const long BaseTicks = 100_000_000;

    [Fact]
    public void First_slot_is_closed_at_once_with_the_first_candidate()
    {
        // Слот 0 начинается вместе с первым кадром, поэтому закрывается сразу:
        // в записи он не должен быть повтором «пустоты».
        PullSlotPlan plan = PullCfrScheduler.Plan(
            now: BaseTicks, lastClosedSlot: -1, baseTicks: BaseTicks, fps: Fps, maxBurst: MaxBurst,
            hasCandidate: true, candidateCaptureTicks: BaseTicks, lastContentTicks: -1);

        Assert.Equal(1, plan.CloseCount);
        Assert.True(plan.UseCandidate);

        // А до начала сетки закрывать нечего.
        Assert.Equal(PullSlotPlan.None, PullCfrScheduler.Plan(
            now: BaseTicks - 1, lastClosedSlot: -1, baseTicks: BaseTicks, fps: Fps, maxBurst: MaxBurst,
            hasCandidate: true, candidateCaptureTicks: BaseTicks, lastContentTicks: -1));
    }

    [Fact]
    public void Fresh_frame_goes_to_the_last_slot_of_a_catch_up()
    {
        // Пейсер не заглядывал 3 слота: закрываем три, свежий кадр — только в третий.
        long now = BaseTicks + 3 * Slot() + 1;
        PullSlotPlan plan = PullCfrScheduler.Plan(
            now, lastClosedSlot: -1, BaseTicks, Fps, MaxBurst,
            hasCandidate: true, candidateCaptureTicks: now, lastContentTicks: BaseTicks);

        Assert.Equal(3, plan.CloseCount);
        Assert.True(plan.UseCandidate);
    }

    [Fact]
    public void Candidate_older_than_the_last_content_becomes_a_repeat()
    {
        long now = BaseTicks + Slot() + 1;
        PullSlotPlan plan = PullCfrScheduler.Plan(
            now, lastClosedSlot: 0, BaseTicks, Fps, MaxBurst,
            hasCandidate: true, candidateCaptureTicks: BaseTicks, lastContentTicks: BaseTicks + Slot());

        Assert.Equal(1, plan.CloseCount);
        Assert.False(plan.UseCandidate);
    }

    [Fact]
    public void Without_a_candidate_slots_are_repeated()
    {
        long now = BaseTicks + Slot() + 1;
        PullSlotPlan plan = PullCfrScheduler.Plan(
            now, lastClosedSlot: 0, BaseTicks, Fps, MaxBurst,
            hasCandidate: false, candidateCaptureTicks: 0, lastContentTicks: BaseTicks);

        Assert.Equal(1, plan.CloseCount);
        Assert.False(plan.UseCandidate);
    }

    /// <summary>
    /// Сквозная проверка правила: содержимое по слотам не может ехать назад.
    /// Пейсер просыпается с разной задержкой, кандидаты приходят вразнобой —
    /// время выбранного кадра в каждом следующем слоте только растёт.
    /// </summary>
    [Fact]
    public void Content_never_travels_backwards_when_pacer_lags()
    {
        long now = BaseTicks;
        long lastClosed = -1;
        long lastContent = BaseTicks;
        long candidateTicks = -1;
        bool hasCandidate = false;
        var content = new List<long>();

        // Рваный график: два провала на 5 слотов среди ровных тиков.
        for (int tick = 0; tick < 600; tick++)
        {
            now += tick is 40 or 80 ? 5 * Slot() : Slot() / 3;

            // Новый кадр захвата появляется не на каждом тике
            if (tick % 2 == 0) { candidateTicks = now - Slot() / 2; hasCandidate = true; }

            PullSlotPlan plan = PullCfrScheduler.Plan(now, lastClosed, BaseTicks, Fps, MaxBurst,
                                                      hasCandidate, candidateTicks, lastContent);
            if (plan.CloseCount == 0) continue;

            for (int i = 0; i < plan.CloseCount; i++)
            {
                bool last = i == plan.CloseCount - 1;
                long chosen = last && plan.UseCandidate ? candidateTicks : lastContent;
                content.Add(chosen);
                if (last && plan.UseCandidate)
                {
                    lastContent = candidateTicks;
                    hasCandidate = false;
                }
            }
            lastClosed += plan.CloseCount;
        }

        Assert.True(content.Count > 100, $"закрыто всего {content.Count} слотов");
        for (int i = 1; i < content.Count; i++)
            Assert.True(content[i] >= content[i - 1],
                $"слот {i}: содержимое уехало назад ({content[i]} < {content[i - 1]})");
    }

    private static long Slot() => PullCfrPolicy.TicksPerSecond / Fps;
}
