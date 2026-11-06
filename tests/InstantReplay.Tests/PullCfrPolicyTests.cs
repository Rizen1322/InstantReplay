using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Сетка выходных слотов pull-модели. Главный инвариант — сетка обратна самой себе:
/// <c>SlotIndex(SlotPts(k)) == k</c>. На нём держится всё дозаполнение; в push-сетке
/// с делением вниз он не выполняется, и «догнать слот» превращается в вечный цикл.
/// </summary>
public sealed class PullCfrPolicyTests
{
    [Theory]
    [InlineData(24)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(72)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void Grid_is_its_own_inverse(int fps)
    {
        const long baseTicks = 1_234_567;

        for (long slot = 0; slot < 20_000; slot++)
        {
            long pts = PullCfrPolicy.SlotPts(slot, baseTicks, fps);
            Assert.Equal(slot, PullCfrPolicy.SlotIndex(pts, baseTicks, fps));

            // Тик перед границей принадлежит предыдущему слоту: границы не спорят.
            if (slot > 0)
                Assert.Equal(slot - 1, PullCfrPolicy.SlotIndex(pts - 1, baseTicks, fps));
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void Slot_times_grow_and_do_not_drift(int fps)
    {
        for (long slot = 0; slot < 100_000; slot++)
        {
            long current = PullCfrPolicy.SlotPts(slot, 0, fps);
            long next = PullCfrPolicy.SlotPts(slot + 1, 0, fps);
            Assert.True(next > current, $"слот {slot + 1} не позже слота {slot} при {fps} fps");

            // Ошибка не копится: разница с честным временем не больше тика.
            long ideal = (slot + 1) * PullCfrPolicy.TicksPerSecond / fps;
            Assert.InRange(next - ideal, 0, 1);
        }
    }

    [Fact]
    public void Due_slots_advance_one_by_one_and_never_repeat()
    {
        const int fps = 60;
        const long baseTicks = 5_000_000;
        long lastClosed = -1;
        long step = PullCfrPolicy.TicksPerSecond / fps / 7;

        for (long now = baseTicks; now < baseTicks + 2 * PullCfrPolicy.TicksPerSecond; now += step)
        {
            int due = PullCfrPolicy.DueCount(now, lastClosed, baseTicks, fps, maxBurst: 16);
            long current = PullCfrPolicy.SlotIndex(now, baseTicks, fps);

            Assert.InRange(due, 0, 1);
            Assert.Equal(current, lastClosed + due);
            lastClosed += due;
        }
    }

    [Fact]
    public void Long_pause_is_capped_but_never_stalls()
    {
        const int fps = 60;
        const long baseTicks = 1_000;

        Assert.Equal(0, PullCfrPolicy.DueCount(baseTicks, 0, baseTicks, fps, maxBurst: 4));

        // Секунда простоя — это 60 слотов, а за один тик закрываем не больше четырёх.
        Assert.Equal(4, PullCfrPolicy.DueCount(baseTicks + PullCfrPolicy.TicksPerSecond, 0,
                                               baseTicks, fps, maxBurst: 4));

        // И после потолка цикл всё равно движется: закрытые слоты учитываются.
        Assert.Equal(4, PullCfrPolicy.DueCount(baseTicks + PullCfrPolicy.TicksPerSecond, 4,
                                               baseTicks, fps, maxBurst: 4));
    }

    /// <summary>
    /// Планировщик не добавляет своего джиттера: при захвате на каждом кадре экрана
    /// шаг содержимого между слотами не превышает периода экрана. А нынешнее
    /// полуслотное ограничение захвата (см. EncoderCfrPolicy.CaptureMinIntervalTicks)
    /// его превышает — то есть одной pull-модели мало, захват тоже должен успевать.
    ///
    /// Частоты, кратные записи (60, 120, 240), сюда не входят намеренно: на них
    /// принятая сетка совпадает с сеткой слотов, и ограничение захвата ничего не
    /// портит — это видно и по замерам push-модели.
    /// </summary>
    [Theory]
    [InlineData(144)]
    [InlineData(165)]
    public void Pull_smooths_content_only_when_capture_is_denser_than_the_grid(int hz)
    {
        double presentMs = 1000.0 / hz;
        Simulation everyPresent = Run(hz, captureGate: false);
        Simulation gated = Run(hz, captureGate: true);

        Assert.Equal(everyPresent.Slots, everyPresent.Content.Count);
        Assert.All(everyPresent.Steps, step => Assert.True(step > 0));

        Assert.True(everyPresent.MaxDeviationMs <= presentMs + 0.05,
            $"захват каждого кадра: отклонение {everyPresent.MaxDeviationMs:F2} мс " +
            $"больше периода экрана {presentMs:F2} мс");

        Assert.True(gated.MaxDeviationMs > presentMs,
            $"полуслотное ограничение: отклонение {gated.MaxDeviationMs:F2} мс " +
            $"не больше периода экрана {presentMs:F2} мс");
    }

    private sealed record Simulation(int Slots, List<double> Content, List<double> Steps, double MaxDeviationMs);

    /// <summary>
    /// Модель pull-модели: сетка идёт по своим часам, в каждый слот кладётся самый
    /// свежий из уже пришедших кадров экрана. Захват — с ограничением как в коде или
    /// без него.
    /// </summary>
    private static Simulation Run(int hz, bool captureGate)
    {
        const int fps = 60;
        const int slots = 600;                     // 10 секунд выходного видео
        const long baseTicks = 10_000_000;          // первый кадр экрана = начало сетки
        double presentInterval = PullCfrPolicy.TicksPerSecond / (double)hz;

        var content = new List<double>(slots);
        long lastAccepted = -1;
        long latestContent = baseTicks;
        long presentIndex = 0;

        for (long slot = 0; slot < slots; slot++)
        {
            long until = PullCfrPolicy.SlotPts(slot, baseTicks, fps);
            while (true)
            {
                long present = baseTicks + (long)Math.Round(presentIndex * presentInterval);
                if (present > until) break;

                bool admitted = !captureGate ||
                                !EncoderCfrPolicy.CaptureTooSoon(present, lastAccepted, fps);
                if (admitted)
                {
                    latestContent = present;
                    lastAccepted = present;
                }
                presentIndex++;
            }
            content.Add((latestContent - baseTicks) / 10_000.0);
        }

        double nominalMs = 1000.0 / fps;
        var steps = new List<double>(slots - 1);
        double maxDeviation = 0;
        for (int i = 1; i < content.Count; i++)
        {
            double step = content[i] - content[i - 1];
            steps.Add(step);
            maxDeviation = Math.Max(maxDeviation, Math.Abs(step - nominalMs));
        }

        return new Simulation(slots, content, steps, maxDeviation);
    }
}
