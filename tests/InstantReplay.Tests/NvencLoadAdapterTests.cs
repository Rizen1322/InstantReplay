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
        public int? Window(int encoded, double p95, int dropped = 0, int submitted = PerWindow)
        {
            _now += NvencLoadAdapter.WindowMs; _encoded += encoded; _submitted += submitted; _dropped += dropped;
            return adapter.Tick(_now, _encoded, _submitted, _dropped, p95, out _);
        }
        public void Calm(int windows) { for (int i = 0; i < windows; i++) Window(PerWindow, 2); }
    }

    [Fact]
    public void Overload_steps_down_one_level_per_window_and_stops_at_the_bottom()
    {
        var adapter = new NvencLoadAdapter(Fps, baseMultipass: 1, baseAq: true);
        var run = new Run(adapter);
        Assert.Equal(1, run.Window(encoded: 220, p95: 40));
        Assert.Equal((0, true), adapter.Current);
        Assert.Equal(2, run.Window(encoded: 250, p95: 30));
        Assert.Equal((0, false), adapter.Current);
        Assert.Null(run.Window(encoded: 200, p95: 50));        // ниже некуда
        Assert.Equal(2, adapter.Level);
    }

    [Fact]
    public void Slow_submit_alone_is_overload()
    {
        var adapter = new NvencLoadAdapter(Fps, 1, true);
        var run = new Run(adapter);
        // Все кадры закодированы, но отправка дольше целого кадра: очередь на подходе
        Assert.Equal(1, run.Window(encoded: PerWindow, p95: 25));
    }

    [Fact]
    public void Starving_capture_is_not_blamed_on_the_encoder()
    {
        var adapter = new NvencLoadAdapter(Fps, 1, true);
        var run = new Run(adapter);
        Assert.Null(run.Window(encoded: 150, p95: 40, submitted: 150));
        Assert.Equal(0, adapter.Level);
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
}
