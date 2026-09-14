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
public sealed class ReplayEngine : IDisposable
{
    private static readonly TimeSpan ScreenshotFreshFrameWait = TimeSpan.FromMilliseconds(200);

    private readonly SettingsManager _settings;
    private readonly StorageManager _storage;

    private IScreenCapture? _capture;
    private VideoProcessorNv12? _processor;
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
    public bool TryUseLiveFrame(UseFrame use)
    {
        // Те же ворота, что и у OnFrame: скриншот берёт кадр с живого устройства
        // захвата, и Stop() не должен освободить это устройство прямо во время чтения.
        if (!_frameGate.TryEnterReadLock(TimeSpan.FromMilliseconds(50))) return false;
        try
        {
            var cap = _capture;
            var broker = _frameBroker;
            long generation = Interlocked.Read(ref _captureGeneration);
            if (cap is null || broker is null || !_pipelineOpen) return false;
            return broker.TryUseFreshestMonitor(
                generation,
                ScreenshotFreshFrameWait,
                texture => use(cap.D3DDevice, cap.D3DContext, texture));
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
        var selection = ScreenCaptureFactory.Selection;
        _captureBackend = selection.Backend;
        _preferredCaptureBackend = selection.Backend;
        _captureBackendForced = selection.Forced;
        _gameCaptureRecovery = new GameCaptureRecoveryCoordinator(
            _preferredCaptureBackend,
            _captureBackendForced);
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
            if (!_captureHealth.CanUse(alternative, now) ||
                !_captureHealth.TryRecordSwitch(now)) throw;

            Log.Warn("Engine", $"Захват {candidate} не запустился — пробую {alternative}");
            // Неудачный Start очистил частично собранный pipeline как UserStop и
            // остановил coordinator. Это внутренний fallback, а не команда человека.
            _stopRequested = false;
            _gameCaptureRecovery.Resume();
            StartLocked(preserveBuffers, alternative);
        }
    }

    private CaptureBackend MonitorFallbackFor(CaptureBackend backend) =>
        backend == CaptureBackend.MinecraftOpenGl
            ? _preferredCaptureBackend
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

            var monitorCanvas = MonitorLayout.For(s.MonitorIndex);
            int canvasWidth = monitorCanvas?.Width ?? _capture.Width;
            int canvasHeight = monitorCanvas?.Height ?? _capture.Height;

            // Десять бит просим только у HEVC: см. AppSettings.BitDepth.
            bool wantTenBit = s.Codec == VideoCodec.HEVC &&
                              s.BitDepth is VideoBitDepth.Auto or VideoBitDepth.Ten;

            _processor = new VideoProcessorNv12(_capture.D3DDevice, _capture.D3DContext);
            try
            {
                _processor.Configure(canvasWidth, canvasHeight, s.VerticalResolution, s.Fps, wantTenBit);
            }
            catch (Exception ex) when (wantTenBit)
            {
                // Последняя страховка десяти бит. Всё, что можно было спросить у
                // драйвера, мы спросили, но отказать он вправе и на любом другом
                // шаге. Запись важнее глубины цвета, поэтому молча берём восемь бит
                // вместо того, чтобы не включиться вовсе.
                Log.Warn("Capture", $"Десять бит не настроились ({ex.Message}) — беру восемь");
                _processor.Configure(canvasWidth, canvasHeight, s.VerticalResolution, s.Fps,
                                     preferTenBit: false);
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
                                     preferTenBit: false);
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

                long t0 = Diagnostics.PipelineProbe.Now();
                var nv12 = _processor!.Convert(current.Texture);
                long t1 = Diagnostics.PipelineProbe.Now();
                _encoder!.SubmitFrame(nv12, current.Timestamp, _capture!.D3DContext);
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

    // ---------------- Восстановление и автосмена backend ----------------

    private int _recovering;

