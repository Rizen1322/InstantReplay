using Vortice.MediaFoundation;
using Aura.Core.Audio;
using Aura.Core.Buffering;
using Aura.Core.Capture;
using Aura.Core.Encoding;
using Aura.Core.GameDetection;
using Aura.Core.Logging;
using Aura.Core.Saving;
using Aura.Core.Settings;
using Aura.Core.Storage;

namespace Aura.Core.Engine;

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

    /// <summary>
    /// Один замок на весь жизненный цикл: <see cref="Start"/>, <see cref="Stop"/> и
    /// снимок в <see cref="SaveReplay"/> взаимно исключают друг друга.
    ///
    /// ЗАЧЕМ. Звать эти три метода могут четверо: поток интерфейса, обработчик
    /// изменения настроек, вотчдог захвата и восстановление после потери устройства.
    /// Проверка «if (State != Stopped) return» без замка их не разводит: два Start
    /// подряд собирают два конвейера, два Stop освобождают энкодер дважды.
    /// Monitor реентерантен, поэтому Stop() из catch внутри Start() работает.
    /// </summary>
    private readonly object _lifecycle = new();

    /// <summary>
    /// Ворота «кадров в полёте». <see cref="OnFrame"/> держит читательский замок на
    /// время работы с D3D, снос конвейера берёт писательский — и тем самым ждёт,
    /// пока текущий кадр досчитается.
    ///
    /// Без этого Stop() освобождал текстуры и контекст прямо под работающим
    /// Convert/SubmitFrame. Такое падение — access violation внутри драйвера,
    /// его не ловит ни try/catch в OnFrame, ни глобальный обработчик.
    /// </summary>
    private readonly ReaderWriterLockSlim _frameGate = new(LockRecursionPolicy.NoRecursion);

    /// <summary>Конвейер собран и кадры можно обрабатывать. Гасится первым при сносе.</summary>
    private volatile bool _pipelineOpen;

    /// <summary>Идущее сохранение — Stop() обязан его дождаться, а не сносить буферы под ним.</summary>
    private Task? _saveTask;
    private int? _saveTaskId;

    private volatile EngineState _state = EngineState.Stopped;
    public EngineState State => _state;
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

    /// <summary>Сколько занимает звуковая часть буфера повтора.</summary>
    public long BufferedAudioBytes => _audioBuffer.TotalBytes;

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
        // Те же ворота, что и у OnFrame: скриншот берёт кадр с живого устройства
        // захвата, и Stop() не должен освободить это устройство прямо во время чтения.
        if (!_frameGate.TryEnterReadLock(TimeSpan.FromMilliseconds(50))) return false;
        try
        {
            var cap = _capture;
            if (cap is null || !_pipelineOpen) return false;
            return cap.TryUseLatestFrame(tex => use(cap.D3DDevice, cap.D3DContext, tex));
        }
        finally { _frameGate.ExitReadLock(); }
    }

    // ---- Обычная запись в файл («Начать запись») ----
    private ManualRecorder? _recorder;
    public bool IsRecordingToFile => _recorder is not null;
    /// <summary>Сколько идёт обычная запись; null — не пишем.</summary>
    public TimeSpan? RecordingElapsed => _recorder is { } r ? DateTime.Now - r.StartedAt : null;
    /// <summary>Номер текущей части файла (файл режется по достижении предела MP4).</summary>
    public int RecordingPart => _recorder?.PartCount ?? 0;
    public event Action<bool>? RecordingChanged;
    /// <summary>Успешное завершение обычной записи: путь + длительность (сек).</summary>
    public event Action<string, int>? RecordingSaved;

    public ReplayEngine(SettingsManager settings, StorageManager storage)
    {
        _settings = settings;
        _storage = storage;
        // Проблемы со звуком должны доходить до человека сразу: немой клип
        // обнаруживается уже после того, как момент упущен
        _audio.Warning += msg => Warning?.Invoke(msg);
        // Реакция на изменение настроек записи: перезапуск конвейера на лету
        _settings.Changed += group =>
        {
            // Шумодав применяется на лету — перезапускать конвейер ради порога незачем
            if (group is "" or "video" or "audio")
            {
                _audio.MicNoiseGate = _settings.Current.MicNoiseSuppression;
                _audio.MicGateThresholdDb = _settings.Current.MicNoiseGateDb;
            }
            if (_state == EngineState.Stopped) return;
            if (group is "video" or "audio" or "replay")
            {
                Log.Info("Engine", $"Настройки '{group}' изменены — перезапускаю конвейер");
                // Под одним замком: между Stop и Start не должен вклиниться ни хоткей
                // сохранения, ни вотчдог со своим перезапуском
                lock (_lifecycle) { StopLocked(); StartLocked(); }
            }
        };
    }

    public void Start()
    {
        lock (_lifecycle) StartLocked();
    }

    private void StartLocked()
    {
        if (_state != EngineState.Stopped) return;
        var s = _settings.Current;
        try
        {
            _videoBuffer.MaxDurationTicks = TimeSpan.FromSeconds(s.ReplayLengthSeconds).Ticks;
            _audioBuffer.MaxDurationTicks = _videoBuffer.MaxDurationTicks;
            _videoBuffer.Clear();
            _audioBuffer.Release();
            // Арена под кадры: размер считается из длительности и битрейта, дальше
            // память не растёт — сколько выделено, столько буфер и занимает.
            _videoBuffer.Allocate(s.BitrateBps, s.ReplayLengthSeconds);
            // Своя арена под звук, по той же причине: раньше микшер выделял пару
            // массивов каждые 10 мс, и буфер держал их все живыми.
            _audioBuffer.Allocate(Audio.AudioMixerEngine.BlockSamples, s.ReplayLengthSeconds);

            _capture = ScreenCaptureFactory.Create(s.MonitorIndex);
            _capture.Start(s.MonitorIndex, s.Fps, s.RecordCursor);

            _processor = new VideoProcessorNv12(_capture.D3DDevice, _capture.D3DContext);
            _processor.Configure(_capture.Width, _capture.Height, s.VerticalResolution, s.Fps);

            _encoder = new VideoEncoder();
            _encoder.Initialize(_capture.D3DDevice, _processor.OutWidth, _processor.OutHeight,
                                s.Fps, s.BitrateBps, s.Codec);
            _encoder.FrameEncoded += _videoBuffer.Add;

            // Ворота открываем последним действием перед подпиской: до этого момента
            // кадр, прилетевший от уже стартовавшего захвата, не должен идти в конвейер
            _pipelineOpen = true;
            _capture.FrameArrived += OnFrame;
            // Источник сам не оживёт после потери устройства — пересобираем конвейер.
            // Для WGC это единственный путь: там кадры приходят в колбэк WinRT, из
            // которого исключение не выпустить, не уронив процесс.
            _capture.Failed += OnCaptureFailed;

            _audio.MicNoiseGate = s.MicNoiseSuppression;
            _audio.MicGateThresholdDb = s.MicNoiseGateDb;
            _audio.BlockReady += _audioBuffer.Add;
            _audio.Start(s.CaptureGameAudio, s.CaptureMicrophone, s.RenderDeviceId, s.CaptureDeviceId);

            // Меньше блокирующих GC-пауз, пока идёт запись
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            SetState(EngineState.Running);
            StartStatsTimer();
            StartCaptureWatchdog();
            StartGameTracker();
            Log.Info("Engine", "Instant Replay включен");
        }
        catch (Exception ex)
        {
            Log.Error("Engine", ex);
            StopLocked();
            throw;
        }
    }

    private void OnFrame(Vortice.Direct3D11.ID3D11Texture2D bgra, long ticks)
    {
        // Идёт снос конвейера — кадр уже некуда девать. Ждать нельзя: поток захвата
        // заблокировал бы сам снос, которого он дожидается.
        if (!_frameGate.TryEnterReadLock(0)) return;
        try
        {
            if (!_pipelineOpen) return;

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
        finally { _frameGate.ExitReadLock(); }
    }

    /// <summary>Источник кадров сообщил о потере устройства — пересобираем конвейер.</summary>
    private void OnCaptureFailed(Exception ex) =>
        RecoverFromDeviceLoss($"источник кадров остановился: {ex.Message}");

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
    private long _lastRequests, _lastPacerBlocked;

    private void StartStatsTimer()
    {
        _lastSubmitted = _lastEncoded = _lastDropped = _lastDuplicated = _lastReceived = _lastAccepted = 0;
        _lastRequests = _lastPacerBlocked = 0;
        _statsWindowStart = DateTime.UtcNow;
        _statsTimer?.Dispose();
        _statsTimer = new System.Threading.Timer(_ => DumpStats("за минуту"),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private DateTime _statsWindowStart = DateTime.UtcNow;

    /// <summary>
    /// Счётчики конвейера с прошлой выгрузки в лог. Зовётся раз в минуту, а ещё
    /// при сохранении повтора и остановке буфера: короткие сеансы (записал 20 секунд,
    /// сохранил, выключил) до минутного тика не доживали, и разбирать провал fps
    /// было не по чему.
    /// </summary>
    private void DumpStats(string label)
    {
        var enc = _encoder;
        var cap = _capture;
        if (enc is null || State == EngineState.Stopped) return;

        long s = enc.FramesSubmitted, e = enc.FramesEncoded,
             d = enc.FramesDroppedQueue, dup = enc.FramesDuplicated;
        long req = enc.InputRequests, blocked = enc.PacerBlocked;
        long rcv = cap?.FramesReceived ?? 0, acc = cap?.FramesAccepted ?? 0;
        double seconds = Math.Max((DateTime.UtcNow - _statsWindowStart).TotalSeconds, 0.001);
        if (seconds < 2) return; // только что выгружали — нечего показывать

        // fps по каждой стадии: сразу видно, кто именно не дотягивает до настроенного.
        Log.Info("Engine", $"Конвейер {label} ({seconds:F0} с): WGC {rcv - _lastReceived}/{acc - _lastAccepted} " +
            $"(получено/принято), захвачено {s - _lastSubmitted}, " +
            $"дубликатов {dup - _lastDuplicated}, закодировано {e - _lastEncoded}, " +
            $"дропнуто {d - _lastDropped} (буфер {(int)BufferedDuration.TotalSeconds} сек) | " +
            $"fps: WGC {(rcv - _lastReceived) / seconds:F1}, подано {(s - _lastSubmitted + dup - _lastDuplicated) / seconds:F1}, " +
            $"закодировано {(e - _lastEncoded) / seconds:F1}, запросов MFT {(req - _lastRequests) / seconds:F1}" +
            $", пресет {enc.QualityPreset}, кадров внутри MFT до {Interlocked.Exchange(ref enc.MaxInFlight, 0)}" +
            (blocked > _lastPacerBlocked ? $"; пейсер молчал {blocked - _lastPacerBlocked} раз (очередь полна)" : ""));

        // Видеопамять: превышение бюджета означает вытеснение текстур в оперативную
        // память через шину, и тогда застревает всё, что трогает GPU — и захват, и
        // кодирование разом. По одним лишь fps эту причину от прочих не отличить.
        if (cap is not null && GpuInfo.Usage(cap.D3DDevice) is { } vram)
        {
            string verdict = vram.UsedMb > vram.BudgetMb ? " — БЮДЖЕТ ПРЕВЫШЕН" : "";
            Log.Info("Engine", $"Видеопамять: занято {vram.UsedMb} из {vram.BudgetMb} МБ бюджета{verdict}");
        }

        // Где именно уходит бюджет кадра (16.7 мс при 60 fps)
        string probe = Diagnostics.PipelineProbe.TakeReport();
        if (probe.Length > 0) Log.Info("Engine", probe);

        LogMemory();

        _lastSubmitted = s; _lastEncoded = e; _lastDropped = d; _lastDuplicated = dup;
        _lastReceived = rcv; _lastAccepted = acc;
        _lastRequests = req; _lastPacerBlocked = blocked;
        _statsWindowStart = DateTime.UtcNow;
    }

    /// <summary>
    /// Вернуть память системе после остановки.
    ///
    /// Пока идёт запись, включён SustainedLowLatency — сборщик избегает блокирующих
    /// сборок второго поколения, чтобы не давать пауз в конвейере. Плата за это:
    /// массивы кадров (каждый крупнее порога больших объектов) освобождаются, но
    /// куча под них не сжимается и системе не возвращается — процесс продолжает
    /// занимать гигабайты уже после выключения буфера.
    ///
    /// После остановки торопиться некуда: сжимаем кучу больших объектов один раз.
    /// В фоне, потому что на многогигабайтной куче это заметная пауза, а зовут нас
    /// из потока интерфейса.
    /// </summary>
    private static void ReleaseMemory() => Task.Run(() =>
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            long gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long priv = self.PrivateMemorySize64;
            static string Mb(long b) => $"{b / (1024 * 1024)} МБ";
            Log.Info("Engine", $"Память возвращена: живых объектов {Mb(GC.GetTotalMemory(false))}, " +
                               $"коммит сборщика {Mb(gcCommitted)}, нативная ~{Mb(Math.Max(0, priv - gcCommitted))}, " +
                               $"частная всего {Mb(priv)}");
            Diagnostics.MemoryMap.Log("после остановки");
        }
        catch (Exception ex) { Log.Warn("Engine", $"Сжатие кучи: {ex.Message}"); }
    });

    /// <summary>
    /// Кто занимает память. Буфер видео — это занятая часть арены, звук считается
    /// по числу блоков. Разница с памятью процесса — нативная часть: текстуры D3D,
    /// внутренние буферы Media Foundation, WPF и страницы, которые Windows ещё не
    /// забрала обратно.
    /// </summary>
    private void LogMemory()
    {
        long ring = _videoBuffer.TotalBytes;
        long audio = _audioBuffer.TotalBytes;
        long heap = GC.GetTotalMemory(false);
        long working = 0, priv = 0;
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            working = self.WorkingSet64;
            priv = self.PrivateMemorySize64;
        }
        catch { }

        // Сколько памяти держит закоммиченной сам сборщик мусора. Ключевая цифра:
        // разница между ней и частной памятью процесса — это нативная часть
        // (D3D, Media Foundation, WPF, драйвер). Без этого разделения спор
        // «кто занял гигабайт» не решается.
        long gcCommitted = GC.GetGCMemoryInfo().TotalCommittedBytes;

        static string Mb(long bytes) => $"{bytes / (1024 * 1024)} МБ";
        // Частная память — то, что процесс закоммитил и обязан освободить сам;
        // рабочий набор (его показывает диспетчер задач) система урезает по своему
        // усмотрению, поэтому судить по нему нельзя.
        Log.Info("Engine", $"Память: буфер видео {Mb(ring)}, звук {Mb(audio)}, " +
                           $"живых объектов {Mb(heap)}, коммит сборщика {Mb(gcCommitted)}, " +
                           $"нативная ~{Mb(Math.Max(0, priv - gcCommitted))}, " +
                           $"частная всего {Mb(priv)}, рабочий набор {Mb(working)}");
    }

    // ---------------- Какая игра была в буфере ----------------

    /// <summary>
    /// Игра берётся не в момент нажатия хоткея, а по тому, что было на экране,
    /// ПОКА КОПИЛСЯ БУФЕР. Иначе достаточно свернуться в Discord, вспомнить про
    /// момент и нажать — и трёхминутный клип из игры уезжает в папку «Discord».
    /// Держим отметки за длину буфера и выбираем ту игру, что занимала больше всего
    /// времени; рабочий стол засчитывается только если другого не было вовсе.
    /// </summary>
    private readonly Queue<(long Ticks, string Game)> _gameSamples = new();
    private readonly object _gameSync = new();
    private System.Threading.Timer? _gameTimer;

    private void StartGameTracker()
    {
        lock (_gameSync) _gameSamples.Clear();
        _gameTimer?.Dispose();
        _gameTimer = new System.Threading.Timer(_ =>
        {
            if (State == EngineState.Stopped) return;
            try
            {
                string game = GameDetector.DetectForegroundGame();
                long now = DateTime.UtcNow.Ticks;
                lock (_gameSync)
                {
                    _gameSamples.Enqueue((now, game));
                    long oldest = now - _videoBuffer.MaxDurationTicks;
                    while (_gameSamples.Count > 0 && _gameSamples.Peek().Ticks < oldest)
                        _gameSamples.Dequeue();
                }
            }
            catch (Exception ex)
            {
                // Молчание здесь означает клип, уехавший в папку не той игры,
                // и никаких следов, почему так вышло. Раз в 2 секунды — не спамим:
                // повторы схлопывает сам логгер.
                Log.Warn("Engine", $"Не удалось определить игру на экране: {ex.Message}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    /// <summary>Игра, под которую сохранять клип: самая частая за время буфера.</summary>
    private string GameForClip()
    {
        lock (_gameSync)
        {
            if (_gameSamples.Count == 0) return GameDetector.DetectForegroundGame();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, game) in _gameSamples)
                counts[game] = counts.GetValueOrDefault(game) + 1;

            // Рабочий стол — это «ничего не запущено», он не должен побеждать игру,
            // даже если её свернули на половину буфера.
            var best = counts
                .OrderByDescending(p => string.Equals(p.Key, "Desktop", StringComparison.OrdinalIgnoreCase) ? -1 : p.Value)
                .First();
            return best.Key;
        }
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
        lock (_lifecycle) StopLocked();
    }

    private void StopLocked()
    {
        DumpStats("на выключении");
        _gameTimer?.Dispose(); _gameTimer = null;
        _watchdog?.Dispose(); _watchdog = null;
        _statsTimer?.Dispose(); _statsTimer = null;

        // Сохранение работает с буферами и клипом в арене — снести всё это под ним
        // означает потерять клип, который пользователю уже показали как сохранённый.
        WaitForPendingSave();

        // Гасим ворота и отписываемся: с этого момента новые кадры в конвейер не идут
        _pipelineOpen = false;
        if (_capture is not null)
        {
            _capture.FrameArrived -= OnFrame;
            _capture.Failed -= OnCaptureFailed;
            // Останавливаем поток захвата, но УСТРОЙСТВО НЕ ТРОГАЕМ: им ещё
            // пользуется пейсер энкодера, см. порядок разрушения ниже
            _capture.Stop();
        }
        DrainFramesInFlight();

        // Файл записи закрываем ДО сноса конвейера и дожидаемся конца: иначе выход
        // из приложения обрывает финализацию и MP4 остаётся без moov — «сохранено,
        // а записи нет».
        if (_recorder is not null) StopRecordingToFile(wait: true);
        _audio.BlockReady -= _audioBuffer.Add;
        _audio.Stop();

        // ПОРЯДОК ВАЖЕН, и требований тут ДВА, встречных.
        //
        // 1. Поток захвата не должен работать, когда освобождают видеопроцессор и
        //    текстуры пула: он ими пользуется прямо в кадре. Поэтому выше стоит
        //    _capture.Stop() — он дожидается своего потока.
        //
        // 2. Поток пейсера внутри энкодера держит КОНТЕКСТ, взятый у захвата
        //    (VideoEncoder._context), и дублирует им кадры для постоянного fps.
        //    Значит устройство захвата нельзя освобождать, пока энкодер жив:
        //    Dispose энкодера джойнит пейсер, и только после этого контекст
        //    становится никому не нужен.
        //
        // Отсюда порядок: остановить захват → энкодер → видеопроцессор → и лишь
        // теперь уничтожить устройство захвата. Освобождение устройства первым
        // роняло процесс в CopyResource с NullReferenceException: обёртка Vortice
        // оставалась живой, а нативный указатель внутри неё уже обнулён.
        _encoder?.Dispose(); _encoder = null;
        _processor?.Dispose(); _processor = null;
        _capture?.Dispose(); _capture = null;

        _videoBuffer.Clear();
        _audioBuffer.Release();   // конвейер стоит — арену звука возвращаем системе
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
        SetState(EngineState.Stopped);
        Log.Info("Engine", "Instant Replay выключен");
        ReleaseMemory();
    }

    /// <summary>
    /// Дождаться, пока поток захвата выйдет из <see cref="OnFrame"/>.
    ///
    /// Таймаут нужен на случай, когда SubmitFrame завис на переполненной очереди
    /// энкодера: сносить конвейер силой рискованно, но зависнуть навсегда хуже.
    /// Раньше ожидания не было вовсе, так что даже с таймаутом это строго лучше.
    /// </summary>
    private void DrainFramesInFlight()
    {
        if (_frameGate.TryEnterWriteLock(TimeSpan.FromSeconds(5)))
        {
            _frameGate.ExitWriteLock();
            return;
        }
        Log.Warn("Engine", "Кадр не досчитался за 5 секунд — сношу конвейер не дожидаясь его");
    }

    /// <summary>
    /// Дождаться фонового сохранения повтора. Вызов из самого сохраняющего потока
    /// (например, через обработчик изменения настроек) не ждёт сам себя.
    /// </summary>
    private void WaitForPendingSave()
    {
        var pending = _saveTask;
        if (pending is null || pending.IsCompleted) return;
        if (Task.CurrentId is int id && id == _saveTaskId) return;

        Log.Info("Engine", "Останов ждёт, пока допишется сохраняемый клип");
        if (!pending.Wait(TimeSpan.FromSeconds(70)))
            Log.Warn("Engine", "Сохранение не уложилось в 70 секунд — останавливаюсь без него");
    }

    public void Toggle()
    {
        lock (_lifecycle)
        {
            if (_state == EngineState.Stopped) StartLocked(); else StopLocked();
        }
    }

    /// <summary>
    /// Сохранить последние N секунд (по умолчанию — вся длина буфера из настроек).
    /// Снимок буферов мгновенный, remux — в фоне. После снимка буфер сбрасывается:
    /// каждый следующий повтор начинается с чистого листа.
    /// </summary>
    public void SaveReplay(int? secondsOverride = null)
    {
        lock (_lifecycle) SaveReplayLocked(secondsOverride);
    }

    private void SaveReplayLocked(int? secondsOverride)
    {
        if (_state != EngineState.Running || _encoder?.OutputMediaType is null) return;

        DumpStats("к моменту сохранения"); // короткий сеанс тоже должен оставить следы в логе

        var s = _settings.Current;
        long wanted = TimeSpan.FromSeconds(secondsOverride ?? s.ReplayLengthSeconds).Ticks;

        // Снимок с очисткой: буфер начинает копиться заново, владение массивами
        // кадров переходит нам — вернём их в пул после записи файла.
        var video = _videoBuffer.SnapshotAndClear(wanted, out long snapshotToken);
        if (video.Count == 0) { SaveFailed?.Invoke("Буфер ещё пуст"); return; }
        var audio = _audioBuffer.Snapshot(video[0].PtsTicks, video[^1].PtsTicks);
        _audioBuffer.Clear();

        // Игра берётся по тому, что было на экране пока копился буфер (см. GameForClip)
        string game = GameForClip();
        string file = BuildFilePath(game, "replay");
        // Копия, а не ссылка на поле энкодера: запись файла переживёт остановку
        // конвейера, а Dispose энкодера освободил бы тип прямо под SinkWriter.
        var mediaType = _encoder.CloneOutputMediaType();
        if (mediaType is null) { SaveFailed?.Invoke("Энкодер ещё не отдал тип видеопотока"); return; }
        int seconds = (int)Math.Round(TimeSpan.FromTicks(video[^1].PtsTicks - video[0].PtsTicks).TotalSeconds);

        // Данные уже вырваны из кольцевого буфера и никуда не денутся — говорим об этом
        // пользователю сразу. Раньше уведомление ждало, пока сотни мегабайт доедут
        // до диска, и на длинном клипе это выглядело как «хоткей не сработал».
        SaveProgress = 0;
        ReplayCaptured?.Invoke(Math.Max(seconds, 1));

        SetState(EngineState.Saving);
        _saveTask = Task.Run(() =>
        {
            _saveTaskId = Task.CurrentId;
            // Сохранение — пакетная фоновая работа: сотни МБ копий в нативные буферы
            // MF плюс сброс на диск. На обычном приоритете она конкурирует с потоками
            // захвата и кодирования (у тех AboveNormal), и входная очередь энкодера
            // успевает переполниться: в замерах пик очереди 66 из 66 и 523 дропнутых
            // кадра ровно в минуту сохранения, при этом сам ProcessInput не тормозил.
            // Лишние полсекунды на запись файла не заметит никто, потерянные кадры — да.
            Diagnostics.MemoryMap.Log("до сохранения");

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
                ReplaySaver.Save(file, video, audio, mediaType, s.TrackMode,
                                 s.CaptureGameAudio, s.CaptureMicrophone,
                                 p => SaveProgress = p);
                _storage.RegisterSaved(file); // индекс папки — без повторного обхода диска
                // Правку счётчика делает фоновый поток — идём через Update, чтобы она
                // не столкнулась с сохранением настроек из потока интерфейса.
                //
                // ГРУППА ОБЯЗАНА ОСТАВАТЬСЯ "stats". Обработчик Changed на группы
                // video/audio/replay берёт _lifecycle и перезапускает конвейер, а
                // Stop() как раз в это время ждёт завершения ЭТОЙ задачи, держа тот же
                // замок, — получился бы дедлок. Здесь мы внутри сохраняющего потока.
                _settings.Update(x => x.TotalReplaysSaved++, "stats");
                ReplaySaved?.Invoke(file, Math.Max(seconds, 1));
            }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                // Файл записан — возвращаем буферу место, которое занимали кадры
                // клипа. Новой памяти на сохранение не тратилось вовсе: всё это
                // время клип лежал в той же арене, а запись шла в её свободную часть.
                _videoBuffer.ReleaseSnapshot(snapshotToken);
                video.Clear();
                mediaType.Dispose();                   // копия принадлежала этому сохранению
                // Писатель закрыт — сводим освободившиеся нативные блоки вместе,
                // иначе память, занятая под клип, остаётся за процессом до выхода.
                Diagnostics.MemoryMap.Log("после сохранения");
                self.Priority = previousPriority;      // поток уходит обратно в пул потоков
                SetState(_state == EngineState.Stopped ? EngineState.Stopped : EngineState.Running);
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
        // Тип видеопотока берётся не сейчас, а в момент создания файла (первый keyframe):
        // сразу после старта конвейера энкодер ещё не дописал в него заголовки кодека,
        // и файл, открытый с таким типом, не собирается на финализации.
        var recorder = new ManualRecorder(BuildFilePath(game, "recording"),
            () => _encoder?.CloneOutputMediaType(), s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone);
        _encoder.FrameEncoded += recorder.OnFrame;
        _audio.BlockReady += recorder.OnAudio;
        _recorder = recorder;
        RecordingChanged?.Invoke(true);
    }

    /// <summary>
    /// Остановить обычную запись. Возвращает путь к файлу (null — если не писали).
    /// Дозапись хвоста и финализация контейнера идут в фоне: на десятиминутном файле
    /// это заметное время, и держать на нём UI-поток нельзя. Событие (RecordingSaved
    /// или SaveFailed) приходит, когда файл реально закрыт.
    /// </summary>
    public string? StopRecordingToFile(bool wait = false)
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        _recorder = null;
        if (_encoder is not null) _encoder.FrameEncoded -= recorder.OnFrame;
        _audio.BlockReady -= recorder.OnAudio;
        RecordingChanged?.Invoke(false);

        var finish = Task.Run(() =>
        {
            var result = recorder.Finish();
            foreach (var file in result.Files) _storage.RegisterSaved(file);
            if (result.Ok) RecordingSaved?.Invoke(result.Files[0], Math.Max(result.Seconds, 1));
            else SaveFailed?.Invoke(result.Error ?? "запись не закрылась");
        });
        if (wait) finish.Wait(TimeSpan.FromSeconds(70));
        return recorder.FilePath;
    }

    private void SetState(EngineState st)
    {
        _state = st;
        StateChanged?.Invoke(st);
    }

    public void Dispose()
    {
        Stop();
        _audio.Dispose();
        _frameGate.Dispose();
    }
}
