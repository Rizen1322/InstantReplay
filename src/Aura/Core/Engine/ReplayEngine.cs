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

public enum EngineState { Stopped, Running, Saving, Recovering }

/// <summary>
/// Главный движок Instant Replay: конвейер
/// WGC (BGRA, VRAM) → VideoProcessor (NV12, VRAM) → HW MFT (NVENC/AMF/QSV)
/// → кольцевой RAM-буфер сжатых кадров; параллельно AudioMixer → аудиобуфер.
/// SaveReplay() делает мгновенный снимок буферов и в фонеремуксит в MP4.
/// </summary>
public sealed partial class ReplayEngine : IDisposable
{
    private static readonly TimeSpan ScreenshotFreshFrameWait = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Предельный возраст кадра, который скриншот согласен взять у буфера.
    ///
    /// Полсекунды — граница, за которой картинка перестаёт быть «тем, что на экране».
    /// Выше — уже другое содержимое: человек успел переключить окно, закрыть меню,
    /// свернуть игру. Такой кадр отдавать нельзя, лучше открыть свою сессию захвата.
    /// </summary>
    private static readonly TimeSpan ScreenshotMaxFrameAge = TimeSpan.FromMilliseconds(500);

    private readonly SettingsManager _settings;
    private readonly StorageManager _storage;

    private IScreenCapture? _capture;
    private VideoProcessorNv12? _processor;

    /// <summary>Время стадий по часам видеокарты. Диагностика, на запись не влияет.</summary>
    private Diagnostics.GpuStageTimer? _gpuTimer;

    /// <summary>
    /// Монитор и его размер, под которые собран текущий конвейер. По ним смена
    /// набора дисплеев отличает «наш экран пропал или сменился» от «что-то поменялось
    /// где-то ещё».
    /// </summary>
    private int _pipelineMonitorIndex;
    private (int Width, int Height)? _pipelineCanvas;
    private VideoEncoder? _encoder;
    private GpuCaptureFrameBroker? _frameBroker;
    private readonly AudioMixerEngine _audio = new();
    private readonly CaptureHealthPolicy _captureHealth = new();
    private readonly GameCaptureRecoveryCoordinator _gameCaptureRecovery;
    private CaptureBackend _captureBackend;
    private readonly CaptureBackend _preferredCaptureBackend;
    private readonly bool _captureBackendForced;
    private long _captureGeneration;
    private Action<CapturedSurface>? _captureFrameHandler;
    private Action<CaptureFailure>? _captureFailureHandler;
    private CancellationTokenSource? _recoveryCancellation;
    private DateTimeOffset _pipelineStartedAt;
    private VideoCodec _bufferCodec;
    private int _bufferWidth, _bufferHeight, _bufferFps;
    private byte[]? _bufferSequenceHeader;
    private volatile bool _encodedStreamReady;
    private readonly object _captureTargetSync = new();
    private GameCaptureTarget? _lastVerifiedCaptureTarget;
    private GameCaptureTarget? _activeCaptureTarget;
    private long _ddaStormCount;
    private long _windowRetryCount;
    private long _windowHoldStartedTimestamp;

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
    /// Ворота «кадров в полёте». Обработчик кадра держит читательский замок на
    /// время работы с D3D, снос конвейера берёт писательский — и тем самым ждёт,
    /// пока текущий кадр досчитается.
    ///
    /// Без этого Stop() освобождал текстуры и контекст прямо под работающим
    /// Convert/SubmitFrame. Такое падение — access violation внутри драйвера,
    /// его не ловит ни try/catch в OnFrame, ни глобальный обработчик.
    /// </summary>
    private readonly ReaderWriterLockSlim _frameGate = new(LockRecursionPolicy.NoRecursion);
    private AutoResetEvent? _frameReady;
    private Thread? _frameWorker;
    private FrameWorkerToken? _frameWorkerToken;

    /// <summary>Конвейер собран и кадры можно обрабатывать. Гасится первым при сносе.</summary>
    private volatile bool _pipelineOpen;

    /// <summary>
    /// Остановку попросили снаружи — восстанавливать конвейер больше нельзя.
    ///
    /// ЗАЧЕМ. Задача восстановления после потери устройства делает до десяти
    /// попыток Start с паузами, то есть живёт около полуминуты. Если за это время
    /// человек выключил запись, она включала её обратно. Тот же флаг проверяют
    /// таймеры: Timer.Dispose не дожидается уже начатого колбэка, а SetState(Stopped)
    /// стоит в самом конце сноса — то есть проверки «State == Stopped» им мало.
    /// </summary>
    private volatile bool _stopRequested;