    private void RequestCaptureRestart(
        CaptureRecoveryDecision initialDecision,
        string reason,
        CaptureFailureKind failureKind,
        long observedGeneration)
    {
        CaptureBackend next = initialDecision.Backend;
        CaptureBackend previous = _captureBackend;
        if (initialDecision.Action == CaptureRecoveryAction.HoldForGameWindow)
        {
            Interlocked.CompareExchange(
                ref _windowHoldStartedTimestamp,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                0);
        }
        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? replaced = Interlocked.Exchange(
            ref _recoveryCancellation, cancellation);
        replaced?.Cancel();

        Task.Run(() =>
        {
            try
            {
                string recoveryMode = initialDecision.Action == CaptureRecoveryAction.HoldForGameWindow
                    ? "; держу последний игровой кадр"
                    : "";
                Log.Warn("Engine", $"Захват generation {observedGeneration}: {previous} → {next}; " +
                                   $"target r{initialDecision.TargetRevision}; {reason}{recoveryMode}");
                Warning?.Invoke(previous == next
                    ? "Перезапускаю захват экрана"
                    : $"Переключаю захват: {previous} → {next}");

                // При деградации оконного WGC не рвём конвейер сразу: пейсер
                // продолжает кодировать последний принятый игровой кадр на время
                // короткой выдержки. Мониторный кадр в этот эпизод admission-gate
                // всё равно не пропустит.
                bool delayConsumedWhileHolding =
                    initialDecision.Action == CaptureRecoveryAction.HoldForGameWindow &&
                    initialDecision.RetryDelay > TimeSpan.Zero;
                if (delayConsumedWhileHolding &&
                    cancellation.Token.WaitHandle.WaitOne(initialDecision.RetryDelay))
                    return;

                try
                {
                    lock (_lifecycle)
                    {
                        if (_stopRequested || observedGeneration != Interlocked.Read(ref _captureGeneration)) return;
                        StopLocked(PipelineStopIntent.CaptureRestart);
                        if (_state != EngineState.Recovering)
                            throw new InvalidOperationException("Остановка видеоконвейера не завершилась");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Engine", $"Автовосстановление отменено: конвейер не остановился ({ex.Message})");
                    Warning?.Invoke("Не удалось безопасно перезапустить захват");
                    return;
                }

                long restartGeneration = Interlocked.Read(ref _captureGeneration);
                CaptureBackend candidate = next;
                CaptureRecoveryDecision decision = initialDecision;
                for (int attempt = 1; ; attempt = attempt == int.MaxValue ? attempt : attempt + 1)
                {
                    int delay = attempt == 1 && delayConsumedWhileHolding
                        ? 0
                        : attempt == 1 && decision.RetryDelay > TimeSpan.Zero
                        ? Math.Max(1, (int)decision.RetryDelay.TotalMilliseconds)
                        : CaptureRecoveryBackoff.DelayMilliseconds(
                            attempt, failureKind == CaptureFailureKind.DeviceLost);
                    if (cancellation.Token.WaitHandle.WaitOne(delay)) return;

                    // Пока мы спали, человек мог выключить запись — тогда включать
                    // её обратно нельзя ни при каких обстоятельствах
                    if (_stopRequested ||
                        restartGeneration != Interlocked.Read(ref _captureGeneration))
                    {
                        Log.Info("Engine", "Восстановление отменено: состояние конвейера уже изменилось");
                        return;
                    }

                    try
                    {
                        GameCaptureTarget? candidateTarget = candidate is
                            CaptureBackend.WgcWindow or CaptureBackend.MinecraftOpenGl
                            ? _gameCaptureRecovery.Target
                            : null;
                        if (candidate == CaptureBackend.WgcWindow)
                            Interlocked.Increment(ref _windowRetryCount);
                        lock (_lifecycle)
                        {
                            if (_stopRequested ||
                                restartGeneration != Interlocked.Read(ref _captureGeneration)) return;
                            StartLocked(preserveBuffers: true, candidate, candidateTarget);
                            if (_stopRequested)
                            {
                                StopLocked(PipelineStopIntent.UserStop);
                                return;
                            }
                            // Читаем intent под тем же lifecycle-lock: если человек
                            // нажал «остановить» в промежутке, новый файл не создаём.
                            if (_continuousRecordingRequested) StartRecordingLocked();
                            if (_stopRequested)
                            {
                                StopLocked(PipelineStopIntent.UserStop);
                                return;
                            }
                        }
                        long holdStarted = Interlocked.Exchange(ref _windowHoldStartedTimestamp, 0);
                        string held = holdStarted == 0
                            ? ""
                            : $", удержание {ElapsedMilliseconds(holdStarted)} мс";
                        Log.Info("Engine", $"Конвейер восстановлен на {candidate} " +
                                           $"(попытка {attempt}{held})");
                        Warning?.Invoke("Запись восстановлена");
                        return;
                    }
                    catch (OperationCanceledException) when (_stopRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Engine", $"Восстановление {candidate}, попытка {attempt}: {ex.Message}");
                        restartGeneration = Interlocked.Read(ref _captureGeneration);
                        RefreshCaptureTarget();
                        if (!_gameCaptureRecovery.TryDecide(
                                candidate,
                                CaptureFailureKind.BackendUnavailable,
                                out decision))
                            return;
                        candidate = decision.Backend;
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(
                        Interlocked.CompareExchange(ref _recoveryCancellation, null, cancellation),
                        cancellation))
                {
                    cancellation.Dispose();
                }
                Interlocked.Exchange(ref _recovering, 0);
            }
        });
    }

    private static long ElapsedMilliseconds(long startedTimestamp)
    {
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp;
        return elapsed <= 0
            ? 0
            : (long)(elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    // Раз в минуту — здоровье конвейера в лог: по этим цифрам видно, ГДЕ теряются
    // кадры (дропы очереди = не успевает энкодер; низкий submit = не успевает захват).
    private System.Threading.Timer? _statsTimer;
    private long _lastSkippedBeforeConvert;
    private long _lastSubmitted, _lastEncoded, _lastDropped, _lastDiscardedDuplicates,
                 _lastSuppressedDuplicates, _lastDuplicated, _lastReceived, _lastAccepted;
    private long _lastRequests, _lastPacerBlocked;

    private void StartStatsTimer()
    {
        _lastSubmitted = _lastEncoded = _lastDropped = _lastDiscardedDuplicates =
            _lastSuppressedDuplicates = _lastDuplicated = _lastReceived = _lastAccepted = 0;
        _lastRequests = _lastPacerBlocked = _lastSkippedBeforeConvert = 0;
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

        long skipped = Interlocked.Read(ref enc.FramesSkippedBeforeConvert);
        long s = enc.FramesSubmitted, e = enc.FramesEncoded,
             d = enc.FramesDroppedRealQueue,
             discardedDuplicates = enc.FramesDiscardedDuplicates,
             suppressedDuplicates = enc.FramesSuppressedDuplicates,
             dup = enc.FramesDuplicated;
        long req = enc.InputRequests, blocked = enc.PacerBlocked;
        long rcv = cap?.FramesReceived ?? 0, acc = cap?.FramesAccepted ?? 0;
        double seconds = Math.Max((DateTime.UtcNow - _statsWindowStart).TotalSeconds, 0.001);
        if (seconds < 2) return; // только что выгружали — нечего показывать

        // fps по каждой стадии: сразу видно, кто именно не дотягивает до настроенного.
        string captureName = _captureBackend == CaptureBackend.Wgc ? "WGC" : "DDA";
        Log.Info("Engine", $"Конвейер {label} ({seconds:F0} с): {captureName} {rcv - _lastReceived}/{acc - _lastAccepted} " +
            $"(получено/принято), захвачено {s - _lastSubmitted}, " +
            $"дубликатов {dup - _lastDuplicated}, закодировано {e - _lastEncoded}, " +
            $"дропнуто реальных {d - _lastDropped}, убрано старых дублей " +
            $"{discardedDuplicates - _lastDiscardedDuplicates}, подавлено дублей " +
            $"{suppressedDuplicates - _lastSuppressedDuplicates} " +
            $"(буфер {(int)BufferedDuration.TotalSeconds} сек) | " +
            $"fps: {captureName} {(rcv - _lastReceived) / seconds:F1}, подано {(s - _lastSubmitted + dup - _lastDuplicated) / seconds:F1}, " +
            $"закодировано {(e - _lastEncoded) / seconds:F1}, запросов MFT {(req - _lastRequests) / seconds:F1}" +
            $", пресет {enc.QualityPreset}, кадров внутри MFT до {Interlocked.Exchange(ref enc.MaxInFlight, 0)}" +
            (blocked > _lastPacerBlocked ? $"; пейсер молчал {blocked - _lastPacerBlocked} раз (давление очереди/MFT)" : "") +
            (skipped > _lastSkippedBeforeConvert
                ? $"; не преобразовано на забитой очереди {skipped - _lastSkippedBeforeConvert}"
                : ""));

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

        _lastSubmitted = s; _lastEncoded = e; _lastDropped = d;
        _lastSkippedBeforeConvert = skipped;
        _lastDiscardedDuplicates = discardedDuplicates;
        _lastSuppressedDuplicates = suppressedDuplicates;
        _lastDuplicated = dup;
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
        // Коммит сборщика — состояние НА МОМЕНТ ПОСЛЕДНЕЙ СБОРКИ, а не на сейчас.
        // При SustainedLowLatency блокирующие сборки второго поколения подавлены,
        // поэтому число может быть старым, и тогда «нативная» вбирает в себя рост
        // управляемой кучи. Счётчик сборок показывает, обновилось ли оно вообще.
        int gen2 = GC.CollectionCount(2);

        Log.Info("Engine", $"Память: буфер видео {Mb(ring)}, звук {Mb(audio)}, " +
                           $"живых объектов {Mb(heap)}, коммит сборщика {Mb(gcCommitted)} (сборок gen2 {gen2}), " +
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

    private GameCaptureTarget? RefreshCaptureTarget()
    {
        lock (_captureTargetSync)
        {
            GameCaptureTarget? selected = ForegroundGameWindowProbe.TrySelect(
                _settings.Current.MonitorIndex,
                _lastVerifiedCaptureTarget);
            if (selected is GameCaptureTarget target)
                _lastVerifiedCaptureTarget = target;
            _gameCaptureRecovery.ObserveTarget(selected);
            return selected;
        }
    }

    private GameCaptureTarget? ActiveCaptureTarget()
    {
        lock (_captureTargetSync) return _activeCaptureTarget;
    }

    private void StartGameTracker(bool preserveHistory = false)
    {
        if (!preserveHistory)
            lock (_gameSync) _gameSamples.Clear();
        _gameTimer?.Dispose();
        _gameTimer = new System.Threading.Timer(_ =>
        {
            if (!_pipelineOpen || _stopRequested) return;
            try
            {
                GameCaptureTarget? foregroundTarget = RefreshCaptureTarget();
                CaptureBackendTargetSelection targetSelection =
                    CaptureBackendPolicy.SelectForForeground(
                        _captureBackend,
                        ActiveCaptureTarget(),
                        foregroundTarget,
                        _captureBackendForced);
                if (targetSelection.RestartRequired)
                    RequestProactiveCaptureTransition(targetSelection);

                if (_captureBackend == CaptureBackend.WgcWindow &&
                    ActiveCaptureTarget() is GameCaptureTarget activeTarget &&
                    (foregroundTarget is not GameCaptureTarget currentTarget ||
                     !currentTarget.HasSameIdentity(activeTarget) ||
                     currentTarget.Revision != activeTarget.Revision))
                {
                    long generation = Interlocked.Read(ref _captureGeneration);
                    OnCaptureFailed(new CaptureFailure(
                        CaptureFailureKind.CaptureTargetClosed,
                        new InvalidOperationException("Foreground game target changed"),
                        "WGC window: игра вышла из fullscreen или сменила окно",
                        generation,
                        activeTarget.Revision), generation);
                }

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

    private void RequestProactiveCaptureTransition(
        in CaptureBackendTargetSelection selection)
    {
        if (selection.Target is not GameCaptureTarget target ||
            !_pipelineOpen || _stopRequested ||
            Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
        {
            return;
        }

        long generation = Interlocked.Read(ref _captureGeneration);
        var decision = new CaptureRecoveryDecision(
            CaptureRecoveryAction.Restart,
            selection.Backend,
            target.Revision,
            TimeSpan.Zero,
            _gameCaptureRecovery.Episode.Align(target));
        RequestCaptureRestart(
            decision,
            $"обнаружен Minecraft fullscreen: PID {target.ProcessId}, revision {target.Revision}",
            CaptureFailureKind.BackendUnavailable,
            generation);
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
    // Каждые 5 сек проверяем приток кадров; тишина >15 сек запускает общую
    // generation-safe пересборку и при необходимости смену backend.
    private System.Threading.Timer? _watchdog;
    private long _wdLastReceived = -1;
    private DateTime _wdLastActivity = DateTime.UtcNow;
    private bool _wdEpisodeLogged; // логируем только начало эпизода тишины, не каждые 15 сек
    private bool _wdSilenceLogged; // ранняя запись с обстановкой — один раз на эпизод
    private double _wdLastRate;    // кадров в секунду в последнем живом окне

    // ---------------- Посекундная диагностика провалов ----------------
    //
    // Поминутной сводки мало: она усредняет провал вместе с нормальной работой и
    // не даёт отличить три разных болезни друг от друга —
    //   A) кончается видеопамять: бюджет падает, кадры перестают приходить И кодироваться;
    //   B) молчит захват: бюджет в норме, кадры не приходят, очередь пуста;
    //   C) не тянет энкодер: кадры приходят, очередь полна, запросов MFT мало.
    // Поэтому пока идёт провал, пишем строку раз в секунду, а в норме молчим.

    private System.Threading.Timer? _probeTimer;
    private long _probeRecv, _probeEnc, _probeReq, _probeDrop, _probeDup;
    private long _probeLastTimestamp;
    private int _probeRunning;
    private bool _probeEpisode;
    private int _probeQuiet;

    /// <summary>Ниже этого числа ЗАКОДИРОВАННЫХ кадров в секунду считаем, что идёт провал.</summary>
    private const int ProbeFpsFloor = 45;

    private void StartCaptureProbe()
    {
        _probeEpisode = false;
        _probeQuiet = 0;
        _probeRecv = _probeEnc = _probeReq = _probeDrop = _probeDup = 0;
        _probeLastTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Volatile.Write(ref _probeRunning, 0);
        _probeTimer?.Dispose();
        _probeTimer = new System.Threading.Timer(_ => Probe(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Probe()
    {
        // System.Threading.Timer допускает reentrancy: под нагрузкой следующий tick
        // может прийти до завершения предыдущего и дважды сдвинуть общие счётчики.
        if (Interlocked.CompareExchange(ref _probeRunning, 1, 0) != 0) return;
        try { ProbeCore(); }
        finally { Volatile.Write(ref _probeRunning, 0); }
    }

    private void ProbeCore()
    {
        var cap = _capture;
        var enc = _encoder;
        long generation = Interlocked.Read(ref _captureGeneration);
        CaptureBackend backend = _captureBackend;
        if (cap is null || enc is null || !_pipelineOpen || _stopRequested) return;

        try
        {
            long recv = cap.FramesReceived, encoded = enc.FramesEncoded;
            long req = enc.InputRequests, drop = enc.FramesDroppedRealQueue, dup = enc.FramesDuplicated;

            long dRecv = recv - _probeRecv, dEnc = encoded - _probeEnc, dReq = req - _probeReq;
            long dDrop = drop - _probeDrop, dDup = dup - _probeDup;
            _probeRecv = recv; _probeEnc = encoded; _probeReq = req; _probeDrop = drop; _probeDup = dup;

            long sampleTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double sampleSeconds = (sampleTimestamp - _probeLastTimestamp) /
                                   (double)System.Diagnostics.Stopwatch.Frequency;
            _probeLastTimestamp = sampleTimestamp;
            if (sampleSeconds <= 0) return;
            static long PerSecond(long delta, double seconds) =>
                (long)Math.Round(Math.Max(0, delta) / seconds);
            long fpsRecv = PerSecond(dRecv, sampleSeconds);
            long fpsEnc = PerSecond(dEnc, sampleSeconds);
            long fpsReq = PerSecond(dReq, sampleSeconds);
            long fpsDrop = PerSecond(dDrop, sampleSeconds);
            long fpsDup = PerSecond(dDup, sampleSeconds);

            bool gameForeground = _gameCaptureRecovery.Target is not null;
            var health = _captureHealth.Observe(new CaptureHealthSample(
                    backend,
                    _settings.Current.Fps,
                    (int)Math.Clamp(fpsRecv, 0, int.MaxValue),
                    (int)Math.Clamp(fpsEnc, 0, int.MaxValue),
                    (int)Math.Clamp(fpsDup, 0, int.MaxValue),
                    gameForeground,
                    DateTimeOffset.UtcNow - _pipelineStartedAt),
                DateTimeOffset.UtcNow);

            if (health.SwitchBackend)
            {
                string metrics = $"{health.Reason}; получено {fpsRecv}, закодировано {fpsEnc}, дублей {fpsDup} кадр/с";
                OnCaptureFailed(new CaptureFailure(
                    CaptureFailureKind.BackendStalled,
                    new InvalidOperationException("WGC capture starvation"), metrics,
                    generation, ActiveCaptureTarget()?.Revision ?? 0), generation);
                return;
            }

            // Смотрим ТОЛЬКО на кодирование: именно оно попадает в файл. Низкий
            // приток от WGC сам по себе нормален — на малоподвижной картинке система
            // отдаёт меньше кадров, а пейсер добивает сетку дубликатами, и запись
            // остаётся ровной. По прежнему порогу «или захват, или кодирование»
            // диагностика срабатывала 725 раз за день на совершенно здоровой работе
            // и топила в себе настоящие провалы.
            bool bad = fpsEnc < ProbeFpsFloor;

            // Хвост после восстановления: по нему видно, что именно поднялось первым
            if (!bad && _probeEpisode && ++_probeQuiet > 3) { _probeEpisode = false; _probeQuiet = 0; return; }
            if (!bad && !_probeEpisode) return;
            if (bad) _probeQuiet = 0;

            if (!_probeEpisode)
            {
                _probeEpisode = true;
                Log.Warn("Probe", "Провал записи — посекундная диагностика (кадры/с: получено, закодировано, " +
                                  "запросов MFT, дублей, дропов | backend/broker/cursor | очередь | видеопамять)");
            }

            string vram = GpuInfo.Usage(cap.D3DDevice) is { } v
                ? $"{v.UsedMb}/{v.BudgetMb} МБ"
                : "нет данных";
            var broker = _frameBroker?.GetDiagnostics(cap.InvalidCursorShapes)
                ?? new Diagnostics.CaptureBrokerDiagnostics(
                    generation, 0, 0, 0, 0, 0, cap.InvalidCursorShapes);
            long now100Nanoseconds = (long)(sampleTimestamp *
                (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            long frameAgeMilliseconds = Diagnostics.CaptureBrokerDiagnostics.AgeMilliseconds(
                now100Nanoseconds, broker.LatestTimestamp);

            string captureLabel = backend switch
            {
                CaptureBackend.Wgc => "WGC-monitor",
                CaptureBackend.WgcWindow => "WGC-window",
                CaptureBackend.DesktopDuplication => "DDA",
                CaptureBackend.MinecraftOpenGl => "OpenGL-game",
                _ => backend.ToString()
            };
            string routeDetails = "";
            if (cap is MinecraftGameCaptureSource minecraft)
            {
                Diagnostics.CaptureRouteProbeDiagnostics route = minecraft.GetRouteDiagnostics();
                captureLabel = route.Label;
                routeDetails = $"/epoch {route.RouteEpoch}/hook {route.HookState}:{route.HookError}/" +
                               $"heartbeat {route.HookHeartbeatAgeMilliseconds} мс/" +
                               $"issued {route.FramesIssued}/mapped {route.FramesMapped}/" +
                               $"published {route.FramesPublished}/rejected {route.FramesRejected}/" +
                               $"uploaded {route.FramesUploaded}";
            }
            GameCaptureTarget? target = ActiveCaptureTarget() ?? _gameCaptureRecovery.Target;
            string targetLabel = target is GameCaptureTarget current
                ? $"{current.ExecutableName}/hwnd 0x{current.Hwnd.ToInt64():X}/" +
                  $"pid {current.ProcessId}/r{current.Revision}"
                : "нет";
            CaptureEpisode episode = _gameCaptureRecovery.Episode;
            long holdStarted = Interlocked.Read(ref _windowHoldStartedTimestamp);
            long holdMilliseconds = holdStarted == 0 ? 0 : ElapsedMilliseconds(holdStarted);

            Log.Info("Probe", $"получено {fpsRecv}, закодировано {fpsEnc}, запросов {fpsReq}, " +
                              $"дублей {fpsDup}, дропов {fpsDrop} | {captureLabel}/gen {broker.Generation}{routeDetails} " +
                              $"target {targetLabel}/quarantine {episode.Quarantines} | " +
                              $"broker {broker.FramesPublished}/drop {broker.FramesDroppedNoSlot}/" +
                              $"reject {broker.FramesRejected}/" +
                              $"age {frameAgeMilliseconds} мс | cursor rev {broker.CursorRevision}/" +
                              $"invalid {broker.InvalidCursorShapes} | DDA storms {_ddaStormCount}/" +
                              $"window retries {_windowRetryCount}/hold {holdMilliseconds} мс | " +
                              $"очередь {enc.QueueDepth}/{enc.MaxQueue} " +
                              $"(пул {enc.PoolSlots}) | VRAM {vram}");
        }
        catch (Exception ex) { Log.Warn("Probe", $"Диагностика прервана: {ex.Message}"); }
    }

    private void StartCaptureWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;

        // DDA не присылает кадры, пока изображение рабочего стола не меняется —
        // это штатное поведение AcquireNextFrame, а не зависание. Его состояние
        // контролируется через ACCESS_LOST/Failed внутри самого источника.
        if (_capture is DesktopDuplicationSource)
        {
            Log.Info("Engine", "Watchdog тишины не нужен для Desktop Duplication");
            return;
        }

        _wdLastReceived = -1;
        _wdLastRate = 0;
        _wdLastActivity = DateTime.UtcNow;
        _watchdog = new System.Threading.Timer(_ =>
        {
            var cap = _capture;
            if (cap is null || !_pipelineOpen || _stopRequested) return;

            long received = cap.FramesReceived;
            if (received != _wdLastReceived)
            {
                // Запоминаем темп последнего живого окна: по нему и судим, тишина
                // это поломка или законное затишье (см. ниже).
                var now = DateTime.UtcNow;
                double window = (now - _wdLastActivity).TotalSeconds;
                if (_wdLastReceived >= 0 && window > 0.5)
                    _wdLastRate = (received - _wdLastReceived) / window;

                _wdLastReceived = received;
                _wdLastActivity = now;
                _wdEpisodeLogged = false;
                _wdSilenceLogged = false;
                return;
            }
            double silent = (DateTime.UtcNow - _wdLastActivity).TotalSeconds;

            // Порог пересборки зависит от того, ЧТО мы снимаем.
            //
            // Игра показывает кадры непрерывно, поэтому её молчание дольше пяти
            // секунд — это уже сломанный захват, и ждать пятнадцать значит подарить
            // буферу десять секунд пустоты. На рабочем столе всё наоборот: WGC
            // отдаёт кадры по композиции, а если на экране ничего не меняется,
            // композиции может не быть вовсе. Там короткий порог давал бы ложные
            // пересборки на ровном месте.
            var target = ActiveCaptureTarget();

            // Судим по ТЕМПУ последнего живого окна, а не по тому, что снимаем.
            //
            // WGC отдаёт кадры по композиции рабочего стола. Если на экране ничего
            // не меняется, композиции может не быть вовсе, и молчание там законно —
            // короткий порог давал бы пересборки на ровном месте. Но если секунду
            // назад шло шестьдесят кадров в секунду, а теперь ноль, это поломка,
            // и ждать пятнадцать секунд значит подарить буферу столько же пустоты.
            //
            // Отдельный случай — физически выключенный монитор. Событие питания при
            // этом не приходит вовсе: для Windows это отключение дисплея от шины,
            // а не погашенный экран. В логе такой эпизод выглядел как ровные 60 fps
            // и сразу за ними тишина.
            bool wasLive = _wdLastRate >= 20 || target is not null;
            double rebuildAfter = wasLive ? 3 : 15;

            // Ранняя запись в лог: она не чинит захват, но без неё причина эпизода
            // терялась. Пересборка стирает и очередь, и состояние источника, то есть
            // всё, по чему потом можно было бы понять, что именно встало.
            if (silent >= 3 && !_wdSilenceLogged)
            {
                _wdSilenceLogged = true;
                string vram = GpuInfo.Usage(cap.D3DDevice) is { } usage
                    ? $"{usage.UsedMb}/{usage.BudgetMb} МБ"
                    : "не читается";
                string captureName = _captureBackend == CaptureBackend.Wgc ? "WGC" : "DDA";
                Log.Warn("Engine", $"Захват молчит {silent:F1} с: backend {captureName}, " +
                                   $"цель {(target is null ? "рабочий стол" : $"окно 0x{target.Value.Hwnd:X}/r{target.Value.Revision}")}, " +
                                   $"получено всего {received}, темп до тишины {_wdLastRate:F0} кадр/с, " +
                                   $"очередь энкодера {_encoder?.QueueDepth ?? -1}, " +
                                   $"видеопамять {vram}; пересборка через {rebuildAfter - silent:F1} с");
            }

            if (silent < rebuildAfter) return;

            _wdLastActivity = DateTime.UtcNow;
            _wdSilenceLogged = false;
            if (!_wdEpisodeLogged)
            {
                _wdEpisodeLogged = true;
                Log.Warn("Engine", $"Захват молчит >{rebuildAfter:F0} сек — пересобираю видеоконвейер");
            }
            long generation = Interlocked.Read(ref _captureGeneration);
            var stalled = new InvalidOperationException(
                $"WGC не присылает кадры больше {rebuildAfter:F0} секунд");
            OnCaptureFailed(new CaptureFailure(
                CaptureFailureKind.BackendStalled,
                stalled,
                "WGC: поток кадров остановился",
                generation,
                target?.Revision ?? 0), generation);
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
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
                if (added) Log.Info("Engine", $"{reason}: идёт запись в файл, конвейер не трогаем");
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
        if (_state != EngineState.Running || !_encodedStreamReady ||
            _encoder?.OutputMediaType is null) return;

        DumpStats("к моменту сохранения"); // короткий сеанс тоже должен оставить следы в логе

        var s = _settings.Current;
        long wanted = TimeSpan.FromSeconds(secondsOverride ?? s.ReplayLengthSeconds).Ticks;

        // Снимок с очисткой: буфер начинает копиться заново, владение массивами
        // кадров переходит нам — вернём их в пул после записи файла.
        var video = _videoBuffer.TakeSnapshot(wanted, out long snapshotToken);
        if (video.Count == 0) { SaveFailed?.Invoke("Буфер ещё пуст"); return; }
        SnapshotLease? lease = new(_videoBuffer, snapshotToken, video);
        string? reservedFile = null;
        try
        {
        // Аудио берём до очистки, затем его шкала начинается заново вместе с видео.
        var audio = _audioBuffer.Snapshot(video[0].PtsTicks, video[^1].PtsTicks);
        _audioBuffer.Clear();

        // Игра берётся по тому, что было на экране пока копился буфер (см. GameForClip)
        string game = GameForClip();
        string file = reservedFile = ReserveFilePath(game, "replay");
        // Копия, а не ссылка на поле энкодера: запись файла переживёт остановку
        // конвейера, а Dispose энкодера освободил бы тип прямо под SinkWriter.
        var mediaType = _encoder.CloneOutputMediaType();
        if (mediaType is null) { SaveFailed?.Invoke("Энкодер ещё не отдал тип видеопотока"); return; }
        lease.MediaType = mediaType;
        int seconds = (int)Math.Round(TimeSpan.FromTicks(video[^1].PtsTicks - video[0].PtsTicks).TotalSeconds);

        // Данные уже вырваны из кольцевого буфера и никуда не денутся — говорим об этом
        // пользователю сразу. Раньше уведомление ждало, пока сотни мегабайт доедут
        // до диска, и на длинном клипе это выглядело как «хоткей не сработал».
        SaveProgress = 0;
        ReplayCaptured?.Invoke(Math.Max(seconds, 1));

        SetState(EngineState.Saving);
        SnapshotLease saveLease = lease;
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
                string publishedFile = ReplaySaver.Save(file, video, audio, mediaType, s.TrackMode,
                                                        s.CaptureGameAudio, s.CaptureMicrophone,
                                                        p => SaveProgress = p);
                _storage.RegisterSaved(publishedFile); // индекс папки — без повторного обхода диска
                // Правку счётчика делает фоновый поток — идём через Update, чтобы она
                // не столкнулась с сохранением настроек из потока интерфейса.
                //
                // ГРУППА ОБЯЗАНА ОСТАВАТЬСЯ "stats". Обработчик Changed на группы
                // video/audio/replay берёт _lifecycle и перезапускает конвейер, а
                // Stop() как раз в это время ждёт завершения ЭТОЙ задачи, держа тот же
                // замок, — получился бы дедлок. Здесь мы внутри сохраняющего потока.
                _settings.Update(x => x.TotalReplaysSaved++, "stats");
                ReplaySaved?.Invoke(publishedFile, Math.Max(seconds, 1));
            }
            catch (Exception ex)
            {
                Log.Error("Engine", ex);
                FallBackToEightBitIfContainerRefused(ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                // Файл записан — возвращаем буферу место, которое занимали кадры
                // клипа. Новой памяти на сохранение не тратилось вовсе: всё это
                // время клип лежал в той же арене, а запись шла в её свободную часть.
                saveLease.Dispose();
                ReleaseFilePath(file);
                // Писатель закрыт — сводим освободившиеся нативные блоки вместе,
                // иначе память, занятая под клип, остаётся за процессом до выхода.
                Diagnostics.MemoryMap.Log("после сохранения");
                self.Priority = previousPriority;      // поток уходит обратно в пул потоков
                CompleteSavingState();
            }
        });
        lease = null; // владение ресурсами снимка перешло фоновой задаче
        reservedFile = null; // и резерв пути тоже
        }
        finally
        {
            // Любая ошибка между TakeSnapshot и успешным Task.Run раньше навсегда
            // оставляла арену зарезервированной, после чего новые кадры отбрасывались.
            lease?.Dispose();
            if (reservedFile is not null) ReleaseFilePath(reservedFile);
        }
    }

    /// <summary>Транзакционное владение snapshot и клоном MediaType.</summary>
    private sealed class SnapshotLease(ReplayVideoBuffer owner, long token, List<EncodedFrame> frames) : IDisposable
    {
        private ReplayVideoBuffer? _owner = owner;
        private List<EncodedFrame>? _frames = frames;
        private IMFMediaType? _mediaType;

        public IMFMediaType? MediaType
        {
            set => _mediaType = value;
        }

        public void Dispose()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is null) return;
            currentOwner.ReleaseSnapshot(token);
            Interlocked.Exchange(ref _frames, null)?.Clear();
            Interlocked.Exchange(ref _mediaType, null)?.Dispose();
        }
    }

    /// <summary>
    /// Путь файла по шаблону из настроек. {game} {date} {time} {preset} + раскладка по папкам игр.
    /// </summary>
    private string ReserveFilePath(string game, string fallbackPrefix)
    {
        var s = _settings.Current;
        lock (_pathSync)
        {
            string path = FileNaming.BuildPath(s.SaveRootPath, s.GroupByGame, s.FileNameTemplate, game,
                DateTime.Now, $"{s.VerticalResolution}p{s.Fps}", fallbackPrefix,
                candidate => File.Exists(candidate) || _reservedPaths.Contains(candidate));
            _reservedPaths.Add(path);
            return path;
        }
    }

    private void ReleaseFilePath(string path)
    {
        lock (_pathSync) _reservedPaths.Remove(path);
    }

    // ---------------- Обычная запись в файл ----------------

    /// <summary>
    /// Начать обычную запись в файл. Если буфер выключен — включает его.
    ///
    /// Под тем же замком, что и остальной жизненный цикл: метод трогает _encoder,
    /// _audio и _recorder, а параллельный Stop() обнуляет ровно их. Monitor
    /// реентерантен, поэтому вызов из StopLocked и из восстановления проходит.
    /// </summary>
    public void StartRecordingToFile()
    {
        lock (_lifecycle)
        {
            _continuousRecordingRequested = true;
            // Recovery уже владеет обязанностью поднять конвейер. Здесь достаточно
            // записать пользовательский intent; новый сегмент откроется после старта.
            if (_state == EngineState.Recovering) return;
            try
            {
                StartRecordingLocked();
                _continuousRecordingRequested = _recorder is not null;
            }
            catch
            {
                _continuousRecordingRequested = false;
                throw;
            }
        }
    }

    private void StartRecordingLocked()
    {
        if (_recorder is not null) return;
        if (_state == EngineState.Stopped) StartWithFallbackLocked(preserveBuffers: false); // может бросить — наружу, UI покажет
        if (_encoder?.OutputMediaType is null) return;

        var s = _settings.Current;
        string game = GameDetector.DetectForegroundGame();
        // Тип видеопотока берётся не сейчас, а в момент создания файла (первый keyframe):
        // сразу после старта конвейера энкодер ещё не дописал в него заголовки кодека,
        // и файл, открытый с таким типом, не собирается на финализации.
        string file = ReserveFilePath(game, "recording");
        ManualRecorder recorder;
        try
        {
            recorder = new ManualRecorder(file, () => _encoder?.CloneOutputMediaType(),
                s.TrackMode, s.CaptureGameAudio, s.CaptureMicrophone);
        }
        catch
        {
            ReleaseFilePath(file);
            throw;
        }
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
        lock (_lifecycle)
        {
            // Важен даже вызов между двумя сегментами, когда _recorder уже null:
            // recovery не должен после него снова открыть файл.
            _continuousRecordingRequested = false;
            return StopRecordingLocked(wait);
        }
    }

    private string? StopRecordingLocked(bool wait)
    {
        var recorder = _recorder;
        if (recorder is null) return null;
        _recorder = null;
        // Одно чтение поля вместо двух: параллельный снос обнулял _encoder ровно
        // между проверкой и использованием
        var encoder = _encoder;
        if (encoder is not null) encoder.FrameEncoded -= recorder.OnFrame;
        _audio.BlockReady -= recorder.OnAudio;
        RecordingChanged?.Invoke(false);

        var finish = Task.Run(() =>
        {
            try
            {
                var result = recorder.Finish();

                // Индекс наполняем ТОЛЬКО удачными файлами. Раньше RegisterSaved шёл до
                // проверки Ok, и незакрытые части попадали в библиотеку и в статистику
                // хранилища как полноценные записи — карточка есть, а файла нет.
                if (result.Ok)
                {
                    foreach (var file in result.Files) _storage.RegisterSaved(file);
                    RecordingSaved?.Invoke(result.Files[0], Math.Max(result.Seconds, 1));
                }
                else SaveFailed?.Invoke(result.Error ?? "запись не закрылась");
            }
            catch (Exception ex)
            {
                Log.Error("Recorder", ex);
                SaveFailed?.Invoke(ex.Message);
            }
            finally
            {
                try { recorder.Dispose(); }
                finally { ReleaseFilePath(recorder.FilePath); }
            }
        });
        lock (_writerTasksSync) _writerTasks.Add(finish);
        _ = finish.ContinueWith(completed =>
        {
            lock (_writerTasksSync) _writerTasks.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (wait) finish.Wait(TimeSpan.FromSeconds(70));
        return recorder.FilePath;
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
