using Vortice.MediaFoundation;
using InstantReplay.Core.Audio;
using InstantReplay.Core.Buffering;
using InstantReplay.Core.Capture;
using InstantReplay.Core.Encoding;
using InstantReplay.Core.GameDetection;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Saving;
using InstantReplay.Core.Settings;
using InstantReplay.Core.Storage;

namespace InstantReplay.Core.Engine;

public enum EngineState { Stopped, Running, Saving }

/// <summary>
/// Главный движок Instant Replay: конвейер
/// WGC (BGRA, VRAM) → VideoProcessor (NV12, VRAM) → HW MFT (NVENC/AMF/QSV)
/// → кольцевой RAM-буфер сжатых кадров; параллельно AudioMixer → аудиобуфер.
/// SaveReplay() делает мгновенный снимок буферов и в фонеремуксит в MP4.
/// </summary>
public sealed class ReplayEngine : IDisposable
{
    private readonly SettingsManager _settings;
    private readonly StorageManager _storage;

    private IScreenCapture? _capture;
    private VideoProcessorNv12? _processor;
    private VideoEncoder? _encoder;
    private readonly AudioMixerEngine _audio = new();

    private readonly ReplayVideoBuffer _videoBuffer = new();
    private readonly ReplayAudioBuffer _audioBuffer = new();

    public EngineState State { get; private set; } = EngineState.Stopped;
    public event Action<EngineState>? StateChanged;
    /// <summary>
    /// Снимок буфера сделан — клип уже гарантирован, дальше только запись файла.
    /// Аргумент — длительность клипа в секундах. Именно по этому событию показывается
    /// уведомление: ждать конца записи, чтобы сказать «сохранено», незачем.
    /// </summary>
    public event Action<int>? ReplayCaptured;
    /// <summary>Файл дописан на диск: путь + фактическая длительность (сек).</summary>
    public event Action<string, int>? ReplaySaved;
    public event Action<string>? SaveFailed;
    /// <summary>Сообщение о нештатной ситуации для пользователя (потеря GPU, восстановление).</summary>
    public event Action<string>? Warning;

    public TimeSpan BufferedDuration => TimeSpan.FromTicks(_videoBuffer.BufferedDurationTicks);
    public long BufferedBytes => _videoBuffer.TotalBytes;

    /// <summary>Метка активного энкодера для шапки UI, напр. "h264_nvenc".</summary>
    public string EncoderLabel => _encoder?.EncoderLabel ?? "";
    /// <summary>Вендор активного энкодера (NVIDIA/AMD/Intel).</summary>
    public string EncoderVendor => _encoder?.EncoderVendor ?? "";
    /// <summary>Живые пиковые уровни аудио (0..1) — для индикаторов.</summary>
    public (float Game, float Mic) AudioLevels => (_audio.GamePeak, _audio.MicPeak);

    /// <summary>Счётчики конвейера для панели «Обзор»: сколько кадров прошло каждую стадию.</summary>
    public (long Received, long Accepted, long Encoded, long Dropped, long Duplicated) FrameCounters =>
        (_capture?.FramesReceived ?? 0, _capture?.FramesAccepted ?? 0,
         _encoder?.FramesEncoded ?? 0, _encoder?.FramesDroppedQueue ?? 0, _encoder?.FramesDuplicated ?? 0);

    /// <summary>Размер кадра, который реально уходит в энкодер (после масштабирования).</summary>
    public (int Width, int Height) OutputSize => (_processor?.OutWidth ?? 0, _processor?.OutHeight ?? 0);

    /// <summary>Готовность текущего сохранения, 0..1 — для показа в UI вместо немой паузы.</summary>
    public double SaveProgress { get; private set; }

    /// <summary>
    /// Отдать последний захваченный кадр (для скриншота), не создавая вторую
    /// сессию захвата. На Windows 10 это единственный рабочий путь при включённом
    /// буфере: DXGI не даёт второй дупликации того же монитора.
    /// false — буфер выключен, вызывающий сделает свою одноразовую сессию.
    /// </summary>
    public bool TryUseLiveFrame(UseFrame use)
    {
        var cap = _capture;
        if (cap is null || State == EngineState.Stopped) return false;
        return cap.TryUseLatestFrame(tex => use(cap.D3DDevice, cap.D3DContext, tex));
    }

