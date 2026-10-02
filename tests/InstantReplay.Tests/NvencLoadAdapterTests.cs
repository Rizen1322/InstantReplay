using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class NvencLoadAdapterTests
{
    private const int Fps = 60;
    private const int Window = NvencLoadAdapter.WindowMs;
    private const int PerWindow = Fps * Window / 1000;   // 300 кадров

    /// <summary>Прогон окон: каждое окно сколько подано, закодировано, потеряно и p95 отправки.</summary>
    private sealed class Run(NvencLoadAdapter adapter)
    {
        private long _now, _encoded, _submitted, _dropped;
        public int? Window(int encoded, double p99, int dropped = 0, int submitted = PerWindow,
                           double pendingAgeMs = 20, int queueGrowth = 0)
        {
            _now += NvencLoadAdapter.WindowMs; _encoded += encoded; _submitted += submitted; _dropped += dropped;
            return adapter.Tick(_now, _encoded, _submitted, _dropped, p99, pendingAgeMs, queueGrowth, out _);
        }
        public void Calm(int windows) { for (int i = 0; i < windows; i++) Window(PerWindow, 2); }
    }

    [Fact]
    public void Overload_steps_down_one_level_per_window_and_stops_at_the_bottom()
    {
        var adapter = new NvencLoadAdapter(Fps, baseMultipass: 1, baseAq: true);
        var run = new Run(adapter);
        Assert.Equal(1, run.Window(encoded: 220, p99: 40));
        Assert.Equal((0, true), adapter.Current);
        Assert.Equal(2, run.Window(encoded: 250, p99: 30));
        Assert.Equal((0, false), adapter.Current);
        Assert.Null(run.Window(encoded: 200, p99: 50));        // ниже некуда
        Assert.Equal(2, adapter.Level);
    }

    [Fact]
    public void Stalled_submit_old_frames_or_growing_queue_are_overload()
    {
        // Все кадры закодированы, но вызов отправки подвисает
        Assert.Equal(1, new Run(new NvencLoadAdapter(Fps, 1, true)).Window(encoded: PerWindow, p99: 30));
        // Самый старый кадр ждёт в очереди дольше 100 мс
        Assert.Equal(1, new Run(new NvencLoadAdapter(Fps, 1, true)).Window(PerWindow, 2, pendingAgeMs: 150));
        // Очередь растёт
        Assert.Equal(1, new Run(new NvencLoadAdapter(Fps, 1, true)).Window(PerWindow, 2, queueGrowth: 6));
    }

    [Fact]
    public void Slow_completion_with_b_frames_is_not_overload()
    {
        // Отправка быстрая, всё кодируется, очередь пуста: 17 мс p99 это ещё не повод
        var adapter = new NvencLoadAdapter(Fps, 1, true);
        Assert.Null(new Run(adapter).Window(PerWindow, 17));
        Assert.Equal(0, adapter.Level);
    }

    [Fact]
    public void Starving_capture_is_not_blamed_on_the_encoder()
    {
        var adapter = new NvencLoadAdapter(Fps, 1, true);
        var run = new Run(adapter);
        Assert.Null(run.Window(encoded: 150, p99: 40, submitted: 150));
        Assert.Equal(0, adapter.Level);
    }

    [Theory]
    [InlineData(1100, 0, 0)]
    [InlineData(20, 6, 0)]
    [InlineData(20, 0, 15)]
    public void Backpressure_still_reduces_load_when_it_has_already_starved_submission(
        double pendingAgeMs, int queueGrowth, int dropped)
    {
        var adapter = new NvencLoadAdapter(Fps, 0, true);
        Assert.Equal(1, new Run(adapter).Window(150, 2, submitted: 150,
            pendingAgeMs: pendingAgeMs, queueGrowth: queueGrowth, dropped: dropped));
        Assert.Equal((0, false), adapter.Current);
    }

    [Fact]
    public void Low_frame_delivery_does_not_count_as_a_calm_minute_for_quality_recovery()
    {
        var adapter = new NvencLoadAdapter(Fps, 0, true);
        var run = new Run(adapter);
        run.Window(200, 40);
        for (int i = 0; i < 24; i++)
            Assert.Null(run.Window(150, 2, submitted: 150));
        Assert.Equal(1, adapter.Level);
        run.Calm(11);
        Assert.Equal(1, adapter.Level);
        Assert.Equal(0, run.Window(PerWindow, 2));
    }

    [Fact]
    public void Delayed_poll_uses_actual_elapsed_time_instead_of_mistaking_30fps_for_60fps()
    {
        var adapter = new NvencLoadAdapter(Fps, 0, true);
        Assert.Equal(1, adapter.Tick(5000, 200, 300, 0, 40, 20, 0, out _));
        for (int i = 1; i <= 12; i++)
            Assert.Null(adapter.Tick(5000 + i * 10000, 200 + i * 300, 300 + i * 300,
                0, 2, 20, 0, out _));
        Assert.Equal(1, adapter.Level);
    }

    [Fact]
    public void Recovers_one_level_after_a_calm_minute()
    {
        var adapter = new NvencLoadAdapter(Fps, 1, true);
        var run = new Run(adapter);
        run.Window(200, 40);
        run.Window(200, 40);
        Assert.Equal(2, adapter.Level);
        run.Calm(11);
        Assert.Equal(2, adapter.Level);
        Assert.Equal(1, run.Window(PerWindow, 2));
        run.Calm(11);
        Assert.Equal(0, run.Window(PerWindow, 2));
    }

    [Fact]
    public void Relapse_right_after_recovery_doubles_the_wait()
    {
        var adapter = new NvencLoadAdapter(Fps, 1, false);
        var run = new Run(adapter);
        run.Window(200, 40);                 // ступень 1
        run.Calm(12);                        // вернулись на 0
        Assert.Equal(0, adapter.Level);
        run.Window(200, 40);                 // та же нагрузка сразу: снова 1
        Assert.Equal(1, adapter.Level);
        run.Calm(12);
        Assert.Equal(1, adapter.Level);      // минуты уже мало
        run.Calm(12);
        Assert.Equal(0, adapter.Level);      // через две
    }

    [Fact]
    public void Nothing_to_adapt_without_multipass_and_aq()
    {
        var adapter = new NvencLoadAdapter(Fps, 0, false);
        Assert.False(adapter.CanAdapt);
        Assert.Null(new Run(adapter).Window(100, 80));
    }

    [Fact]
    public void Latency_summary_percentiles()
    {
        var stats = new LatencyStats();
        for (int i = 1; i <= 100; i++) stats.Add(i);
        var summary = stats.Take();
        Assert.Equal(100, summary.Count);
        Assert.Equal(50, summary.P50);
        Assert.Equal(95, summary.P95);
        Assert.Equal(99, summary.P99);
        Assert.Equal(100, summary.Max);
        Assert.Equal(0, stats.Take().Count);                  // окно начинается заново
    }

    [Fact]
    public void Freeze_gaps_between_real_frames_are_counted_with_cause()
    {
        var f = new FreezeStats();
        const long ms = 10_000;
        double frame = 1000.0 / 60;
        f.Add(0, false, 0, frame);
        f.Add(17 * ms, false, 0, frame);
        f.Add(33 * ms, true, 0, frame);            // дубль не считается настоящим
        f.Add(117 * ms, false, 0, frame);          // 100 мс без новых кадров: захват
        f.Add(417 * ms, false, 3, frame);          // 300 мс и потеряно 3 кадра: конвейер
        var minute = f.TakeAndReset();
        Assert.Equal(2, minute.Over50);
        Assert.Equal(1, minute.Over100);           // 100 мс ровно не больше 100
        Assert.Equal(1, minute.Over250);
        Assert.Equal(1, minute.PipelineFreezes);
        Assert.Equal(300, minute.LongestMs);
        Assert.Equal("конвейер", minute.LongestCause);
        Assert.Equal(4, minute.RealFrames);
        Assert.Equal(1, minute.Duplicates);
        Assert.Equal(0, f.TakeAndReset().Frames);
    }

    [Fact]
    public void Session_histogram_percentiles()
    {
        var h = new LatencyHistogram();
        for (int i = 1; i <= 1000; i++) h.Add(i / 10.0);   // 0.1..100 мс
        var s = h.Summary();
        Assert.InRange(s.P50, 49.5, 50.5);
        Assert.InRange(s.P99, 98.5, 99.5);
        Assert.Equal(100, s.Max);
    }
}
