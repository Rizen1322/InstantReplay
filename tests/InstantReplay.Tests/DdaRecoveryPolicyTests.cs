using Aura.Core.Capture;
using Aura.Core.Engine;
using Xunit;

namespace InstantReplay.Tests;

public sealed class DdaRecoveryPolicyTests
{
    // Частота по умолчанию у DdaProgressWatchdog — TimeSpan.TicksPerSecond.
    private const long Freq = TimeSpan.TicksPerSecond;

    private static DdaProgressSample Frame(
        long loopSequence,
        long lastLoopProgressQpc,
        DdaCapturePhase phase = DdaCapturePhase.Acquire,
        long generation = 1) =>
        new(loopSequence, lastLoopProgressQpc, lastLoopProgressQpc, phase, generation);

    [Fact]
    public void WaitTimeoutWithoutFramesStillCountsAsProgress()
    {
        var watchdog = new DdaProgressWatchdog(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5));
        long now = 100 * Freq;
        var verdict = watchdog.Observe(Frame(0, now, DdaCapturePhase.Idle), now);
        Assert.Equal(DdaProgressVerdict.NoData, verdict);

        // Статичный экран: кадров нет вообще, но цикл каждую секунду возвращается
        // из AcquireNextFrame с WAIT_TIMEOUT — это не зависание.
        for (int second = 1; second <= 20; second++)
        {
            now += Freq;
            verdict = watchdog.Observe(Frame(second, now, DdaCapturePhase.Idle), now);
            Assert.Equal(DdaProgressVerdict.Progressing, verdict);
        }
    }

    [Fact]
    public void ShortIdleSleepAtFpsLimitIsNotAHang()
    {
        var watchdog = new DdaProgressWatchdog(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5));
        long now = 100 * Freq;
        watchdog.Observe(Frame(0, now, DdaCapturePhase.Idle), now);

        // Ограничение FPS: пауза 16 мс в Idle между итерациями.
        for (int i = 1; i <= 60; i++)
        {
            now += 160_000; // 16 мс
            var verdict = watchdog.Observe(Frame(i, now, DdaCapturePhase.Idle), now);
            Assert.NotEqual(DdaProgressVerdict.Suspect, verdict);
            Assert.NotEqual(DdaProgressVerdict.Confirmed, verdict);
        }
    }

    [Fact]
    public void StalledLoopRequiresSeveralConfirmations()
    {
        var watchdog = new DdaProgressWatchdog(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5));
        long started = 100 * Freq;
        Assert.Equal(DdaProgressVerdict.NoData,
            watchdog.Observe(Frame(1, started), started));

        // Сторож тикает раз в секунду. Первые секунды без прогресса — порог не
        // пройден, поток не считается зависшим.
        long now = started;
        for (int second = 1; second <= 4; second++)
        {
            now = started + second * Freq;
            Assert.Equal(DdaProgressVerdict.Progressing,
                watchdog.Observe(Frame(1, started), now));
        }

        // 5 с без прогресса — подозрение, но одного наблюдения мало.
        now = started + 5 * Freq;
        Assert.Equal(DdaProgressVerdict.Suspect,
            watchdog.Observe(Frame(1, started), now));

        // Второе подтверждение подряд — зависание установлено.
        now = started + 6 * Freq;
        Assert.Equal(DdaProgressVerdict.Confirmed,
            watchdog.Observe(Frame(1, started), now));
    }

    [Fact]
    public void LateWatchdogWakeAfterGlobalFreezeDoesNotConfirmByItself()
    {
        var watchdog = new DdaProgressWatchdog(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5));
        long started = 100 * Freq;
        watchdog.Observe(Frame(1, started), started);

        // Весь процесс стоял 20 с. Метка времени устарела, но это не доказательство
        // зависания потока: первое позднее наблюдение сбрасывает подтверждения.
        long now = started + 20 * Freq;
        Assert.Equal(DdaProgressVerdict.Frozen,
            watchdog.Observe(Frame(1, started), now));

        // Затем обычные наблюдения: только они могут подтвердить зависание.
        now += Freq;
        Assert.Equal(DdaProgressVerdict.Suspect,
            watchdog.Observe(Frame(1, started), now));
        now += Freq;
        Assert.Equal(DdaProgressVerdict.Confirmed,
            watchdog.Observe(Frame(1, started), now));
    }

    [Fact]
    public void NewGenerationRestartsObservation()
    {
        var watchdog = new DdaProgressWatchdog(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2.5));
        long now = 100 * Freq;
        watchdog.Observe(Frame(5, now, generation: 1), now);

        // Долгая пауза между наблюдениями (процесс стоял) не подтверждает зависание.
        now += 6 * Freq;
        Assert.Equal(DdaProgressVerdict.Frozen,
            watchdog.Observe(Frame(5, now - 6 * Freq, generation: 1), now));

        // Новый конвейер: прежние наблюдения не должны к нему применяться.
        Assert.Equal(DdaProgressVerdict.NoData,
            watchdog.Observe(Frame(0, now, generation: 2), now));
    }

    [Fact]
    public void HeldOutputIsReportedOnceAndKeepsItsFirstOwner()
    {
        HeldCaptureOutputs.ResetForTests();
        try
        {
            Assert.False(HeldCaptureOutputs.IsHeld(@"\\?\DISPLAY#MONITOR"));

            Assert.True(HeldCaptureOutputs.TryHold(
                @"\\?\DISPLAY#MONITOR", generation: 7, "остановка не уложилась", "фаза ReleaseFrame"));
            // Сравнение имён выходов не должно зависеть от регистра.
            Assert.True(HeldCaptureOutputs.IsHeld(@"\\?\display#monitor", out var held));
            Assert.Equal(7, held.Generation);

            // Более новая генерация не перетирает настоящую причину блокировки.
            Assert.False(HeldCaptureOutputs.TryHold(
                @"\\?\DISPLAY#MONITOR", generation: 8, "повторная пометка"));
            Assert.True(HeldCaptureOutputs.IsHeld(@"\\?\DISPLAY#MONITOR", out held));
            Assert.Equal(7, held.Generation);
            Assert.Contains("не уложилась", held.Reason);
        }
        finally
        {
            HeldCaptureOutputs.ResetForTests();
        }
    }

    [Fact]
    public void HeldOutputWithoutIdentityCannotBeMarked()
    {
        HeldCaptureOutputs.ResetForTests();
        try
        {
            Assert.False(HeldCaptureOutputs.TryHold("", 1, "нет идентичности"));
            Assert.False(HeldCaptureOutputs.TryHold(null, 1, "нет идентичности"));
            Assert.Empty(HeldCaptureOutputs.Snapshot());
        }
        finally
        {
            HeldCaptureOutputs.ResetForTests();
        }
    }

    [Fact]
    public void HeldOutputExceptionTellsUserToSaveReplayAndRestart()
    {
        var held = new HeldCaptureOutput(
            @"\\?\DISPLAY#MONITOR",
            5,
            "остановка захвата не завершилась",
            DateTimeOffset.UnixEpoch,
            "DDA: фаза ReleaseFrame");
        var error = new CaptureOutputHeldException(held);

        Assert.Contains("сохранить", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("перезапустите Aura", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, error.Output.Generation);
    }

    [Fact]
    public void TerminalCaptureBlockKeepsReplayAndAudioBuffersWithoutSaving()
    {
        var actions = PipelineRestartPolicy.For(
            PipelineStopIntent.CaptureBlocked,
            continuousRecordingActive: true,
            formatCompatible: false);

        Assert.True(actions.KeepReplayBuffer);
        Assert.True(actions.KeepAudioBuffer);
        Assert.False(actions.SaveReplay);
        Assert.False(actions.ResumeContinuousRecording);
    }

    [Fact]
    public void DiagnosticsSnapshotReportsPhaseAndProgressInWords()
    {
        long now = 15 * Freq;
        var snapshot = new CaptureDiagnosticsSnapshot(
            "DesktopDuplication",
            DdaCapturePhase.FrameArrived,
            PhaseSinceQpc: 10 * Freq,
            LastLoopProgressQpc: 12 * Freq,
            LoopSequence: 42,
            FramesReceived: 3,
            FramesAccepted: 2,
            LastHResult: unchecked((int)0x887A0026),
            ManagedThreadId: 17,
            NativeThreadId: 99,
            Generation: 5,
            MonitorDeviceId: @"\\?\DISPLAY#MONITOR",
            PublishedQpc: now);

        string text = snapshot.Describe(now, Freq);
        Assert.Contains("FrameArrived", text);
        Assert.Equal(5.0, snapshot.PhaseSeconds(now, Freq), precision: 3);
        Assert.Equal(3.0, snapshot.SinceLoopProgressSeconds(now, Freq), precision: 3);
        Assert.Contains("0x887A0026", text);
        Assert.Contains("managed 17/native 99", text);
        Assert.Contains("генерация 5", text);
    }
}