    // ---- Обычная запись в файл («Начать запись») ----
    private ManualRecorder? _recorder;
    public bool IsRecordingToFile => _recorder is not null;
    public event Action<bool>? RecordingChanged;
    /// <summary>Успешное завершение обычной записи: путь + длительность (сек).</summary>
    public event Action<string, int>? RecordingSaved;

    public ReplayEngine(SettingsManager settings, StorageManager storage)
    {
        _settings = settings;
        _storage = storage;
        // Реакция на изменение настроек записи: перезапуск конвейера на лету
        _settings.Changed += group =>
        {
            // Шумодав применяется на лету — перезапускать конвейер ради порога незачем
            if (group is "" or "video" or "audio")
            {
                _audio.MicNoiseGate = _settings.Current.MicNoiseSuppression;
                _audio.MicGateThresholdDb = _settings.Current.MicNoiseGateDb;
            }
            if (State == EngineState.Stopped) return;
            if (group is "video" or "audio" or "replay")
            {
                Log.Info("Engine", $"Настройки '{group}' изменены — перезапускаю конвейер");
                Stop(); Start();
            }
        };
    }

    public void Start()
    {
        if (State != EngineState.Stopped) return;
        var s = _settings.Current;
        try
        {
            _videoBuffer.MaxDurationTicks = TimeSpan.FromSeconds(s.ReplayLengthSeconds).Ticks;
            _audioBuffer.MaxDurationTicks = _videoBuffer.MaxDurationTicks;
            _videoBuffer.Clear();
            _audioBuffer.Clear();

            _capture = ScreenCaptureFactory.Create();
            _capture.Start(s.MonitorIndex, s.Fps, s.RecordCursor);

            _processor = new VideoProcessorNv12(_capture.D3DDevice, _capture.D3DContext);
            _processor.Configure(_capture.Width, _capture.Height, s.VerticalResolution, s.Fps);

            _encoder = new VideoEncoder();
            _encoder.Initialize(_capture.D3DDevice, _processor.OutWidth, _processor.OutHeight,
                                s.Fps, s.BitrateBps, s.Codec);
            _encoder.FrameEncoded += _videoBuffer.Add;

            _capture.FrameArrived += OnFrame;

            _audio.MicNoiseGate = s.MicNoiseSuppression;
            _audio.MicGateThresholdDb = s.MicNoiseGateDb;
            _audio.BlockReady += _audioBuffer.Add;
            _audio.Start(s.CaptureGameAudio, s.CaptureMicrophone, s.RenderDeviceId, s.CaptureDeviceId);

            // Меньше блокирующих GC-пауз, пока идёт запись
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            SetState(EngineState.Running);
            StartStatsTimer();
            StartCaptureWatchdog();
            Log.Info("Engine", "Instant Replay включен");
        }
        catch (Exception ex)
        {
            Log.Error("Engine", ex);
            Stop();
            throw;
        }
    }

    private void OnFrame(Vortice.Direct3D11.ID3D11Texture2D bgra, long ticks)
    {
        try
        {
            long t0 = Diagnostics.PipelineProbe.Now();
            var nv12 = _processor!.Convert(bgra);
            long t1 = Diagnostics.PipelineProbe.Now();
            _encoder!.SubmitFrame(nv12, ticks, _capture!.D3DContext);
            long t2 = Diagnostics.PipelineProbe.Now();
            Diagnostics.PipelineProbe.Convert.Add(t0, t1);
            Diagnostics.PipelineProbe.Submit.Add(t1, t2);
        }
        catch (Exception ex)
        {
            // Сброс/пропажа устройства — единственная ошибка кадра, из которой конвейер
            // сам не выберется: все объекты D3D мертвы, нужно пересобирать с нуля.
            if (DeviceLoss.IsDeviceLost(ex))
            {
                RecoverFromDeviceLoss($"ошибка кадра: {ex.Message}");
                return;
            }
            Log.Error("Engine", $"Кадр пропущен: {ex.Message}");
        }
    }

    // ---------------- Восстановление после потери GPU-устройства ----------------

    private int _recovering;

