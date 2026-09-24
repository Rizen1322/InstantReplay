using Aura.Core.Audio;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Шкала звука на часах QPC: пакеты ложатся туда, где звук реально прозвучал, дрейф
/// часов устройства убирается плавной поправкой темпа, а молчание устройства —
/// честная тишина на своём месте.
/// </summary>
public class AudioTimelineTests
{
    private const long Second = 10_000_000;

    /// <summary>Подать звук с часами устройства, идущими на <paramref name="ppm"/> быстрее.</summary>
    private static void Feed(AudioTimeline line, double seconds, double ppm, int rate = 48000,
                             Func<long, float>? signal = null, long startTicks = 0)
    {
        const int packet = 480;
        var buffer = new float[packet * line.Channels];
        long produced = 0;
        double deviceRate = rate * (1 + ppm / 1e6);
        while (produced < seconds * rate)
        {
            for (int f = 0; f < packet; f++)
                for (int c = 0; c < line.Channels; c++)
                    buffer[f * line.Channels + c] = signal?.Invoke(produced + f) ?? 0.25f;
            // Метка времени пакета — по часам системы: устройство с быстрыми часами
            // выдаёт свои N сэмплов за меньшее системное время.
            long qpc = startTicks + (long)(produced / deviceRate * Second);
            line.Push(buffer, packet, rate, qpc, discontinuity: false);
            produced += packet;
        }
    }

    [Fact]
    public void Drifting_device_clock_is_absorbed_without_gaps()
    {
        var line = new AudioTimeline(0, 2);
        Feed(line, seconds: 30, ppm: 150);

        Assert.Equal(0, line.Resyncs);
        // Поправка темпа нашла дрейф часов: ~150 ppm
        Assert.InRange(line.Correction * 1e6, 100, 220);
        // Позиция записи идёт по системному времени, а не по числу сэмплов устройства
        long expected = (long)(30 * 48000 / (1 + 150e-6));
        Assert.InRange(line.WritePosition, expected - 480 * 2, expected + 480 * 2);
    }

    [Fact]
    public void Silence_from_device_becomes_silence_at_its_place()
    {
        var line = new AudioTimeline(0, 1);
        Feed(line, seconds: 1, ppm: 0);
        // Устройство молчало две секунды (loopback без звука пакетов не шлёт)
        Feed(line, seconds: 1, ppm: 0, startTicks: 3 * Second);

        var block = new float[480];
        line.Read(2 * 48000, block, 480);                   // внутри паузы
        Assert.All(block, v => Assert.Equal(0f, v));
        line.Read(3 * 48000 + 4800, block, 480);            // после паузы — снова звук
        Assert.All(block, v => Assert.InRange(v, 0.24f, 0.26f));
    }

    [Fact]
    public void Resampler_keeps_level_and_frequency()
    {
        // 44.1 кГц → 48 кГц: синус 1 кГц сохраняет амплитуду и число переходов через ноль
        var line = new AudioTimeline(0, 1);
        Feed(line, seconds: 2, ppm: 0, rate: 44100,
             signal: n => 0.5f * MathF.Sin(2 * MathF.PI * 1000 * n / 44100f));

        var out48 = new float[48000];
        line.Read(24000, out48, 48000);   // вторая половина — без переходного процесса в начале
        float peak = out48.Max(MathF.Abs);
        Assert.InRange(peak, 0.47f, 0.53f);
        int crossings = 0;
        for (int i = 1; i < out48.Length; i++) if (out48[i - 1] < 0 && out48[i] >= 0) crossings++;
        Assert.InRange(crossings, 995, 1005);
    }

    [Fact]
    public void Limiter_holds_peaks_under_full_scale()
    {
        var limiter = new Limiter(2);
        var block = new float[480 * 2];
        float maxOut = 0;
        for (int b = 0; b < 200; b++)
        {
            for (int i = 0; i < 480; i++)
            {
                float v = 1.8f * MathF.Sin(2 * MathF.PI * 200 * (b * 480 + i) / 48000f);
                block[2 * i] = block[2 * i + 1] = v;
            }
            limiter.Process(block, 480);
            if (b > 2) maxOut = Math.Max(maxOut, block.Max(MathF.Abs));
        }
        Assert.InRange(maxOut, 0.9f, 1.0f);
        Assert.True(limiter.MinGain < 0.6f);
    }
}
