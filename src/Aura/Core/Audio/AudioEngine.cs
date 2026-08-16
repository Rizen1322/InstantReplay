using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Aura.Core.Buffering;
using Aura.Core.Logging;

namespace Aura.Core.Audio;

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
        var flow = loopback ? DataFlow.Render : DataFlow.Capture;
        MMDevice device = Resolve(enumerator, flow, deviceId, loopback);

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

        // Приведение к 48k/stereo/float — включая устройства с 5.1 и 7.1,
        // которые раньше проезжали мимо и ломали темп записи (см. AudioFormat)
        _pipeline = AudioFormat.Normalize(_buffered.ToSampleProvider());

        _capture.StartRecording();
        Log.Info("Audio", $"Источник запущен: {(loopback ? "loopback" : "mic")} {device.FriendlyName} ({_capture.WaveFormat})");
        if (_capture.WaveFormat.Channels != Channels)
            Log.Info("Audio", $"Устройство отдаёт {_capture.WaveFormat.Channels} канала(ов) — свожу в стерео");
    }

    /// <summary>
    /// На какое устройство пришлось откатиться, потому что выбранное не нашлось.
    /// null — взяли ровно то, что просили. Заполняется, чтобы движок мог сказать
    /// об этом человеку, а не оставить его с немой записью.
    /// </summary>
    public string? FellBackTo { get; private set; }

    /// <summary>
    /// Найти устройство по сохранённому идентификатору, а если его больше нет —
    /// взять текущее по умолчанию.
    ///
    /// ЗАЧЕМ ОТКАТ. Идентификатор устройства лежит в настройках и переживает
    /// смену железа. Купил человек новые наушники — старый ID перестаёт
    /// разрешаться (ERROR_NOT_FOUND, 0x80070490), и раньше запись просто
    /// оставалась без звука: ошибка уходила в лог, источник не создавался,
    /// и никто об этом не сообщал.
    /// </summary>
    private MMDevice Resolve(MMDeviceEnumerator enumerator, DataFlow flow, string? deviceId, bool loopback)
    {
        if (deviceId is not null)
            try { return enumerator.GetDevice(deviceId); }
            catch (Exception ex)
            {
                var fallback = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                FellBackTo = fallback.FriendlyName;
                Log.Warn("Audio", $"Выбранное устройство ({(loopback ? "звук игры" : "микрофон")}) " +
                                  $"не найдено ({ex.Message}) — беру по умолчанию: {fallback.FriendlyName}");
                return fallback;
            }

        return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
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

    /// <summary>
    /// Сэмплов в блоке на дорожку. Публичная: по этому числу кольцевой аудиобуфер
    /// считает свою арену, а сам он про NAudio знать не должен — иначе его не
    /// подключить к тестам.
    /// </summary>
    public const int BlockSamples = BlockFrames * AudioCaptureSource.Channels;

    private AudioCaptureSource? _game;
    private AudioCaptureSource? _mic;
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>Noise gate для микрофона: глушит фоновый гул, когда голоса нет.</summary>
    public bool MicNoiseGate { get; set; }

    /// <summary>
    /// Порог шумодава в дБFS: всё тише этого уровня считается фоном и глушится.
    /// -60 — почти всё пропускает (только совсем тихий гул), -30 — жёстко режет и
    /// может съедать начало тихих фраз. Разумная середина около -44.
    /// </summary>
    public float MicGateThresholdDb { get; set; } = -44f;

    /// <summary>Пиковые уровни последнего блока (0..1) — для индикаторов в UI.</summary>
    public float GamePeak { get; private set; }
    public float MicPeak { get; private set; }

    private float _gateEnvelope; // сглаженная огибающая гейта (0 закрыт .. 1 открыт)

    public event Action<AudioBlock>? BlockReady;

    /// <summary>
    /// Со звуком что-то не так, и человеку об этом надо сказать: устройство
    /// не найдено, пришлось откатиться на другое, запись пойдёт без звука.
    /// Немой клип обнаруживается уже после того, как момент упущен.
    /// </summary>
    public event Action<string>? Warning;

    // Настройки последнего запуска — по ним пересоздаём источник, если система
    // сменила устройство по умолчанию.
    private bool _wantGame, _wantMic;
    private string? _renderDeviceId, _captureDeviceId;
    private MMDeviceEnumerator? _deviceWatchEnumerator;
    private DefaultDeviceWatcher? _deviceWatcher;

    public void Start(bool captureGame, bool captureMic, string? renderDeviceId, string? captureDeviceId)
    {
        Stop();

        _wantGame = captureGame; _wantMic = captureMic;
        _renderDeviceId = renderDeviceId; _captureDeviceId = captureDeviceId;

        if (captureGame)
            try
            {
                _game = new AudioCaptureSource(loopback: true, renderDeviceId);
                if (_game.FellBackTo is { } name)
                    Warning?.Invoke($"Выбранное устройство вывода не найдено — пишу звук с «{name}»");
            }
            catch (Exception ex)
            {
                Log.Error("Audio", $"Loopback недоступен: {ex.Message}");
                Warning?.Invoke("Звук игры записать не удалось — проверьте устройство вывода в настройках");
            }
        if (captureMic)
            try
            {
                _mic = new AudioCaptureSource(loopback: false, captureDeviceId);
                if (_mic.FellBackTo is { } name)
                    Warning?.Invoke($"Выбранный микрофон не найден — пишу с «{name}»");
            }
            catch (Exception ex)
            {
                Log.Error("Audio", $"Микрофон недоступен: {ex.Message}");
                Warning?.Invoke("Микрофон записать не удалось — проверьте устройство в настройках");
            }

        WatchDefaultDevices();

        _running = true;
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>
    /// Следим за сменой устройства по умолчанию.
    ///
    /// Источник привязывается к устройству один раз и сам о подмене не узнаёт:
    /// воткнули наушники, выдернули гарнитуру, Windows переключила вывод — а
    /// loopback продолжает слушать старое устройство. Запись при этом идёт дальше,
    /// индикаторы показывают тишину, и человек обнаруживает немой клип потом,
    /// когда уже ничего не вернуть. Поэтому пересоздаём источник на лету.
    ///
    /// Следим только за теми источниками, которые взяты «по умолчанию»: если
    /// устройство выбрано в настройках явно, подменять его нельзя.
    /// </summary>
    private void WatchDefaultDevices()
    {
        try
        {
            _deviceWatchEnumerator = new MMDeviceEnumerator();
            _deviceWatcher = new DefaultDeviceWatcher(flow =>
            {
                bool affectsGame = flow == DataFlow.Render && _wantGame && _renderDeviceId is null;
                bool affectsMic = flow == DataFlow.Capture && _wantMic && _captureDeviceId is null;
                if (affectsGame || affectsMic) RestartSource(flow);
            });
            _deviceWatchEnumerator.RegisterEndpointNotificationCallback(_deviceWatcher);
        }
        catch (Exception ex) { Log.Warn("Audio", $"Слежение за устройствами недоступно: {ex.Message}"); }
    }

    private readonly object _restartSync = new();

    private void RestartSource(DataFlow flow)
    {
        // Windows шлёт уведомление до того, как новое устройство готово принимать
        // клиентов, поэтому даём ему мгновение и уходим с потока уведомлений.
        Task.Run(() =>
        {
            Thread.Sleep(300);
            lock (_restartSync)
            {
                if (!_running) return;
                try
                {
                    if (flow == DataFlow.Render)
                    {
                        var replacement = new AudioCaptureSource(loopback: true, null);
                        var old = _game;
                        _game = replacement;
                        old?.Dispose();
                        Log.Info("Audio", "Устройство вывода сменилось — источник звука игры пересоздан");
                    }
                    else
                    {
                        var replacement = new AudioCaptureSource(loopback: false, null);
                        var old = _mic;
                        _mic = replacement;
                        old?.Dispose();
                        Log.Info("Audio", "Устройство ввода сменилось — микрофон пересоздан");
                    }
                }
                catch (Exception ex) { Log.Error("Audio", $"Не удалось пересоздать источник: {ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// Уведомления WASAPI. Интересует единственное событие — смена устройства по
    /// умолчанию для роли Multimedia; остальное реализовано пустышками, так как
    /// интерфейс требует все методы.
    /// </summary>
    private sealed class DefaultDeviceWatcher(Action<DataFlow> onDefaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia) onDefaultChanged(flow);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
        // Приёмники PCM живут столько же, сколько поток. Блок отдаётся подписчикам
        // ссылкой и валиден только на время вызова (см. AudioBlock): раньше здесь
        // выделялись два свежих массива на КАЖДЫЕ 10 мс, то есть двести в секунду,
        // и кольцевой буфер держал их все живыми до самого вытеснения.
        var gameOut = new short[BlockSamples];
        var micOut = new short[BlockSamples];
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

            // Noise gate микрофона: ниже порога плавно закрываемся. Порог задаётся
            // пользователем в дБ (см. MicGateThresholdDb), быстрая атака (голос не
            // «съедается»), медленный релиз (нет щёлканья).
            float gate = 1f;
            if (MicNoiseGate && _mic is not null)
            {
                double sum = 0;
                for (int i = 0; i < BlockSamples; i++) sum += micBuf[i] * micBuf[i];
                float rms = (float)Math.Sqrt(sum / BlockSamples);
                float threshold = (float)Math.Pow(10, Math.Clamp(MicGateThresholdDb, -70f, -10f) / 20.0);
                float target = rms > threshold ? 1f : 0f;
                float coef = target > _gateEnvelope ? 0.6f : 0.06f;
                _gateEnvelope += (target - _gateEnvelope) * coef;
                gate = _gateEnvelope;
            }

            // Считаем в float (шумодав и пики требуют дробной точности), а отдаём
            // 16 бит: буфер хранит часы звука, и вчетверо более широкий формат там
            // ничего не добавляет — дальше всё равно AAC.
            float gPeak = 0, mPeak = 0;
            for (int i = 0; i < BlockSamples; i++)
            {
                float g = Math.Clamp(gameBuf[i], -1f, 1f);
                float m = Math.Clamp(micBuf[i] * gate, -1f, 1f);
                gameOut[i] = ToPcm16(g);
                micOut[i] = ToPcm16(m);
                float ga = Math.Abs(g); if (ga > gPeak) gPeak = ga;
                float ma = Math.Abs(m); if (ma > mPeak) mPeak = ma;
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

    /// <summary>
    /// float −1…1 → PCM16. Умножаем на 32767, а не на 32768: иначе ровно +1.0
    /// переполняет short и громкий звук щёлкает.
    /// </summary>
    private static short ToPcm16(float value) => (short)MathF.Round(value * 32767f);

    public void Stop()
    {
        try
        {
            if (_deviceWatcher is not null && _deviceWatchEnumerator is not null)
                _deviceWatchEnumerator.UnregisterEndpointNotificationCallback(_deviceWatcher);
        }
        catch { }
        _deviceWatcher = null;
        _deviceWatchEnumerator?.Dispose();
        _deviceWatchEnumerator = null;

        _running = false;
        _thread?.Join(500); _thread = null;
        _game?.Dispose(); _game = null;
        _mic?.Dispose(); _mic = null;
    }

    public void Dispose() => Stop();
}