    /// <summary>Идущее сохранение — Stop() обязан его дождаться, а не сносить буферы под ним.</summary>
    private Task? _saveTask;
    private int? _saveTaskId;
    private readonly object _writerTasksSync = new();
    private readonly HashSet<Task> _writerTasks = [];
    private readonly object _pathSync = new();
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);

    private volatile EngineState _state = EngineState.Stopped;
    private readonly object _stateSync = new();
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
    public CaptureBackend ActiveCaptureBackend => _captureBackend;
    /// <summary>Живые пиковые уровни аудио (0..1) — для индикаторов.</summary>
    public (float Game, float Mic) AudioLevels => (_audio.GamePeak, _audio.MicPeak);

    /// <summary>Счётчики конвейера для панели «Обзор»: сколько кадров прошло каждую стадию.</summary>
    public (long Received, long Accepted, long Encoded, long Dropped, long Duplicated) FrameCounters =>
        (_capture?.FramesReceived ?? 0, _capture?.FramesAccepted ?? 0,
         _encoder?.FramesEncoded ?? 0, _encoder?.FramesDroppedRealQueue ?? 0, _encoder?.FramesDuplicated ?? 0);

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
    public bool TryUseLiveFrame(UseFrame use, bool allowStale) =>
        TryUseLiveFrame(use, withoutCursor: false, allowStale);

    /// <summary>Живой кадр без дорисованного курсора — для оверлея выделения области.</summary>
    public bool TryUseLiveFrameWithoutCursor(UseFrame use, bool allowStale) =>
        TryUseLiveFrame(use, withoutCursor: true, allowStale);

    private bool TryUseLiveFrame(UseFrame use, bool withoutCursor, bool allowStale)
    {
        // Те же ворота, что и у OnFrame: скриншот берёт кадр с живого устройства
        // захвата, и Stop() не должен освободить это устройство прямо во время чтения.
        // Причина отказа уходит в лог. Раньше false возвращался молча, и по логу
        // нельзя было отличить «конвейер пересобирается» от «все слоты кадров заняты».
        if (!_frameGate.TryEnterReadLock(TimeSpan.FromMilliseconds(50)))
        {
            Log.Info("Screenshot", "Живой кадр не отдан: конвейер пересобирается");
            return false;
        }
        try
        {
            var cap = _capture;
            var broker = _frameBroker;
            long generation = Interlocked.Read(ref _captureGeneration);
            if (cap is null || broker is null || !_pipelineOpen)
            {
                Log.Info("Screenshot", "Живой кадр не отдан: конвейер не запущен");
                return false;
            }
            bool used = broker.TryUseFreshestMonitor(
                generation,
                ScreenshotFreshFrameWait,
                allowStale ? TimeSpan.MaxValue : ScreenshotMaxFrameAge,
                texture => use(cap.D3DDevice, cap.D3DContext, texture),
                withoutCursor,
                out string rejection);
            if (!used)
                Log.Info("Screenshot", $"Живой кадр не отдан: {rejection} (источник {_captureBackend})");
            return used;
        }
        finally { _frameGate.ExitReadLock(); }
    }

    // ---- Обычная запись в файл («Начать запись») ----
    private ManualRecorder? _recorder;
    // Это именно желание человека, а не факт наличия текущего writer.
    // Во время recovery writer закрывается, но intent остаётся true; явная
    // команда «остановить» меняет его даже в промежутке между сегментами.
    private bool _continuousRecordingRequested;
    public bool IsRecordingToFile => _recorder is not null;
    /// <summary>Сколько идёт обычная запись; null — не пишем.</summary>
    public TimeSpan? RecordingElapsed => _recorder is { } r ? r.Elapsed.Elapsed : null;
    /// <summary>Номер текущей части файла (файл режется по достижении предела MP4).</summary>
    public int RecordingPart => _recorder?.PartCount ?? 0;
    public event Action<bool>? RecordingChanged;
    /// <summary>Успешное завершение обычной записи: путь + длительность (сек).</summary>
    public event Action<string, int>? RecordingSaved;

    public ReplayEngine(SettingsManager settings, StorageManager storage)
    {
        _settings = settings;
        _storage = storage;
        var selection = ScreenCaptureFactory.Selection;
        _captureBackend = selection.Backend;
        _preferredCaptureBackend = selection.Backend;
        _captureBackendForced = selection.Forced;
        _gameCaptureRecovery = new GameCaptureRecoveryCoordinator(
            _preferredCaptureBackend,
            _captureBackendForced,
            ScreenCaptureFactory.WgcAllowed);
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
                lock (_lifecycle) { StopLocked(); StartWithFallbackLocked(preserveBuffers: false); }
            }
        };
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            // Человек включил повтор сам — значит спор о том, надо ли писать,
            // решён в его пользу, и накопленные системные причины паузы больше не
            // действуют. Без этой строки оставшаяся причина держала бы
            // _suspendedBySystem поднятым при работающем конвейере, и следующая
            // пауза не сработала бы вовсе: она выходит сразу, если считает, что уже
            // приостановила нас.
            ForgetSystemSuspend();
            StartWithFallbackLocked(preserveBuffers: false);
        }
    }

    private void StartWithFallbackLocked(bool preserveBuffers)
    {
        if (_state != EngineState.Stopped) return;
        if (!preserveBuffers)
        {
            _gameCaptureRecovery.Resume();
            Interlocked.Exchange(ref _ddaStormCount, 0);
            Interlocked.Exchange(ref _windowRetryCount, 0);
            Interlocked.Exchange(ref _windowHoldStartedTimestamp, 0);
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CaptureBackend desired = preserveBuffers ? _captureBackend : _preferredCaptureBackend;
        GameCaptureTarget? desiredTarget = preserveBuffers ? ActiveCaptureTarget() : RefreshCaptureTarget();
        if (!preserveBuffers)
        {
            CaptureBackendTargetSelection proactive = CaptureBackendPolicy.SelectForForeground(
                desired,
                activeTarget: null,
                desiredTarget,
                _captureBackendForced);
            desired = proactive.Backend;
            desiredTarget = proactive.Target;
        }
        CaptureBackend candidate = _captureHealth.CanUse(desired, now)
            ? desired
            : MonitorFallbackFor(desired);

        try
        {
            StartLocked(
                preserveBuffers,
                candidate,
                candidate is CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl
                    ? desiredTarget
                    : null);
        }
        catch when (!_captureBackendForced)
        {
            _captureHealth.Quarantine(candidate, now, CaptureQuarantine.Transient);
            CaptureBackend alternative = MonitorFallbackFor(candidate);
            if (alternative == candidate ||
                !_captureHealth.CanUse(alternative, now) ||
                !_captureHealth.TryRecordSwitch(now)) throw;

            Log.Warn("Engine", $"Захват {candidate} не запустился — пробую {alternative}");
            // Неудачный Start очистил частично собранный pipeline как UserStop и
            // остановил coordinator. Это внутренний fallback, а не команда человека.
            _stopRequested = false;
            _gameCaptureRecovery.Resume();
            StartLocked(preserveBuffers, alternative);
        }
    }

    /// <summary>Про какой монитор уже сказали про HDR — повторять на каждом рестарте незачем.</summary>
    private int _hdrWarnedMonitor = -1;

    /// <summary>
    /// Сказать человеку, что монитор в HDR, а запись будет выцветшей.
    ///
    /// ЗАЧЕМ ГОВОРИТЬ, А НЕ ЧИНИТЬ НА МЕСТЕ. Весь тракт захвата восьмибитный, и
    /// система свёртывает HDR-рабочий стол в восемь бит без тональной компрессии.
    /// Настоящее лечение — свой HDR-тракт, это отдельная работа. Но человек, увидев
    /// блёклую запись, идёт крутить битрейт и кодек, где причины нет вовсе; одна
    /// строка в уведомлении экономит ему этот вечер.
    ///
    /// Говорим один раз на монитор: конвейер пересобирается при каждой смене режима
    /// экрана, и повторять это на каждый Alt-Tab нельзя.
    /// </summary>
    private void WarnIfMonitorIsHdr(int monitorIndex)
    {
        if (MonitorColorSpace.Detect(monitorIndex) is not { } state) return;

        Log.Info("Capture", $"Монитор #{monitorIndex}: {state.Description}");
        if (!state.Hdr || _hdrWarnedMonitor == monitorIndex) return;

        _hdrWarnedMonitor = monitorIndex;
        Log.Warn("Capture", "Монитор работает в HDR — запись будет бледнее картинки на экране");
        Warning?.Invoke("На мониторе включён HDR. Запись выйдет бледной и тёмной: " +
                        "захват идёт в восьми битах. Отключите HDR в Windows на время записи.");
    }

    private CaptureBackend MonitorFallbackFor(CaptureBackend backend) =>
        backend == CaptureBackend.MinecraftOpenGl
            ? _preferredCaptureBackend
            // На Windows 10 запасного мониторного источника нет: WGC дал бы рамку.
            : !ScreenCaptureFactory.WgcAllowed
            ? CaptureBackend.DesktopDuplication
            : CaptureBackendPolicy.Alternative(backend);

    private void StartLocked(
        bool preserveBuffers,
        CaptureBackend backend,
        GameCaptureTarget? requestedTarget = null)
    {
        if (_state != EngineState.Stopped && !(preserveBuffers && _state == EngineState.Recovering))
            throw new InvalidOperationException("Видеоконвейер ещё не остановлен");
        if (preserveBuffers && _stopRequested)
            throw new OperationCanceledException("Автовосстановление отменено человеком");
        if (!preserveBuffers) _stopRequested = false;
        var s = _settings.Current;
        try
        {
            GameCaptureTarget? sourceTarget = null;
            if (backend is CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl)
            {
                GameCaptureTarget target = requestedTarget ?? _gameCaptureRecovery.Target ??
                    throw new InvalidOperationException("Для игрового capture нет проверенного окна");
                GameCaptureTarget? verified = ForegroundGameWindowProbe.TrySelect(
                    s.MonitorIndex,
                    target);
                if (verified is not GameCaptureTarget current ||
                    !current.HasSameIdentity(target) ||
                    current.Revision != target.Revision)
                {
                    throw new InvalidOperationException("Игровое окно изменилось перед запуском capture");
                }
                if (backend == CaptureBackend.MinecraftOpenGl &&
                    !CaptureBackendPolicy.IsMinecraftOpenGlTarget(current))
                    throw new InvalidOperationException("OpenGL capture разрешён только для Minecraft javaw");
                sourceTarget = current;
                _gameCaptureRecovery.ObserveTarget(current);
            }

            _captureBackend = backend;
            int effectiveReplaySeconds = Math.Min(
                s.ReplayLengthSeconds,
                ReplayVideoBuffer.MaximumDurationSeconds(s.BitrateBps));
            if (effectiveReplaySeconds < s.ReplayLengthSeconds)
            {
                Log.Warn("Engine", $"Повтор {s.ReplayLengthSeconds} с не помещается в арену при " +
                                   $"{s.BitrateMbps} Мбит/с — ограничен до {effectiveReplaySeconds} с");
                Warning?.Invoke($"Длина повтора ограничена до {TimeSpan.FromSeconds(effectiveReplaySeconds):m\\:ss} из-за объёма RAM");
            }

            _videoBuffer.MaxDurationTicks = TimeSpan.FromSeconds(effectiveReplaySeconds).Ticks;
            _audioBuffer.MaxDurationTicks = _videoBuffer.MaxDurationTicks;
            // Арена под кадры: размер считается из длительности и битрейта, дальше
            // память не растёт — сколько выделено, столько буфер и занимает.
            // Своя арена под звук, по той же причине: раньше микшер выделял пару
            // массивов каждые 10 мс, и буфер держал их все живыми.
            bool captureAudio = s.CaptureGameAudio || s.CaptureMicrophone;
            bool validateSequenceHeader = false;
            byte[]? previousSequenceHeader = null;
            if (!preserveBuffers)
            {
                _videoBuffer.Allocate(s.BitrateBps, effectiveReplaySeconds);
                _bufferSequenceHeader = null;
                if (captureAudio)
                    _audioBuffer.Allocate(
                        Audio.AudioMixerEngine.BlockSamples,
                        effectiveReplaySeconds,
                        s.CaptureMicrophone,
                        s.CaptureGameAudio);
                else
                    _audioBuffer.Release();
            }

            long generation = Interlocked.Increment(ref _captureGeneration);
            _capture = ScreenCaptureFactory.Create(CaptureSourceRequest.Create(
                _captureBackend,
                s.MonitorIndex,
                sourceTarget));
            _capture.Prepare(s.MonitorIndex, s.Fps, s.RecordCursor, generation);

            lock (_captureTargetSync) _activeCaptureTarget = sourceTarget;

            WarnIfMonitorIsHdr(s.MonitorIndex);

            var monitorCanvas = MonitorLayout.For(s.MonitorIndex);
            int canvasWidth = monitorCanvas?.Width ?? _capture.Width;
            _pipelineMonitorIndex = s.MonitorIndex;
            _pipelineCanvas = monitorCanvas is { } mc ? (mc.Width, mc.Height) : null;
            int canvasHeight = monitorCanvas?.Height ?? _capture.Height;

            // Размер записи фиксируется по разрешению рабочего стола, а не по текущему
            // режиму экрана. Иначе игра, переключающая экран на своё разрешение, меняла
            // формат видео при каждом сворачивании, и буфер повтора очищался. Кадр
            // другой пропорции растягивается на весь выход.
            var outputBase = MonitorLayout.DesktopModeFor(s.MonitorIndex);

            // Десять бит просим только у HEVC: см. AppSettings.BitDepth.
            bool wantTenBit = s.Codec == VideoCodec.HEVC &&
                              s.BitDepth is VideoBitDepth.Auto or VideoBitDepth.Ten;

            _gpuTimer?.Dispose();
            _gpuTimer = new Diagnostics.GpuStageTimer(_capture.D3DDevice);

            _processor = new VideoProcessorNv12(_capture.D3DDevice, _capture.D3DContext);
            try
            {
                _processor.Configure(canvasWidth, canvasHeight, s.VerticalResolution, s.Fps, wantTenBit,
                                     outputBase);
            }
            catch (Exception ex) when (wantTenBit)
            {
                // Последняя страховка десяти бит. Всё, что можно было спросить у
                // драйвера, мы спросили, но отказать он вправе и на любом другом
                // шаге. Запись важнее глубины цвета, поэтому молча берём восемь бит
                // вместо того, чтобы не включиться вовсе.
                Log.Warn("Capture", $"Десять бит не настроились ({ex.Message}) — беру восемь");
                _processor.Configure(canvasWidth, canvasHeight, s.VerticalResolution, s.Fps,
                                     preferTenBit: false, outputBase);
            }

            _frameBroker = new GpuCaptureFrameBroker(
                _capture.D3DDevice,
                _capture.D3DContext,
                canvasWidth,
                canvasHeight,
                separateCursor: _captureBackend is
                    CaptureBackend.DesktopDuplication or
                    CaptureBackend.WgcWindow or
                    CaptureBackend.MinecraftOpenGl,
                generation,
                targetRevision: sourceTarget?.Revision ?? 0,
                windowEpisode: _captureBackend == CaptureBackend.WgcWindow,
                hybridEpisode: _captureBackend == CaptureBackend.MinecraftOpenGl);

            if (preserveBuffers)
            {
                bool compatible = _bufferCodec == s.Codec &&
                                  _bufferWidth == _processor.OutWidth &&
                                  _bufferHeight == _processor.OutHeight &&
                                  _bufferFps == s.Fps;
                if (!_videoBuffer.PrepareForCaptureRestart(compatible, s.BitrateBps, effectiveReplaySeconds))
                {
                    _bufferSequenceHeader = null;
                    Log.Warn("Engine", "Формат видео изменился — несовместимая часть RAM-буфера очищена");
                }
                else if (_videoBuffer.TotalBytes > 0)
                {
                    validateSequenceHeader = true;
                    previousSequenceHeader = _bufferSequenceHeader;
                }
            }

            var encoder = _encoder = new VideoEncoder();
            encoder.Initialize(_capture.D3DDevice, _processor.OutWidth, _processor.OutHeight,
                               s.Fps, s.BitrateBps, s.Codec, _processor.TenBit);

            // Видеопроцессор согласился писать десять бит, а энкодер их не принял.
            // Форматы обязаны совпадать, поэтому переводим процессор обратно на NV12.
            // Случай редкий, но молча отдавать P010 в восьмибитный вход нельзя.
            if (_processor.TenBit && !encoder.TenBit)
            {
                Log.Info("Capture", "Возвращаю видеопроцессор на восемь бит вслед за энкодером");
                _processor.Configure(canvasWidth, canvasHeight, s.VerticalResolution, s.Fps,
                                     preferTenBit: false, outputBase);
            }
            bool awaitingRestartKeyframe = validateSequenceHeader;
            _encodedStreamReady = !awaitingRestartKeyframe;
            encoder.FrameEncoded += frame =>
            {
                // До первого keyframe нового MFT кадры не добавляем: их
                // нельзя декодировать от старого GOP. На keyframe уже доступен
                // sequence header и можно безопасно решить, сохранять ли хвост.
                if (awaitingRestartKeyframe)
                {
                    if (!frame.IsKeyframe) return;
                    byte[]? currentSequenceHeader = encoder.TryGetSequenceHeader();
                    if (!EncodedStreamCompatibility.SameSequenceHeader(
                            previousSequenceHeader, currentSequenceHeader))
                    {
                        _videoBuffer.Clear();
                        Log.Warn("Engine", "Параметры новой сессии энкодера отличаются — " +
                                           "старая видеочасть RAM-буфера очищена");
                    }
                    _bufferSequenceHeader = currentSequenceHeader;
                    awaitingRestartKeyframe = false;
                    _encodedStreamReady = true;
                }
                else if (frame.IsKeyframe)
                {
                    _bufferSequenceHeader = encoder.TryGetSequenceHeader();
                }
                _videoBuffer.Add(frame);
            };
            _bufferCodec = s.Codec;
            _bufferWidth = _processor.OutWidth;
            _bufferHeight = _processor.OutHeight;
            _bufferFps = s.Fps;

            // Сначала подписываем полностью готовый конвейер и только потом запускаем
            // источник: так не теряется единственный первый кадр статичного DDA-экрана.
            _captureFrameHandler = frame => OnCapturedSurface(frame, generation);
            _captureFailureHandler = failure => OnCaptureFailed(failure, generation);
            _capture.FrameArrived += _captureFrameHandler;
            _capture.Failed += _captureFailureHandler;
            _pipelineOpen = true;
            StartFrameWorker(generation);
            _capture.Start();
            // Источник сам не оживёт после потери устройства — пересобираем конвейер.
            // Для WGC это единственный путь: там кадры приходят в колбэк WinRT, из
            // которого исключение не выпустить, не уронив процесс.
            _pipelineStartedAt = DateTimeOffset.UtcNow;

            _audio.MicNoiseGate = s.MicNoiseSuppression;
            _audio.MicGateThresholdDb = s.MicNoiseGateDb;
            if (captureAudio && !preserveBuffers)
            {
                _audio.BlockReady += _audioBuffer.Add;
                _audio.Start(s.CaptureGameAudio, s.CaptureMicrophone, s.RenderDeviceId, s.CaptureDeviceId);
            }

            // Меньше блокирующих GC-пауз, пока идёт запись
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

            SetState(EngineState.Running);
            StartStatsTimer();
            StartCaptureWatchdog();
            StartCaptureProbe();
            StartGameTracker(preserveHistory: preserveBuffers);
            Log.Info("Engine", "Instant Replay включен");
        }
        catch (Exception ex)
        {
            Log.Error("Engine", ex);
            StopLocked(preserveBuffers ? PipelineStopIntent.CaptureRestart : PipelineStopIntent.UserStop);
            throw;
        }
    }

    private void OnCapturedSurface(in CapturedSurface surface, long observedGeneration)
    {
        if (surface.Generation != observedGeneration ||
            observedGeneration != Interlocked.Read(ref _captureGeneration)) return;
        // Идёт снос конвейера — кадр уже некуда девать. Ждать нельзя: поток захвата
        // заблокировал бы сам снос, которого он дожидается.
        if (!_frameGate.TryEnterReadLock(0)) return;
        try
        {
            if (!_pipelineOpen || _frameBroker is null) return;
            long copyStart = Diagnostics.PipelineProbe.Now();
            if (!_frameBroker.Publish(surface)) return;
            Diagnostics.PipelineProbe.CaptureCopy.Add(
                copyStart, Diagnostics.PipelineProbe.Now());
            _frameReady?.Set();
        }
        catch (Exception ex)
        {
            // Сброс/пропажа устройства — единственная ошибка кадра, из которой конвейер
            // сам не выберется: все объекты D3D мертвы, нужно пересобирать с нуля.
            if (DeviceLoss.IsDeviceLost(ex))
            {
                OnCaptureFailed(new CaptureFailure(
                    CaptureFailureKind.DeviceLost, ex, $"ошибка кадра: {ex.Message}",
                    observedGeneration, surface.TargetRevision), observedGeneration);
                return;
            }
            Log.Error("Engine", $"Кадр пропущен: {ex.Message}");
        }
        finally { _frameGate.ExitReadLock(); }
    }

    private sealed class FrameWorkerToken
    {
        public volatile bool Running = true;
    }

    private void StartFrameWorker(long generation)
    {
        if (_frameWorker is not null)
            throw new InvalidOperationException("Обработчик кадров уже запущен");

        _frameReady = new AutoResetEvent(false);
        var token = new FrameWorkerToken();
        _frameWorkerToken = token;
        _frameWorker = new Thread(() => FrameWorkerLoop(token, generation))
        {
            IsBackground = true,
            Name = "AuraFrameWorker",
            Priority = ThreadPriority.AboveNormal
        };
        _frameWorker.Start();
    }

    private void FrameWorkerLoop(FrameWorkerToken token, long generation)
    {
        while (token.Running)
        {
            AutoResetEvent? ready = _frameReady;
            if (ready is null || !ready.WaitOne(100)) continue;
            if (!token.Running) break;
            ProcessLatestFrame(generation);
        }
    }

    private void ProcessLatestFrame(long generation)
    {
        if (generation != Interlocked.Read(ref _captureGeneration) ||
            !_frameGate.TryEnterReadLock(0)) return;

        try
        {
            if (!_pipelineOpen || _frameBroker is null ||
                !_frameBroker.TryLeaseLatest(generation, out GpuCaptureFrameLease? lease)) return;

            GpuCaptureFrameLease current = lease!;
            using (current)
            {
                // Очередь энкодера забита — кадр всё равно вытеснит другой такой же.
                // Тратить на него видеокарту, которая и так не справляется, незачем:
                // см. VideoEncoder.InputQueueSaturated.
                if (_encoder is { } saturatedCheck && saturatedCheck.InputQueueSaturated)
                {
                    Interlocked.Increment(ref saturatedCheck.FramesSkippedBeforeConvert);
                    return;
                }

                // Метки видеокарты ставятся вокруг тех же двух стадий, что и
                // секундомер потока. Сравнение этих двух цифр и есть весь смысл:
                // процессор здесь только ставит команды в очередь.
                var context = _capture!.D3DContext;
                bool timed = _gpuTimer?.BeginFrame(context) == true;

                long t0 = Diagnostics.PipelineProbe.Now();
                var nv12 = _processor!.Convert(current.Texture);
                long t1 = Diagnostics.PipelineProbe.Now();
                if (timed) _gpuTimer!.Mark(context);

                _encoder!.SubmitFrame(nv12, current.Timestamp, context);
                if (timed) { _gpuTimer!.Mark(context); _gpuTimer.EndFrame(context); }

                Diagnostics.PipelineProbe.Convert.Add(t0, t1);
                Diagnostics.PipelineProbe.Submit.Add(t1, Diagnostics.PipelineProbe.Now());
            }
        }
        catch (Exception ex)
        {
            if (DeviceLoss.IsDeviceLost(ex))
            {
                OnCaptureFailed(new CaptureFailure(
                    CaptureFailureKind.DeviceLost, ex, $"ошибка кадра: {ex.Message}",
                    generation, ActiveCaptureTarget()?.Revision ?? 0), generation);
            }
            else
            {
                Log.Error("Engine", $"Кадр пропущен: {ex.Message}");
            }
        }
        finally
        {
            _frameGate.ExitReadLock();
        }
    }

    private void StopFrameWorker()
    {
        FrameWorkerToken? token = _frameWorkerToken;
        _frameWorkerToken = null;
        if (token is not null) token.Running = false;
        _frameReady?.Set();

        Thread? worker = _frameWorker;
        _frameWorker = null;
        bool stopped = worker is null || worker.Join(5000);
        if (!stopped)
        {
            Log.Warn("Engine", "Обработчик кадра не завершился за 5 секунд");
            return;
        }

        _frameReady?.Dispose();
        _frameReady = null;
    }

    /// <summary>Источник остановился или подтверждённо голодает.</summary>
    private void OnCaptureFailed(CaptureFailure failure, long observedGeneration)
    {
        if (failure.Generation != observedGeneration ||
            observedGeneration != Interlocked.Read(ref _captureGeneration) ||
            _stopRequested) return;
        if (failure.TargetRevision != 0)
        {
            GameCaptureTarget? active = ActiveCaptureTarget();
            if (active is not GameCaptureTarget target ||
                target.Revision != failure.TargetRevision)
                return;
        }
        // Источник, probe и watchdog могут одновременно увидеть один и тот же
        // обрыв. Забираем право на восстановление ДО изменения health-policy,
        // иначе дубль зря карантинил backend и съедал второй switch-slot.
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
        if (failure.Kind == CaptureFailureKind.BackendTransitionStorm)
            Interlocked.Increment(ref _ddaStormCount);

        try
        {
            // Между первой проверкой и CompareExchange человек мог успеть
            // выключить replay. В таком случае ничего не перезапускаем.
            if (observedGeneration != Interlocked.Read(ref _captureGeneration) || _stopRequested)
            {
                Interlocked.Exchange(ref _recovering, 0);
                return;
            }

            RefreshCaptureTarget();
            if (!_gameCaptureRecovery.TryDecide(
                    _captureBackend,
                    failure.Kind,
                    out CaptureRecoveryDecision decision))
            {
                Interlocked.Exchange(ref _recovering, 0);
                return;
            }

            RequestCaptureRestart(
                decision,
                $"{failure.Reason}: {failure.Error.Message}",
                failure.Kind,
                observedGeneration);
        }
        catch
        {
            Interlocked.Exchange(ref _recovering, 0);
            throw;
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        _recoveryCancellation?.Cancel();
        lock (_lifecycle)
        {
            _continuousRecordingRequested = false;
            // Выключил человек — системной паузе тут больше нечего держать.
            ForgetSystemSuspend();
            StopLocked();
        }
    }

    /// <summary>
    /// Повторить попытку паузы, когда сохранение закончится.
    ///
    /// Повторов не накопится: причина попадает в множество один раз, а попытка
    /// выходит сразу, если причину уже сняли.
    /// </summary>
    private void ScheduleSuspendRetry(string reason)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            if (_stopRequested) return;
            lock (_lifecycle)
                if (!_systemSuspendReasons.Contains(reason)) return;   // причину уже сняли
            SuspendForSystem(reason);
        }, TaskScheduler.Default);
    }

    /// <summary>Забыть, что конвейер стоит по системной причине.</summary>
    private void ForgetSystemSuspend()
    {
        _systemSuspendReasons.Clear();
        _suspendedBySystem = false;
    }

    /// <summary>
    /// Контейнер MP4 не принял десятибитный поток — переводим настройку на восемь
    /// бит навсегда.
    ///
    /// ЗАЧЕМ. Готовность энкодера принять P010 мы проверяем при запуске, а вот
    /// согласие MP4-мультиплексора Windows собрать из этого файл выясняется только
    /// на финализации, то есть уже после того, как клип записан. Проверить это
    /// заранее можно было бы только пробной записью на каждом запуске, и цена такой
    /// проверки выше выигрыша.
    ///
    /// Поэтому ловим отказ один раз и запоминаем его. Пользователь теряет один клип
    /// вместо каждого следующего, и делать ему ничего не нужно.
    /// </summary>
    private void FallBackToEightBitIfContainerRefused(Exception error)
    {
        if (error is not NotSupportedException) return;
        if (_encoder is not { TenBit: true }) return;
        if (_settings.Current.BitDepth == VideoBitDepth.Eight) return;

        // ГРУППА ОБЯЗАНА БЫТЬ "stats". Обработчик Changed на группу video берёт
        // _lifecycle и перезапускает конвейер, а мы находимся в потоке сохранения,
        // завершения которого этот же перезапуск дожидается под тем же замком.
        // Получился бы дедлок. Новая глубина цвета применится при следующем старте
        // конвейера, и этого достаточно.
        _settings.Update(x => x.BitDepth = VideoBitDepth.Eight, "stats");
        Log.Warn("Engine", "MP4 не принял десятибитный поток — запись переведена на восемь бит");
        Warning?.Invoke("Windows не собрала MP4 из десятибитной записи. " +
                        "Глубина цвета переключена на восемь бит, следующие клипы сохранятся.");
    }

    /// <summary>
    /// Набор дисплеев изменился — пересобрать захват, не дожидаясь сторожа тишины.
    ///
    /// ЗАЧЕМ. Монитор, выключенный собственной кнопкой, исчезает с шины, и события
    /// питания при этом нет. Захват просто перестаёт отдавать кадры. Сторож тишины
    /// это заметит, но только по истечении своего порога, и всё это время буфер
    /// копит пустоту. Здесь мы знаем причину сразу и пересобираем конвейер по тому
    /// же пути, что и при зависании источника.
    ///
    /// Ничего не делаем, если конвейер и так стоит: событие приходит и при обычной
    /// смене разрешения, когда повтор выключен.
    /// </summary>
    public void RebuildAfterDisplayChange(string reason)
    {
        if (!_pipelineOpen || _stopRequested || _state != EngineState.Running) return;

        // Desktop Duplication чинит себя сам: смена режима экрана приходит к нему
        // как ACCESS_LOST, и дупликация пересоздаётся на лету без остановки записи.
        // Пересобирать поверх этого весь конвейер значило чистить буфер повтора на
        // каждой смене режима. В логе у пользователя с игрой в растянутом 4:3 это
        // выглядело как generation 43: игра переключала рабочий стол между 1280x1024
        // и 1920x1080, каждое переключение давало событие, каждое событие — пересборку
        // и сброс буфера, и сама пересборка успевала попасть под следующее событие.
        if (_captureBackend == CaptureBackend.DesktopDuplication) return;

        // WGC ломается, только если монитор, который мы снимаем, пропал или сменил
        // размер. Любая другая перестройка дисплеев — второй монитор, масштаб на
        // соседнем экране — захвату не мешает, и трогать работающий конвейер незачем.
        var now = MonitorLayout.For(_pipelineMonitorIndex);
        if (now is { } m && _pipelineCanvas is { } was && m.Width == was.Width && m.Height == was.Height)
            return;

        long generation = Interlocked.Read(ref _captureGeneration);
        Log.Info("Engine", $"{reason} — пересобираю захват");
        OnCaptureFailed(new CaptureFailure(
            CaptureFailureKind.CaptureFormatChanged,
            new InvalidOperationException(reason),
            "пересборка захвата",
            generation,
            ActiveCaptureTarget()?.Revision ?? 0), generation);
    }

    // ---------------- Пауза на время, когда экран смотреть некому ----------------

    /// <summary>
    /// Причины, по которым конвейер сейчас на паузе. Их может быть несколько сразу:
    /// система гасит экран и запирает сеанс почти одновременно, а событий прихода и
    /// ухода одинаковое число не гарантирует никто. Поэтому держим множество, а не
    /// счётчик: повторное «экран погас» не удвоит вес причины, а лишнее «экран
    /// включился» не снимет паузу, которую держит блокировка.
    /// </summary>
    private readonly HashSet<string> _systemSuspendReasons = new(StringComparer.Ordinal);

    /// <summary>Мы ли остановили конвейер. Чужую остановку возобновлять не наше дело.</summary>
    private bool _suspendedBySystem;

    /// <summary>Идёт ли сейчас системная пауза — для интерфейса и диагностики.</summary>
    public bool SuspendedBySystem { get { lock (_lifecycle) return _suspendedBySystem; } }

    /// <summary>
    /// Остановить конвейер, пока длится системное событие.
    ///
    /// ЗАЧЕМ. Пока экран погашен или сеанс заперт, записывать нечего: содержимое
    /// буфера всё равно никому не пригодится, а конвейер продолжает занимать
    /// видеопамять, кодировать кадры и держать около гигабайта оперативной памяти.
    /// На ноутбуке это ещё и разряд батареи в закрытой крышке.
    ///
    /// Останавливаем с намерением UserStop: оно освобождает буферы и запускает
    /// сжатие кучи. Возобновление всё равно начинает буфер заново, поэтому держать
    /// арену на время паузы незачем.
    ///
    /// Ручную запись в файл пауза НЕ трогает. Пользователь мог запустить запись
    /// намеренно перед тем, как отойти, и оборвать её тише было бы хуже всего.
    /// </summary>
    public void SuspendForSystem(string reason)
    {
        lock (_lifecycle)
        {
            bool added = _systemSuspendReasons.Add(reason);
            if (_continuousRecordingRequested || _recorder is not null)
            {
                if (added) Log.Info("Engine", $"{reason}: идёт запись в файл, пауза отложена");
                // Повтор обязателен. Причина уже в множестве, поэтому наблюдатель
                // больше ничего не пришлёт: он сообщает о СМЕНЕ состояния, а оно не
                // изменится, пока человек не вернётся. Без повтора пауза после
                // окончания записи не наступила бы вовсе.
                ScheduleSuspendRetry(reason);
                return;
            }
            if (_suspendedBySystem || _state == EngineState.Stopped) return;

            // Идёт сохранение — останов ждал бы его завершения, держа замок
            // жизненного цикла. Всё это время хоткей сохранения и любая правка
            // настроек стояли бы в очереди за нами. Клип важнее паузы: отпускаем
            // замок и пробуем снова через несколько секунд. Причина уже записана,
            // поэтому повтор ничего не удвоит.
            if (_state == EngineState.Saving || _saveTask is { IsCompleted: false })
            {
                if (added) Log.Info("Engine", $"{reason}: идёт сохранение, пауза отложена");
                ScheduleSuspendRetry(reason);
                return;
            }

            _suspendedBySystem = true;
            Log.Info("Engine", $"{reason}: конвейер приостановлен");
            StopLocked();
        }
    }

    /// <summary>
    /// Снять одну причину паузы и, если других не осталось, поднять конвейер.
    ///
    /// Поднимаем только то, что сами и остановили: если пользователь выключил повтор
    /// до блокировки экрана, разблокировка не должна включать его обратно.
    /// </summary>
    public void ResumeAfterSystem(string reason)
    {
        lock (_lifecycle)
        {
            _systemSuspendReasons.Remove(reason);
            if (_systemSuspendReasons.Count > 0 || !_suspendedBySystem) return;

            _suspendedBySystem = false;
            if (_stopRequested) return;   // приложение закрывается — поднимать нечего

            Log.Info("Engine", $"{reason}: конвейер возобновлён");
            try { StartWithFallbackLocked(preserveBuffers: false); }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                Warning?.Invoke($"Не удалось возобновить запись после события «{reason}»: {ex.Message}");
            }
        }
    }

    private void StopLocked(PipelineStopIntent intent = PipelineStopIntent.UserStop)
    {
        if (intent == PipelineStopIntent.UserStop)
        {
            _continuousRecordingRequested = false;
            _recoveryCancellation?.Cancel();
            _gameCaptureRecovery.Stop();
            Interlocked.Exchange(ref _windowHoldStartedTimestamp, 0);
        }
        var restartActions = PipelineRestartPolicy.For(
            intent, continuousRecordingActive: _recorder is not null, formatCompatible: true);
        Interlocked.Increment(ref _captureGeneration); // все колбэки старого источника больше не действуют
        DumpStats("на выключении");
        // Дожидаемся уже начатых колбэков: обычный Dispose возвращается сразу, и
        // вотчдог продолжал работать параллельно сносу — вплоть до попытки поднять
        // захват на уже освобождённом источнике.
        StopTimer(ref _gameTimer);
        StopTimer(ref _watchdog);
        StopTimer(ref _probeTimer);
        StopTimer(ref _statsTimer);

        // Сохранение работает с буферами и клипом в арене — снести всё это под ним
        // означает потерять клип, который пользователю уже показали как сохранённый.
        WaitForPendingWrites();

        // Гасим ворота и отписываемся: с этого момента новые кадры в конвейер не идут
        _pipelineOpen = false;
        _encodedStreamReady = false;
        if (_capture is not null)
        {
            if (_captureFrameHandler is not null)
                _capture.FrameArrived -= _captureFrameHandler;
            if (_captureFailureHandler is not null)
                _capture.Failed -= _captureFailureHandler;
            // Останавливаем поток захвата, но УСТРОЙСТВО НЕ ТРОГАЕМ: им ещё
            // пользуется пейсер энкодера, см. порядок разрушения ниже
            _capture.Stop();
        }
        _captureFrameHandler = null;
        _captureFailureHandler = null;
        StopFrameWorker();
        DrainFramesInFlight();

        // Файл записи закрываем ДО сноса конвейера и дожидаемся конца: иначе выход
        // из приложения обрывает финализацию и MP4 остаётся без moov — «сохранено,
        // а записи нет».
        if (_recorder is not null) StopRecordingLocked(wait: true);
        if (intent == PipelineStopIntent.UserStop)
        {
            _audio.BlockReady -= _audioBuffer.Add;
            _audio.Stop();
        }

        // ПОРЯДОК ВАЖЕН, и требований тут ДВА, встречных.
        //
        // 1. Поток захвата не должен работать, когда освобождают видеопроцессор и
        //    текстуры пула: он ими пользуется прямо в кадре. Поэтому выше стоит
        //    _capture.Stop() — он дожидается своего потока (DDA джойнит его, WGC
        //    ждёт завершения колбэков).
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
        _gpuTimer?.Dispose(); _gpuTimer = null;
        _processor?.Dispose(); _processor = null;
        _frameBroker?.Dispose(); _frameBroker = null;
        _capture?.Dispose(); _capture = null;
        lock (_captureTargetSync) _activeCaptureTarget = null;

        if (!restartActions.KeepReplayBuffer) _videoBuffer.Clear();
        if (!restartActions.KeepAudioBuffer) _audioBuffer.Release();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
        SetState(intent == PipelineStopIntent.UserStop
            ? EngineState.Stopped
            : EngineState.Recovering);
        Log.Info("Engine", intent == PipelineStopIntent.UserStop
            ? "Instant Replay выключен"
            : "Видеоконвейер остановлен для автовосстановления; replay остался в RAM");
        if (intent == PipelineStopIntent.UserStop) ReleaseMemory();
    }

    /// <summary>
    /// Погасить таймер и дождаться, пока завершится уже начатый колбэк.
    /// Перегрузка Dispose(WaitHandle) для того и существует: без неё колбэк
    /// продолжает работать параллельно разрушению того, чем он пользуется.
    /// </summary>
    private static void StopTimer(ref System.Threading.Timer? timer)
    {
        var t = timer;
        if (t is null) return;
        timer = null;

        using var done = new ManualResetEvent(false);
        if (t.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(5));
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
    private void WaitForPendingWrites()
    {
        var pending = new List<Task>();
        if (_saveTask is { IsCompleted: false } replay &&
            !(Task.CurrentId is int current && current == _saveTaskId))
            pending.Add(replay);
        lock (_writerTasksSync)
            pending.AddRange(_writerTasks.Where(t => !t.IsCompleted && t.Id != Task.CurrentId));
        if (pending.Count == 0) return;

        Log.Info("Engine", $"Останов ждёт фоновые записи: {pending.Count}");
        try
        {
            if (!Task.WaitAll([.. pending], TimeSpan.FromMinutes(2)))
                Log.Warn("Engine", "Фоновые записи не закончились за 2 минуты — продолжаю останов");
        }
        catch (AggregateException ex)
        {
            Log.Warn("Engine", $"Фоновая запись закончилась с ошибкой: {ex.GetBaseException().Message}");
        }
    }

    public void Toggle()
    {
        lock (_lifecycle)
        {
            // Через Toggle идут трей и хоткей, то есть основной путь пользователя.
            // Сброс системной паузы обязан быть здесь так же, как в Start и Stop:
            // человек решил сам, и накопленные причины больше не действуют.
            ForgetSystemSuspend();

            if (_state == EngineState.Stopped)
            {
                StartWithFallbackLocked(preserveBuffers: false);
            }
            else
            {
                _stopRequested = true;
                _continuousRecordingRequested = false;
                StopLocked();
            }
        }
    }

    private void SetState(EngineState st)
    {
        lock (_stateSync) _state = st;
        StateChanged?.Invoke(st);
    }

    /// <summary>
    /// Фоновое сохранение не имеет права затереть более новое состояние recovery.
    /// Переход выполняется атомарно относительно SetState, но без lifecycle-lock:
    /// StopLocked может ждать эту задачу, уже удерживая lifecycle.
    /// </summary>
    private void CompleteSavingState()
    {
        EngineState next;
        lock (_stateSync)
        {
            if (_state != EngineState.Saving) return;
            next = _pipelineOpen ? EngineState.Running : EngineState.Stopped;
            _state = next;
        }
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        Stop();
        _audio.Dispose();
        _frameGate.Dispose();
    }
}
