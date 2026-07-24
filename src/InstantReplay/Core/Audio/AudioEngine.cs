using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using InstantReplay.Core.Buffering;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Audio;

/// <summary>
/// Один источник звука (loopback устройства вывода или микрофон),
/// приведённый к каноническому формату 48 кГц / stereo / float32.
///
/// Грабли из практики: WASAPI loopback НЕ отдаёт данные в тишине, поэтому
/// пассивное чтение потока в pipe даёт рассинхрон. Источник только складывает
/// данные в буфер; непрерывность обеспечивает внешний микшер (AudioMixerEngine),
/// который по системным часам каждые 10 мс читает ровно 480 фреймов, а недостачу
/// BufferedWaveProvider (ReadFully = true) добивает тишиной.
/// </summary>
public sealed class AudioCaptureSource : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffered;
    private readonly ISampleProvider _pipeline;

    public AudioCaptureSource(bool loopback, string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        MMDevice device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, Role.Multimedia)
            : enumerator.GetDevice(deviceId);

        _capture = loopback ? new WasapiLoopbackCapture(device)
                            : new WasapiCapture(device) { ShareMode = AudioClientShareMode.Shared };

        _buffered = new BufferedWaveProvider(_capture.WaveFormat)
        {
            ReadFully = true,                // < тишина-заполнение при недостатке данных
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };
        _capture.DataAvailable += (_, e) => _buffered.AddSamples(e.Buffer, 0, e.BytesRecorded);
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Log.Error("Audio", $"Источник остановился: {e.Exception.Message}");
        };

        // Приведение к 48k/stereo/float
        ISampleProvider sp = _buffered.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);
        _pipeline = sp;

        _capture.StartRecording();
        Log.Info("Audio", $"Источник запущен: {(loopback ? "loopback" : "mic")} {device.FriendlyName} ({_capture.WaveFormat})");
    }

    /// <summary>Читает ровно count float-сэмплов (interleaved). Недостача = тишина.</summary>
    public void Read(float[] dest, int count) => _pipeline.Read(dest, 0, count);

    /// <summary>Сколько миллисекунд аудио скопилось в буфере (хвост = запаздывание звука).</summary>
    public double BufferedMs => _buffered.BufferedDuration.TotalMilliseconds;

    /// <summary>Выбрасывает излишек буфера, оставляя ~keepMs (ресинхронизация звука).</summary>
    public void DiscardExcess(int keepMs)
    {
        double excess = BufferedMs - keepMs;
        if (excess <= 0) return;
        int samples = (int)(excess / 1000.0 * SampleRate) * Channels;
        var scratch = new float[Math.Min(samples, SampleRate * Channels)]; // максимум 1 сек за раз
        while (samples > 0)
        {
            int n = Math.Min(samples, scratch.Length);
            _pipeline.Read(scratch, 0, n);
            samples -= n;
        }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}

/// <summary>
/// Активный микшер: собственный поток идёт по системным часам (QPC) с шагом 10 мс
/// и на каждый тик формирует блок из 480 фреймов на дорожку — независимо от того,
/// прислал ли WASAPI данные. Результат — непрерывный PCM-поток с монотонными
/// таймстампами в той же QPC-шкале, что и видеокадры WGC → идеальная синхронизация
/// без участия энкодера (AAC-энкодер получает уже готовый поток).
/// </summary>
public sealed class AudioMixerEngine : IDisposable
{
    private const int BlockFrames = AudioCaptureSource.SampleRate / 100; // 480 фреймов = 10 мс
    private const int BlockSamples = BlockFrames * AudioCaptureSource.Channels;

    private AudioCaptureSource? _game;
    private AudioCaptureSource? _mic;
    private Thread? _thread;
    private volatile bool _running;

    public float GameVolume { get; set; } = 1f;
    public float MicVolume { get; set; } = 1f;
    /// <summary>Noise gate для микрофона: глушит фоновый гул, когда голоса нет.</summary>
    public bool MicNoiseGate { get; set; }

    /// <summary>Пиковые уровни последнего блока (0..1) — для индикаторов в UI.</summary>
    public float GamePeak { get; private set; }
    public float MicPeak { get; private set; }

    private float _gateEnvelope; // сглаженная огибающая гейта (0 закрыт .. 1 открыт)

