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
        Assert.InRange(maxOut, 0.85f, 0.9f);   // потолок −1 дБFS (0.891) под AAC
        Assert.True(limiter.MinGain < 0.6f);
    }

    [Fact]
    public void Push_to_talk_opens_by_audio_time_and_holds_after_release()
    {
        var gate = new PushToTalkGate();
        const long s = 10_000_000;
        gate.Set(true, 1 * s);
        gate.Set(false, 2 * s);

        Assert.False(gate.IsOpenAt(s - 1));            // до нажатия
        Assert.True(gate.IsOpenAt(s + s / 2));         // зажата
        Assert.True(gate.IsOpenAt(2 * s + s / 10));    // 100 мс после отпускания: ещё открыт
        Assert.False(gate.IsOpenAt(2 * s + s / 4));    // 250 мс: закрыт
    }

    [Fact]
    public void Push_to_talk_ignores_repeated_state_and_keeps_old_press()
    {
        var gate = new PushToTalkGate();
        const long s = 10_000_000;
        gate.Set(true, 1 * s);
        gate.Set(true, 3 * s);                         // автоповтор клавиши
        Assert.True(gate.IsOpenAt(2 * s));
        // Долгое удержание: старое нажатие не выбрасывается, пока нет отпускания
        gate.Set(false, 60 * s);
        Assert.True(gate.IsOpenAt(59 * s));
        Assert.False(gate.IsOpenAt(61 * s));
    }

    [Fact]
    public void Short_push_to_talk_tap_holds_continuously_without_reopening()
    {
        // Нажатие 100 мс, отпускание 150 мс: открыт непрерывно до 350 мс, потом закрыт
        var gate = new PushToTalkGate();
        const long ms = 10_000;
        gate.Set(true, 100 * ms);
        gate.Set(false, 150 * ms);
        foreach (long t in new long[] { 100, 149, 175, 225, 300, 349 })
            Assert.True(gate.IsOpenAt(t * ms), $"{t} мс");
        foreach (long t in new long[] { 99, 350, 400, 500 })
            Assert.False(gate.IsOpenAt(t * ms), $"{t} мс");
    }

    [Fact]
    public void Overlapping_push_to_talk_taps_keep_one_tail()
    {
        var gate = new PushToTalkGate();
        const long ms = 10_000;
        gate.Set(true, 100 * ms);
        gate.Set(false, 150 * ms);
        gate.Set(true, 250 * ms);     // второе нажатие внутри хвоста первого
        gate.Set(false, 260 * ms);
        foreach (long t in new long[] { 200, 255, 300, 459 })
            Assert.True(gate.IsOpenAt(t * ms), $"{t} мс");
        Assert.False(gate.IsOpenAt(460 * ms));
    }

    [Fact]
    public void Release_without_press_does_not_open()
    {
        var gate = new PushToTalkGate();
        gate.Set(false, 10_000_000);
        Assert.False(gate.IsOpenAt(10_000_000 + 50_000));
    }

    [Fact]
    public void Push_to_talk_orders_events_by_time_not_arrival()
    {
        var gate = new PushToTalkGate();
        const long s = 10_000_000;
        gate.Set(false, 2 * s);                        // отпускание пришло раньше нажатия
        gate.Set(true, 1 * s);
        Assert.True(gate.IsOpenAt(s + s / 2));
        Assert.False(gate.IsOpenAt(3 * s));
    }

    [Fact]
    public void Timestamp_guard_keeps_normal_qpc_untouched()
    {
        var guard = new WasapiTimestampGuard();
        long now = 100 * Second;
        const long packetTicks = 100_000; // 10 мс

        for (int i = 0; i < 10; i++)
        {
            long rawQpc = now - packetTicks;
            var resolved = guard.Resolve(rawQpc, badTime: false, discontinuity: false, packetTicks, now);
            Assert.Equal(rawQpc, resolved.QpcTicks);
            Assert.False(resolved.Rebased);
            Assert.False(resolved.Discontinuity);
            Assert.Equal(0, guard.OffsetTicks);
            now += packetTicks;
        }
        Assert.Equal(0, guard.Rebases);
    }

    [Fact]
    public void Timestamp_guard_ignores_single_outlier_without_rebasing_or_corrupting_timeline()
    {
        var guard = new WasapiTimestampGuard();
        var line = new AudioTimeline(0, 2);
        const int packetFrames = 480;
        const long packetTicks = 100_000;
        var samples = new float[packetFrames * 2];
        Array.Fill(samples, 0.25f);

        long now = 10 * Second;
        for (int i = 0; i < 20; i++)
        {
            var r = guard.Resolve(now - packetTicks, badTime: false, discontinuity: false, packetTicks, now);
            line.Push(samples, packetFrames, 48000, r.QpcTicks, r.Discontinuity);
            now += packetTicks;
        }

        // Одиночный выброс на -60 секунд не должен сдвигать шкалу или ломать чтение
        var outlier = guard.Resolve(now - 60 * Second, badTime: false, discontinuity: false, packetTicks, now);
        Assert.False(outlier.Rebased);
        Assert.Equal(0, guard.OffsetTicks);
        Assert.InRange(outlier.QpcTicks, now - 5 * packetTicks, now + packetTicks);
        line.Push(samples, packetFrames, 48000, outlier.QpcTicks, outlier.Discontinuity);

        now += packetTicks;
        var back = guard.Resolve(now - packetTicks, badTime: false, discontinuity: false, packetTicks, now);
        Assert.False(back.Rebased);
        Assert.Equal(0, guard.OffsetTicks);
        line.Push(samples, packetFrames, 48000, back.QpcTicks, back.Discontinuity);

        long readPos = ((now - 2_000_000L) * 48000) / Second;
        var outBlock = new float[packetFrames * 2];
        line.Read(readPos, outBlock, packetFrames);
        Assert.Contains(outBlock, v => MathF.Abs(v) > 0.2f);
        Assert.Equal((0, 0), line.TakeReadMisses());
    }

    [Theory]
    [InlineData(-500_0000L)]     // -500 мс (меньше окна микшера 200 мс)
    [InlineData(-60 * Second)]  // -60 с
    [InlineData(10 * Second)]   // +10 с в будущее (может вытеснить кольцо без защиты)
    public void Timestamp_guard_rebases_persistent_qpc_shift_and_keeps_audio_readable(long shiftTicks)
    {
        var guard = new WasapiTimestampGuard();
        var line = new AudioTimeline(0, 2);
        const int packetFrames = 480;
        const long packetTicks = 100_000;
        var samples = new float[packetFrames * 2];
        Array.Fill(samples, 0.3f);

        long now = 70 * Second;
        for (int i = 0; i < 15; i++)
        {
            var r = guard.Resolve(now - packetTicks, badTime: false, discontinuity: false, packetTicks, now);
            line.Push(samples, packetFrames, 48000, r.QpcTicks, r.Discontinuity);
            now += packetTicks;
        }

        bool sawRebase = false;
        // После сбоя драйвер начинает отдавать QPC со сдвигом shiftTicks и дрейфом -22 ppm
        for (int i = 0; i < 40; i++)
        {
            long rawQpc = now - packetTicks + shiftTicks - (long)(i * packetTicks * 22e-6);
            var r = guard.Resolve(rawQpc, badTime: false, discontinuity: false, packetTicks, now);
            if (r.Rebased)
            {
                sawRebase = true;
                Assert.True(r.Discontinuity);
            }
            line.Push(samples, packetFrames, 48000, r.QpcTicks, r.Discontinuity);
            now += packetTicks;
        }

        Assert.True(sawRebase);
        Assert.Equal(1, guard.Rebases);

        long readPos = ((now - packetTicks - 2_000_000L) * 48000) / Second;
        var outBlock = new float[packetFrames * 2];
        line.Read(readPos, outBlock, packetFrames);
        Assert.All(outBlock, v => Assert.InRange(v, 0.25f, 0.35f));
        Assert.Equal((0, 0), line.TakeReadMisses());
    }

    [Fact]
    public void Timestamp_guard_returns_offset_to_zero_when_raw_qpc_recovers()
    {
        var guard = new WasapiTimestampGuard();
        const long packetTicks = 100_000;
        long now = 20 * Second;

        for (int i = 0; i < WasapiTimestampGuard.RebaseStreak; i++)
        {
            guard.Resolve(now - packetTicks - 2 * Second, badTime: false, discontinuity: false, packetTicks, now);
            now += packetTicks;
        }
        Assert.NotEqual(0, guard.OffsetTicks);
        Assert.Equal(1, guard.Rebases);

        bool returned = false;
        for (int i = 0; i < WasapiTimestampGuard.RebaseStreak; i++)
        {
            var r = guard.Resolve(now - packetTicks, badTime: false, discontinuity: false, packetTicks, now);
            if (r.Rebased) returned = true;
            now += packetTicks;
        }
        Assert.True(returned);
        Assert.Equal(0, guard.OffsetTicks);
        Assert.Equal(2, guard.Rebases);
    }

    [Fact]
    public void Timestamp_guard_synthesizes_smooth_qpc_when_bad_or_zero()
    {
        var guard = new WasapiTimestampGuard();
        const long packetTicks = 100_000;
        long now = 10 * Second;

        var first = guard.Resolve(0, badTime: true, discontinuity: false, packetTicks, now);
        var second = guard.Resolve(0, badTime: false, discontinuity: false, packetTicks, now + 30_000);
        Assert.Equal(first.QpcTicks + packetTicks, second.QpcTicks);
        Assert.False(first.Rebased);
        Assert.False(second.Rebased);
    }

    [Fact]
    public void Loopback_silence_watchdog_triggers_only_after_five_audible_silent_seconds()
    {
        var watchdog = new LoopbackSilenceWatchdog();

        for (int i = 0; i < LoopbackSilenceWatchdog.TriggerSeconds - 1; i++)
            Assert.False(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.3f, endpointMuted: false));

        // Сбрасывается при появлении захваченного звука
        Assert.False(watchdog.ObserveSecond(capturedPeak: 0.05f, endpointPeak: 0.3f, endpointMuted: false));

        // Сбрасывается при тихом или замьюченном устройстве
        for (int i = 0; i < 4; i++)
            Assert.False(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.3f, endpointMuted: false));
        Assert.False(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.001f, endpointMuted: false));

        for (int i = 0; i < 4; i++)
            Assert.False(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.3f, endpointMuted: false));
        Assert.False(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.3f, endpointMuted: true));

        // 5 секунд подряд: звук играет, не замьючен, захват пустой -> срабатывает
        for (int i = 0; i < LoopbackSilenceWatchdog.TriggerSeconds - 1; i++)
            Assert.False(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.3f, endpointMuted: false));
        Assert.True(watchdog.ObserveSecond(capturedPeak: 0f, endpointPeak: 0.3f, endpointMuted: false));
    }

    [Fact]
    public void Silent_loopback_recovery_policy_throttles_and_resets_when_audible()
    {
        var policy = new SilentLoopbackRecoveryPolicy();
        long t = 100_000;

        Assert.True(policy.TryBeginRestart(t, out bool exhausted));
        Assert.False(exhausted);

        // Внутри паузы 15 с повтор не разрешён
        Assert.False(policy.TryBeginRestart(t + 5_000, out exhausted));
        Assert.False(exhausted);

        Assert.True(policy.TryBeginRestart(t + 15_000, out exhausted));
        Assert.False(exhausted);

        Assert.True(policy.TryBeginRestart(t + 15_000 + 60_000, out exhausted));
        Assert.False(exhausted);

        // После 3 попыток с перерывом 300 с бюджет исчерпан
        Assert.False(policy.TryBeginRestart(t + 15_000 + 60_000 + 300_000, out exhausted));
        Assert.True(exhausted);

        // Появился реальный звук — счётчик сбрасывается
        policy.NoteAudible();
        Assert.True(policy.TryBeginRestart(t + 500_000, out exhausted));
        Assert.False(exhausted);
    }

    [Fact]
    public void Format_property_filter_matches_only_device_format_and_speakers()
    {
        var deviceFormat = new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
        var physicalSpeakers = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");

        Assert.True(AudioCaptureHealth.IsFormatProperty(deviceFormat, 0));
        Assert.False(AudioCaptureHealth.IsFormatProperty(deviceFormat, 1));
        Assert.True(AudioCaptureHealth.IsFormatProperty(physicalSpeakers, 3));
        Assert.False(AudioCaptureHealth.IsFormatProperty(physicalSpeakers, 0));
        Assert.False(AudioCaptureHealth.IsFormatProperty(Guid.NewGuid(), 0));
    }

    [Fact]
    public void Audio_timeline_tracks_late_and_evicted_read_misses()
    {
        var line = new AudioTimeline(0, 2);
        const int frames = 480;
        var samples = new float[frames * 2];
        Array.Fill(samples, 0.2f);

        line.Push(samples, frames, 48000, 10 * Second, discontinuity: false);
        var buf = new float[frames * 2];

        // Читаем позже _writePos (позднее чтение)
        line.Read(11 * 48000, buf, frames);
        // Читаем раньше CapacityFrames (вытесненное чтение)
        line.Read(1 * 48000, buf, frames);
        // Нормальное чтение в окне
        line.Read(10 * 48000, buf, frames);

        Assert.Equal((1, 1), line.TakeReadMisses());
        Assert.Equal((0, 0), line.TakeReadMisses());
    }
}