    /// <summary>
    /// Пересобрать конвейер после потери устройства (TDR, обновление драйвера, смена GPU).
    /// Работа уходит в пул потоков: вызывают из колбэка захвата, а внутри мы этот же
    /// захват останавливаем. Драйверу нужно время подняться, поэтому попытки с паузами.
    /// </summary>
    private void RecoverFromDeviceLoss(string reason)
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1) return; // уже восстанавливаемся
        bool wasRecording = IsRecordingToFile;

        Task.Run(() =>
        {
            try
            {
                Log.Warn("Engine", $"Потеряно устройство GPU ({reason}) — пересобираю конвейер");
                Warning?.Invoke("Сброс драйвера GPU — перезапускаю запись");
                try { Stop(); } catch (Exception ex) { Log.Warn("Engine", $"Остановка: {ex.Message}"); }

                for (int attempt = 1; attempt <= RecoveryAttempts; attempt++)
                {
                    Thread.Sleep(attempt == 1 ? 1500 : 3000);
                    try
                    {
                        Start();
                        Log.Info("Engine", $"Конвейер восстановлен (попытка {attempt})");
                        if (wasRecording)
                        {
                            // Прежний файл записи закрыт корректно в Stop(); продолжаем в новый
                            try { StartRecordingToFile(); }
                            catch (Exception ex) { Log.Warn("Engine", $"Запись не возобновилась: {ex.Message}"); }
                        }
                        Warning?.Invoke("Запись восстановлена");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Engine", $"Восстановление, попытка {attempt}: {ex.Message}");
                    }
                }
                Log.Error("Engine", "Восстановить конвейер не удалось");
                Warning?.Invoke("Не удалось восстановить запись — включите Instant Replay заново");
            }
            finally { Interlocked.Exchange(ref _recovering, 0); }
        });
    }

    private const int RecoveryAttempts = 10;

    // Раз в минуту — здоровье конвейера в лог: по этим цифрам видно, ГДЕ теряются
    // кадры (дропы очереди = не успевает энкодер; низкий submit = не успевает захват).
    private System.Threading.Timer? _statsTimer;
    private long _lastSubmitted, _lastEncoded, _lastDropped, _lastDuplicated, _lastReceived, _lastAccepted;

    private void StartStatsTimer()
    {
        _lastSubmitted = _lastEncoded = _lastDropped = _lastDuplicated = _lastReceived = _lastAccepted = 0;
        _statsTimer?.Dispose();
        _statsTimer = new System.Threading.Timer(_ =>
        {
            var enc = _encoder;
            var cap = _capture;
            if (enc is null || State == EngineState.Stopped) return;
            long s = enc.FramesSubmitted, e = enc.FramesEncoded,
                 d = enc.FramesDroppedQueue, dup = enc.FramesDuplicated;
            long rcv = cap?.FramesReceived ?? 0, acc = cap?.FramesAccepted ?? 0;
            Log.Info("Engine", $"Конвейер за минуту: WGC {rcv - _lastReceived}/{acc - _lastAccepted} " +
                $"(получено/принято), захвачено {s - _lastSubmitted}, " +
                $"дубликатов {dup - _lastDuplicated}, закодировано {e - _lastEncoded}, " +
                $"дропнуто {d - _lastDropped} (буфер {(int)BufferedDuration.TotalSeconds} сек)");

            // Где именно уходит бюджет кадра (16.7 мс при 60 fps)
            string probe = Diagnostics.PipelineProbe.TakeReport();
            if (probe.Length > 0) Log.Info("Engine", probe);

            _lastSubmitted = s; _lastEncoded = e; _lastDropped = d; _lastDuplicated = dup;
            _lastReceived = rcv; _lastAccepted = acc;
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // Вотчдог захвата: если WGC замолчал надолго (монитор выключился по AFK, сон,
    // сброс драйвера) — сессия захвата может умереть насовсем. Буфер при этом жив
    // (пейсер дублирует последний кадр), но реальная картинка не вернётся сама.
    // Каждые 5 сек проверяем приток кадров; тишина >15 сек — пересоздаём WGC-сессию.
    // Пока монитор выключен, попытки просто повторяются, при пробуждении — оживает.
    private System.Threading.Timer? _watchdog;
    private long _wdLastReceived = -1;
    private DateTime _wdLastActivity = DateTime.UtcNow;
    private bool _wdEpisodeLogged; // логируем только начало эпизода тишины, не каждые 15 сек
    private int _wdFailures;       // сколько попыток пересоздать сессию не удалось подряд

    private void StartCaptureWatchdog()
    {
        _wdLastReceived = -1;
        _wdFailures = 0;
        _wdLastActivity = DateTime.UtcNow;
        _watchdog?.Dispose();
        _watchdog = new System.Threading.Timer(_ =>
        {
            var cap = _capture;
            if (cap is null || State == EngineState.Stopped) return;

            long received = cap.FramesReceived;
            if (received != _wdLastReceived)
            {
                _wdLastReceived = received;
                _wdLastActivity = DateTime.UtcNow;
                _wdEpisodeLogged = false;
                return;
            }
            if ((DateTime.UtcNow - _wdLastActivity).TotalSeconds < 15) return;

            _wdLastActivity = DateTime.UtcNow; // не чаще одной попытки в 15 сек
            var s = _settings.Current;
            try
            {
                if (!_wdEpisodeLogged)
                {
                    _wdEpisodeLogged = true;
                    Log.Warn("Engine", "Захват молчит >15 сек (AFK/монитор выключен?) — пересоздаю WGC-сессию");
                }
                cap.Start(s.MonitorIndex, s.Fps, s.RecordCursor);
                _wdFailures = 0;
            }
            catch (Exception ex)
            {
                // Пересоздание сессии не помогает, если умерло само устройство D3D:
                // тогда лечит только полная пересборка конвейера.
                if (DeviceLoss.IsDeviceLost(ex))
                {
                    RecoverFromDeviceLoss($"захват не создаётся: {ex.Message}");
                    return;
                }
                if (++_wdFailures >= 3)
                {
                    _wdFailures = 0;
                    RecoverFromDeviceLoss("захват не восстанавливается три попытки подряд");
                    return;
                }
                if (_wdEpisodeLogged) return; // монитор выключен — молча ждём пробуждения
                Log.Warn("Engine", $"Захват пока не восстановился: {ex.Message}");
            }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Stop()
    {
        _watchdog?.Dispose(); _watchdog = null;
        _statsTimer?.Dispose(); _statsTimer = null;
        if (_recorder is not null) StopRecordingToFile(); // корректно закрываем файл записи
        if (_capture is not null) _capture.FrameArrived -= OnFrame;
        _audio.BlockReady -= _audioBuffer.Add;
        _audio.Stop();
        _encoder?.Dispose(); _encoder = null;
        _processor?.Dispose(); _processor = null;
        _capture?.Dispose(); _capture = null;
        _videoBuffer.Clear();
        _audioBuffer.Clear();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
        SetState(EngineState.Stopped);
        Log.Info("Engine", "Instant Replay выключен");
    }

    public void Toggle()
    {
        if (State == EngineState.Stopped) Start(); else Stop();
    }

    /// <summary>
    /// Сохранить последние N секунд (по умолчанию — вся длина буфера из настроек).
    /// Снимок буферов мгновенный, remux — в фоне. После снимка буфер сбрасывается:
    /// каждый следующий повтор начинается с чистого листа.
    /// </summary>
    public void SaveReplay(int? secondsOverride = null)
    {
        if (State != EngineState.Running || _encoder?.OutputMediaType is null) return;

        var s = _settings.Current;
        long wanted = TimeSpan.FromSeconds(secondsOverride ?? s.ReplayLengthSeconds).Ticks;

        // Снимок с очисткой: буфер начинает копиться заново, владение массивами
        // кадров переходит нам — вернём их в пул после записи файла.
        var video = _videoBuffer.SnapshotAndClear(wanted);
        if (video.Count == 0) { SaveFailed?.Invoke("Буфер ещё пуст"); return; }
        var audio = _audioBuffer.Snapshot(video[0].PtsTicks, video[^1].PtsTicks);
        _audioBuffer.Clear();

        // Игра определяется В МОМЕНТ сохранения по активному процессу
        string game = GameDetector.DetectForegroundGame();
        string file = BuildFilePath(game, "replay");
        var mediaType = _encoder.OutputMediaType;
        int seconds = (int)Math.Round(TimeSpan.FromTicks(video[^1].PtsTicks - video[0].PtsTicks).TotalSeconds);

        // Данные уже вырваны из кольцевого буфера и никуда не денутся — говорим об этом
        // пользователю сразу. Раньше уведомление ждало, пока сотни мегабайт доедут
        // до диска, и на длинном клипе это выглядело как «хоткей не сработал».
        SaveProgress = 0;
        ReplayCaptured?.Invoke(Math.Max(seconds, 1));

        SetState(EngineState.Saving);
        Task.Run(() =>
        {
            // Сохранение — пакетная фоновая работа: сотни МБ копий в нативные буферы
            // MF плюс сброс на диск. На обычном приоритете она конкурирует с потоками
            // захвата и кодирования (у тех AboveNormal), и входная очередь энкодера
            // успевает переполниться: в замерах пик очереди 66 из 66 и 523 дропнутых
            // кадра ровно в минуту сохранения, при этом сам ProcessInput не тормозил.
            // Лишние полсекунды на запись файла не заметит никто, потерянные кадры — да.
            var self = Thread.CurrentThread;
            var previousPriority = self.Priority;
            self.Priority = ThreadPriority.BelowNormal;

            // В игре запись клипа уходит в фоновый режим Windows: приоритет дисковых
            // операций падает, и залп в сотни мегабайт не отбирает ввод-вывод у игры.
            // На рабочем столе тормозить сохранение незачем — там пишем в полную силу.
            bool inGame = !string.Equals(game, "Desktop", StringComparison.OrdinalIgnoreCase);
            using var backgroundIo = Saving.BackgroundIoScope.BeginIf(inGame);
            try
            {
                // Автоочистка сканирует всю папку записей рекурсивно — на большой
                // коллекции или медленном диске это ощутимая пауза ПЕРЕД записью.
                var cleanupWatch = System.Diagnostics.Stopwatch.StartNew();
                _storage.EnsureSpace(); // чистим старые записи при нехватке места ДО записи
                if (cleanupWatch.ElapsedMilliseconds > 200)
                    Log.Warn("Engine", $"Проверка места заняла {cleanupWatch.ElapsedMilliseconds} мс " +
                                       "— сохранение начинается с задержкой");

                ReplaySaver.Save(file, video, audio, mediaType, s.TrackMode,
                                 s.CaptureGameAudio, s.CaptureMicrophone,
                                 p => SaveProgress = p);
                _storage.RegisterSaved(file); // индекс папки — без повторного обхода диска
                s.TotalReplaysSaved++;
                _settings.Save("stats");
                ReplaySaved?.Invoke(file, Math.Max(seconds, 1));
            }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                ReplayVideoBuffer.ReturnToPool(video); // файл записан — массивы кадров в пул
                self.Priority = previousPriority;      // поток уходит обратно в пул потоков
                SetState(State == EngineState.Stopped ? EngineState.Stopped : EngineState.Running);
            }
        });
    }

    /// <summary>
    /// Путь файла по шаблону из настроек. {game} {date} {time} {preset} + раскладка по папкам игр.
    /// </summary>
    private string BuildFilePath(string game, string fallbackPrefix)
    {
        var s = _settings.Current;
        return FileNaming.BuildPath(s.SaveRootPath, s.GroupByGame, s.FileNameTemplate, game,
                                    DateTime.Now, $"{s.VerticalResolution}p{s.Fps}", fallbackPrefix);
    }

    // ---------------- Обычная запись в файл ----------------

    /// <summary>Начать обычную запись в файл. Если буфер выключен — включает его.</summary>
    public void StartRecordingToFile()
    {
        if (_recorder is not null) return;
        if (State == EngineState.Stopped) Start(); // может бросить — наружу, UI покажет
        if (_encoder?.OutputMediaType is null) return;

        var s = _settings.Current;
        string game = GameDetector.DetectForegroundGame();
        _storage.EnsureSpace();
        var recorder = new ManualRecorder(BuildFilePath(game, "recording"),
            _encoder.OutputMediaType, s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone);
        _encoder.FrameEncoded += recorder.OnFrame;
        _audio.BlockReady += recorder.OnAudio;
        _recorder = recorder;
        RecordingChanged?.Invoke(true);
    }

    /// <summary>Остановить обычную запись. Возвращает путь к файлу (null — если не писали).</summary>
    public string? StopRecordingToFile()
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        _recorder = null;
        if (_encoder is not null) _encoder.FrameEncoded -= recorder.OnFrame;
        _audio.BlockReady -= recorder.OnAudio;
        int seconds = recorder.Finish();
        _storage.RegisterSaved(recorder.FilePath);
        RecordingChanged?.Invoke(false);
        RecordingSaved?.Invoke(recorder.FilePath, Math.Max(seconds, 1));
        return recorder.FilePath;
    }

    private void SetState(EngineState st)
    {
        State = st;
        StateChanged?.Invoke(st);
    }

    public void Dispose()
    {
        Stop();
        _audio.Dispose();
    }
}