    public event Action<AudioBlock>? BlockReady;

    public void Start(bool captureGame, bool captureMic, string? renderDeviceId, string? captureDeviceId)
    {
        Stop();

        if (captureGame)
            try { _game = new AudioCaptureSource(loopback: true, renderDeviceId); }
            catch (Exception ex) { Log.Error("Audio", $"Loopback недоступен: {ex.Message}"); }
        if (captureMic)
            try { _mic = new AudioCaptureSource(loopback: false, captureDeviceId); }
            catch (Exception ex) { Log.Error("Audio", $"Микрофон недоступен: {ex.Message}"); }

        _running = true;
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void MixLoop()
    {
        // QPC → 100-нс тики: та же шкала, что frame.SystemRelativeTime у видео
        static long NowTicks() => (long)(System.Diagnostics.Stopwatch.GetTimestamp()
            * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

        long blockTicks = 100_000; // 10 мс
        long nextDeadline = NowTicks();
        long pts = nextDeadline;

        // Компенсация задержки WASAPI-loopback: данные, прочитанные сейчас,
        // прозвучали ~25 мс назад — без сдвига звук в записи запаздывает за видео.
        const long latencyCompensation = 250_000; // 25 мс

        var gameBuf = new float[BlockSamples];
        var micBuf = new float[BlockSamples];
        int backlogCheck = 0;

        while (_running)
        {
            // Раз в секунду следим за хвостом буферов: под игровой нагрузкой WASAPI
            // отдаёт данные пачками, хвост растёт — звук всё сильнее отстаёт.
            if (++backlogCheck >= 100)
            {
                backlogCheck = 0;
                if (_game is { BufferedMs: > 120 } g) g.DiscardExcess(40);
                if (_mic is { BufferedMs: > 120 } m) m.DiscardExcess(40);
            }
            // Читаем РОВНО 480 фреймов из каждого источника; если данных нет —
            // BufferedWaveProvider вернёт тишину. Поток не останавливается никогда.
            if (_game is not null) _game.Read(gameBuf, BlockSamples); else Array.Clear(gameBuf);
            if (_mic is not null) _mic.Read(micBuf, BlockSamples); else Array.Clear(micBuf);

            // Noise gate микрофона: ниже порога (~ -44 дБ) плавно закрываемся.
            // Быстрая атака (голос не «съедается»), медленный релиз (нет щёлканья).
            float gate = 1f;
            if (MicNoiseGate && _mic is not null)
            {
                double sum = 0;
                for (int i = 0; i < BlockSamples; i++) sum += micBuf[i] * micBuf[i];
                float rms = (float)Math.Sqrt(sum / BlockSamples);
                float target = rms > 0.006f ? 1f : 0f;
                float coef = target > _gateEnvelope ? 0.6f : 0.06f;
                _gateEnvelope += (target - _gateEnvelope) * coef;
                gate = _gateEnvelope;
            }

            var gameOut = new float[BlockSamples];
            var micOut = new float[BlockSamples];
            float gv = GameVolume, mv = MicVolume * gate;
            float gPeak = 0, mPeak = 0;
            for (int i = 0; i < BlockSamples; i++)
            {
                gameOut[i] = Math.Clamp(gameBuf[i] * gv, -1f, 1f);
                micOut[i] = Math.Clamp(micBuf[i] * mv, -1f, 1f);
                float ga = Math.Abs(gameOut[i]); if (ga > gPeak) gPeak = ga;
                float ma = Math.Abs(micOut[i]); if (ma > mPeak) mPeak = ma;
            }
            GamePeak = gPeak;
            MicPeak = mPeak;

            BlockReady?.Invoke(new AudioBlock(gameOut, micOut, pts - latencyCompensation));
            pts += blockTicks;

            // Держим темп по абсолютным дедлайнам (без дрейфа Thread.Sleep)
            nextDeadline += blockTicks;
            long wait = nextDeadline - NowTicks();
            if (wait > 20_000) Thread.Sleep((int)(wait / 10_000));
            else if (wait < -1_000_000) { nextDeadline = NowTicks(); pts = nextDeadline; } // сильно отстали — ресинхрон
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500); _thread = null;
        _game?.Dispose(); _game = null;
        _mic?.Dispose(); _mic = null;
    }

    public void Dispose() => Stop();
}
