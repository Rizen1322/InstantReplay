using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Encoding;

/// <summary>
/// Аппаратное кодирование через Media Foundation Transform (MFT).
/// На NVIDIA это NVENC, на AMD — AMF/VCN, на Intel — QuickSync: MFTEnumEx с флагом
/// Hardware сам находит вендорский энкодер, поэтому "запись через видеокарту фулл"
/// работает на любом GPU без отдельных SDK.
///
/// Ключевое решение: мы НЕ используем SinkWriter для кодирования в файл.
/// MFT дёргается напрямую, сжатые сэмплы (несколько сотен КБ/с вместо гигабайт
/// сырого видео) складываются в кольцевой RAM-буфер. Файл создаётся только
/// в момент сохранения повтора (см. ReplaySaver) — простой remux без перекодирования.
///
/// Вход — NV12-текстура в VRAM (см. VideoProcessorNv12): никакого hwdownload,
/// кадр до самого NVENC не покидает видеопамять.
/// </summary>
public sealed class VideoEncoder : IDisposable
{
    public event Action<EncodedFrame>? FrameEncoded;

    public IMFMediaType? OutputMediaType { get; private set; }

    /// <summary>
    /// Независимая копия выходного типа — её отдают тем, кто переживёт энкодер.
    ///
    /// ЗАЧЕМ. Сохранение повтора и обычная запись работают в фоне и держат тип
    /// всё время записи файла. Если в это время конвейер остановят (выход из
    /// приложения, смена настроек, восстановление после потери устройства),
    /// <see cref="Dispose"/> освободит <see cref="OutputMediaType"/> прямо под
    /// работающим SinkWriter — это падение процесса без строчки в логе.
    /// Копия принадлежит вызывающему и живёт ровно столько, сколько ему нужно.
    ///
    /// null — энкодер ещё не отдал тип (первый keyframe не прошёл).
    /// </summary>
    public IMFMediaType? CloneOutputMediaType()
    {
        var source = OutputMediaType;   // присваивание ссылки атомарно
        if (source is null) return null;

        var copy = MediaFactory.MFCreateMediaType();
        try
        {
            source.CopyAllItems(copy);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }

    /// <summary>Читаемое имя выбранного MFT, например "NVIDIA H.264 Encoder MFT".</summary>
    public string EncoderName { get; private set; } = "";
    /// <summary>Короткая метка для UI в стиле ffmpeg: "h264_nvenc", "hevc_amf", "av1_qsv".</summary>
    public string EncoderLabel { get; private set; } = "";
    /// <summary>Вендор выбранного энкодера: NVIDIA / AMD / Intel / GPU.</summary>
    public string EncoderVendor { get; private set; } = "";

    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _eventGen;
    private IMFDXGIDeviceManager? _deviceManager;
    private ID3D11Device? _device;

    private Thread? _eventThread;
    private Thread? _feedThread;
    private volatile bool _running;

    // Запросы NeedInput от MFT. КРИТИЧНО: запрос нельзя терять — асинхронный MFT
    // повторно не просит. Потерянный запрос навсегда уменьшает глубину конвейера:
    // сначала дропы кадров, затем полная остановка кодирования (буфер «замерзает»).
    private readonly SemaphoreSlim _needInput = new(0);

    // Очередь входных кадров: энкодер забирает по METransformNeedInput.
    //
    // Глубина считается в Initialize от бюджета видеопамяти. Раньше было жёстко
    // 16 кадров с логикой «лучше дропнуть кадр, чем копить латентность» — но для
    // ЭТОГО приложения она неверна: мы не стримим, а пишем в кольцевой буфер,
    // и задержка кодирования даже в секунду не видна никак. Зато дроп кадра —
    // безвозвратная потеря плавности.
    //
    // Замеры на нагруженном GPU (GTA 3 DE, RTX 4060): ProcessInput в NVENC при
    // средних 8 мс даёт пики до 1068 мс — он ждёт, пока GPU домелет очередь команд
    // игры (наши CopyResource/VideoProcessorBlt асинхронные, вся их задержка
    // схлопывается сюда). Очередь в 16 кадров = 0.27 с, всплеск её переполнял:
    // из 3558 захваченных кадров в минуту кодировалось 2306, 1281 уходил в мусор.
    private int _maxInputQueue;
    private readonly Queue<(ID3D11Texture2D tex, long ticks)> _inputQueue = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _inputAvailable = new(0);
    private long _frameDurationTicks;

    /// <summary>Кольцо текстур-копий на входе (см. <see cref="EncoderTexturePool"/>).</summary>
    private EncoderTexturePool? _copyPool;

    // CFR: таймстампы кадров квантуются к сетке 1/fps. Сырые времена WGC привязаны
    // к vsync монитора (на 144 Гц — интервалы 13.9/20.8 мс вперемешку) — плееры
    // честно воспроизводят этот джиттер, и запись «дёргается», хотя кадры все на месте.
    private long _cfrBase = -1;
    private long _lastCfrPts;

    // Жёсткий CFR как у ShadowPlay: если захват не принёс новый кадр к дедлайну
    // слота сетки (WGC под нагрузкой пропускает, статичный экран и т.п.) — пейсер
    // подаёт ДУБЛИКАТ предыдущего кадра. В файле нет ни одной дыры: ровно fps
    // кадров в секунду всегда; дубликаты статики энкодер сжимает почти в ноль.
    private readonly object _cfrLock = new();
    private ID3D11Texture2D? _lastSubmittedTex;
    private Thread? _pacerThread;

    // Для пейсера: pts последнего РЕАЛЬНОГО кадра и наше wall-время его прихода.
    // Пауза меряется как (wallNow - _lastRealArrivalWall) — разность НАШИХ часов,
    // а цель заполнения строится от pts кадра: смещение эпох WGC/Stopwatch сокращается.
    private long _lastRealPts;
    private long _lastRealArrivalWall;

    // Статистика для диагностики (скидывается в лог движком)
    public long FramesSubmitted;
    public long FramesDuplicated;
    public long FramesDroppedQueue;
    public long FramesEncoded;
    /// <summary>
    /// Сколько раз MFT попросил кадр (METransformNeedInput). Это ПОТОЛОК скорости
    /// кодирования: подать больше, чем у нас попросили, нельзя. Если запросов 26 в
    /// секунду при настроенных 60 — упирается именно энкодер, а не захват и не диск,
    /// причём по времени ProcessInput этого не видно (сам вызов быстрый).
    /// </summary>
    public long InputRequests;
    /// <summary>
    /// Сколько кадров одновременно находится ВНУТРИ энкодера: отдали в ProcessInput,
    /// но ещё не забрали из ProcessOutput. Если ProcessInput подвисает на сотни
    /// миллисекунд и в этот момент число упирается в константу — значит он ждёт
    /// освобождения внутренней поверхности MFT, а не очереди команд GPU. Это две
    /// разные болезни с разным лечением.
    /// </summary>
    public long MaxInFlight;
    private int _inFlight;
    /// <summary>
    /// Сколько раз пейсер отказался ставить дубликат из-за переполненной очереди.
    /// Пока счётчик растёт, жёсткого CFR нет: в файле окажется меньше 60 кадров в
    /// секунду, и запись будет «дёргаться» независимо от того, что показывает игра.
    /// </summary>
    public long PacerBlocked;

    private static long NowQpcTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
               (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public void Initialize(ID3D11Device device, int width, int height, int fps, long bitrateBps, VideoCodec codec)
    {
        _device = device;
        Width = width; Height = height; Fps = fps;
        _frameDurationTicks = 10_000_000L / fps;

        _copyPool = new EncoderTexturePool(device, width, height);
        _maxInputQueue = Math.Max(8, _copyPool.Slots - 8); // запас на кадры в работе у MFT

        // DXGI device manager — чтобы MFT работал на нашем D3D-устройстве
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device);

        Guid subtype = HardwareEncoders.SubtypeFor(codec);

        (_transform, EncoderName) = HardwareEncoders.Find(subtype) is { } found
            ? found
            : throw new NotSupportedException($"Аппаратный энкодер {codec} не найден. " +
               "Проверьте драйвер GPU или выберите H264.");
        EncoderVendor = HardwareEncoders.VendorOf(EncoderName);
        EncoderLabel = $"{codec.ToString().ToLowerInvariant()}_{HardwareEncoders.VendorTag(EncoderName)}";

        using var attrs = _transform.Attributes;
        // Асинхронный MFT обязателен к "разблокировке"
        attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        // Отдаём MFT наш D3D-девайс: вход принимается прямо как GPU-текстуры
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)_deviceManager.NativePointer);

        // Часть ключей ICodecAPI энкодер принимает только ДО установки типов —
        // после он отвечает E_INVALIDARG. Здесь ровно те, что меняют структуру
        // потока (B-кадры), остальные настраиваются ниже, после типов.
        _codecApi = CodecApi.For(_transform);
        ConfigureCodecApiEarly(bitrateBps);

        // --- Выходной (сжатый) тип: у энкодеров задаётся ПЕРВЫМ ---
        var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, subtype);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrateBps);
        outType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
        outType.Set(MediaTypeAttributeKeys.FrameRate, PackLong(fps, 1));
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        // GOP 2 секунды: буфер режется по ключевым кадрам, значит минимальная
        // "гранулярность" начала клипа = 2 сек — разумный компромисс размер/точность.
        outType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)(fps * 2));
        if (codec == VideoCodec.H264)
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u /* eAVEncH264VProfile_High */);
        SetColorInfo(outType);
        _transform.SetOutputType(0, outType, 0);
        OutputMediaType = outType;

        // --- Входной тип: NV12 того же размера ---
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
        inType.Set(MediaTypeAttributeKeys.FrameRate, PackLong(fps, 1));
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        SetColorInfo(inType);
        _transform.SetInputType(0, inType, 0);

        ConfigureCodecApi(fps, bitrateBps);

        _eventGen = _transform.QueryInterface<IMFMediaEventGenerator>();

        _running = true;
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "VideoEncoder.Events", Priority = ThreadPriority.AboveNormal };
        _eventThread.Start();
        _feedThread = new Thread(FeedLoop) { IsBackground = true, Name = "VideoEncoder.Feed", Priority = ThreadPriority.AboveNormal };
        _feedThread.Start();
        _pacerThread = new Thread(PacerLoop) { IsBackground = true, Name = "VideoEncoder.Pacer", Priority = ThreadPriority.AboveNormal };
        _pacerThread.Start();

        int slots = _copyPool.Slots;
        Log.Info("Encoder", $"HW-энкодер инициализирован: {codec}, {width}x{height}@{fps}, {bitrateBps / 1_000_000} Mbps");
        Log.Info("Encoder", $"Очередь кодирования: {_maxInputQueue} кадров " +
            $"(~{_maxInputQueue * 1000 / Math.Max(fps, 1)} мс запаса), пул {slots} текстур " +
            $"= {(long)width * height * 3 / 2 * slots / (1024 * 1024)} МБ видеопамяти");
    }

    // MF_MT_YUV_MATRIX — в Vortice 3.x ключа нет, задаём по GUID
    private static readonly Guid MfMtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");

    /// <summary>
    /// Цветовые метаданные BT.709 limited (16-235) — ровно то, в чём VideoProcessor
    /// формирует NV12. Без них энкодер не пишет VUI, и плееры угадывают цвета сами.
    /// </summary>
    private static void SetColorInfo(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u); // MFNominalRange_16_235
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);    // MFVideoPrimaries_BT709
        type.Set(MediaTypeAttributeKeys.TransferFunction, 5u);  // MFVideoTransFunc_709
        type.Set(MfMtYuvMatrix, 1u);                            // MFVideoTransferMatrix_BT709
    }

    // ---------------- Настройка энкодера ----------------

    /// <summary>Интерфейс настройки энкодера; null — этот MFT его не отдаёт.</summary>
    private CodecApi? _codecApi;

    /// <summary>Адаптивный пресет качество/скорость.</summary>
    private QualityAdapter? _quality;

    /// <summary>Текущий пресет — показывается в статистике конвейера.</summary>
    public uint QualityPreset => _quality?.Preset ?? QualityAdapter.Balanced;

    /// <summary>
    /// Ключи ICodecAPI, которые задаются ДО установки медиатипов.
    ///
    /// B-кадры — главная причина, по которой энкодер держит кадры в себе и реже
    /// присылает NeedInput: чтобы сжать B-кадр, нужен следующий за ним опорный.
    /// Для записи геймплея они не нужны, а задержку конвейера дают прямую.
    /// После SetOutputType этот ключ NVIDIA отвечает E_INVALIDARG — структура
    /// потока к тому моменту уже зафиксирована.
    /// </summary>
    private void ConfigureCodecApiEarly(long bitrateBps)
    {
        if (_codecApi is null) return;

        // NVIDIA этот ключ не поддерживает ни до, ни после установки типов;
        // на её энкодере B-кадры и так выключены режимом низкой задержки.
        // Оставлено ради Intel/AMD, где ключ работает.
        _codecApi.Set(CodecApiGuids.AVEncMPVDefaultBPictureCount, 0u, optional: true);

        // Буфер VBV — тоже структурный параметр. Заданный ПОСЛЕ медиатипов он
        // принимается без ошибки, но энкодер продолжает жить со своим: в замерах
        // читалось 3 Мбит при заданных 40 (и с режимом низкой задержки, и без него).
        // Пробуем до типов — по той же причине, по которой сюда попали B-кадры.
        _codecApi.Set(CodecApiGuids.AVEncCommonBufferSize, (uint)bitrateBps, optional: true);
    }

    /// <summary>Тюнинг через ICodecAPI: CBR, GOP = 2 сек, low-latency. Ошибки не фатальны.</summary>
    private void ConfigureCodecApi(int fps, long bitrateBps)
    {
        if (_codecApi is null) return;

        // eAVEncCommonRateControlMode: 0 = CBR, 1 = PeakConstrainedVBR,
        // 2 = UnconstrainedVBR, 3 = Quality.
        //
        // Здесь стояло 3 с комментарием «CBR». Тройка — это режим ПО КАЧЕСТВУ:
        // энкодер сам выбирает битрейт под свой внутренний уровень качества, а
        // заданный пользователем игнорирует. В замерах по сохранённым клипам
        // фактический битрейт гулял от 15.0 до 58.1 Мбит/с при настройке 50 —
        // на простой сцене энкодер опускался втрое, и запись заметно проигрывала
        // NVIDIA App на тех же настройках (она пишет CBR).
        _codecApi.Set(CodecApiGuids.AVEncCommonRateControlMode, 0u /* CBR */);
        _codecApi.Set(CodecApiGuids.AVEncCommonMeanBitRate, (uint)bitrateBps);
        _codecApi.Set(CodecApiGuids.AVEncMPVGOPSize, (uint)(fps * 2));
        // AVEncCommonLowLatency энкодер NVIDIA отвергает с E_INVALIDARG в ЛЮБОМ
        // виде (пробовали и VT_BOOL, и VT_UI4) — в логе это годами висело как
        // «ICodecAPI 9d3ecd55…: Value does not fall within the expected range».
        // Оставляем документированный VT_BOOL для тех MFT, где ключ работает,
        // а до NVENC добираемся двумя другими ключами ниже.
        _codecApi.Set(CodecApiGuids.AVEncCommonLowLatency, true, optional: true);

        // Режим низкой задержки: под чужой нагрузкой (рядом стримит Discord) он
        // поднял пропускную способность энкодера с 26 до 42 кадров в секунду.
        // Подозревали, что он же ужимает буфер VBV до 3 Мбит — проверили с ним и
        // без него, буфер был одинаковый. Дело было в другом: VBV принимается
        // только ДО установки медиатипов (см. ConfigureCodecApiEarly).
        _codecApi.Set(CodecApiGuids.AVLowLatencyMode, true);

        // Буфер VBV — запас, из которого энкодер берёт биты на резкое усложнение
        // картинки, не разваливая её в блоки. Задаётся СТРОГО ПОСЛЕ режима низкой
        // задержки: тот выставляет свой крошечный буфер (в замере — 3 Мбит при
        // 40 Мбит/с, меньше пяти кадров), и наше значение, выставленное раньше,
        // затиралось. Секунда — как у ShadowPlay.
        _codecApi.Set(CodecApiGuids.AVEncCommonBufferSize, (uint)bitrateBps, optional: true);

        // Стартовый пресет ставит сам адаптер — он же решает, поддерживает ли его
        // этот энкодер, и дальше подстраивает под нагрузку.
        _quality = new QualityAdapter(_codecApi, fps);

        _codecApi.LogRateControl();
        _codecApi.LogSupport();
    }

    // ---------------- Ключевые кадры по времени ----------------

    /// <summary>Как часто в буфере обязан появляться keyframe. Тики (100 нс).</summary>
    private const long KeyframeIntervalTicks = 2 * 10_000_000L;

    /// <summary>Не просить чаще, чем раз в полсекунды: запрос отрабатывает не мгновенно.</summary>
    private const long KeyframeRequestGapTicks = 5_000_000L;

    private long _lastKeyframeTicks = long.MinValue;
    private long _lastKeyframeRequestTicks = long.MinValue;
    private bool _forceKeyframeUnavailable;

    /// <summary>
    /// Попросить энкодер выдать ключевой кадр, если по ЧАСАМ их давно не было.
    ///
    /// Зачем вообще: и AVEncMPVGOPSize, и MF_MT_MAX_KEYFRAME_SPACING заданы В КАДРАХ
    /// (так они и описаны у Microsoft), а не в секундах. Мы ставим fps*2 в расчёте
    /// на «keyframe каждые 2 секунды» — но это верно, только пока энкодер реально
    /// принимает заданные fps. Стоит ему просесть (слабая видеокарта, чужая нагрузка,
    /// дропы перед подачей), и те же 120 кадров растягиваются на 6, 10, 25 секунд.
    ///
    /// Буфер повтора режется строго по keyframe, поэтому редкие ключевые кадры дают
    /// ровно то, что видно у пользователя: длина буфера гуляет вокруг заказанной на
    /// целый GOP, а сохранённый клип оказывается длиннее настройки.
    /// </summary>
    private void MaybeForceKeyframe(long sampleTicks)
    {
        if (_forceKeyframeUnavailable || _codecApi is null) return;
        if (_lastKeyframeTicks != long.MinValue && sampleTicks - _lastKeyframeTicks < KeyframeIntervalTicks) return;
        if (_lastKeyframeRequestTicks != long.MinValue &&
            sampleTicks - _lastKeyframeRequestTicks < KeyframeRequestGapTicks) return;

        _lastKeyframeRequestTicks = sampleTicks;

        // Строго прямым вызовом: мы на потоке питателя, а обёртка .NET с чужого
        // потока отвечает E_NOINTERFACE (см. CodecApi).
        if (_codecApi.SetDirect(CodecApiGuids.AVEncVideoForceKeyFrame, 1u, out string error)) return;

        // Ключ не поддержан — больше не дёргаем. Останется штатный GOP по кадрам.
        _forceKeyframeUnavailable = true;
        Log.Info("Encoder", $"Ключевой кадр по требованию недоступен ({error})");
    }

    /// <summary>
    /// Подать кадр на кодирование. Текстура из пула VideoProcessorNv12 —
    /// делаем собственную GPU-копию, т.к. пул будет перезаписан.
    /// </summary>
    private ID3D11DeviceContext? _context;

    /// <summary>GPU-копия источника в следующий слот кольцевого пула.</summary>
    private ID3D11Texture2D CopyIntoPool(ID3D11Texture2D src) => _copyPool!.Copy(src, _context!);

    // Дозаполнение коротких пропусков сетки задним числом (гонка пейсера с реальным
    // кадром неизбежна — пейсер может не успеть за 4-мс окно). Длинные паузы
    // закрывает пейсер в реальном времени.
    private const int MaxBackfillSlots = 8;

    public void SubmitFrame(ID3D11Texture2D nv12PoolTexture, long ticks, ID3D11DeviceContext context)
    {
        if (!_running) return;
        _context ??= context;

        lock (_cfrLock)
        {
            var copy = CopyIntoPool(nv12PoolTexture);

            // Квантование PTS к сетке CFR
            long pts;
            if (_cfrBase < 0)
            {
                _cfrBase = ticks;
                _lastCfrPts = ticks;
                pts = ticks;
            }
            else
            {
                long n = (ticks - _cfrBase + _frameDurationTicks / 2) / _frameDurationTicks;
                pts = _cfrBase + n * _frameDurationTicks;

                // Слот занят (дубликатом от пейсера или предыдущим кадром) — НЕ выбрасываем
                // реальный кадр, а ставим в следующий слот: живое движение всегда лучше
                // повтора. Раньше так терялось ~90 настоящих кадров в минуту, и вместо них
                // в записи оставались замершие дубликаты — это и читалось как «меньше fps».
                if (pts <= _lastCfrPts)
                    pts = _lastCfrPts + _frameDurationTicks;

                // Пропущенные слоты между прошлым кадром и этим — дубликаты задним числом
                if (_lastSubmittedTex is not null &&
                    pts - _lastCfrPts <= _frameDurationTicks * (MaxBackfillSlots + 1))
                {
                    while (_lastCfrPts + _frameDurationTicks < pts)
                    {
                        _lastCfrPts += _frameDurationTicks;
                        var dup = CopyIntoPool(_lastSubmittedTex);
                        _lastSubmittedTex = dup;
                        Interlocked.Increment(ref FramesDuplicated);
                        Enqueue(dup, _lastCfrPts);
                    }
                }
                _lastCfrPts = pts;
            }
            _lastSubmittedTex = copy;
            _lastRealPts = pts;
            _lastRealArrivalWall = NowQpcTicks();
            Interlocked.Increment(ref FramesSubmitted);
            Enqueue(copy, pts);
        }
    }

    private void Enqueue(ID3D11Texture2D tex, long pts)
    {
        lock (_queueLock)
        {
            // Не даём очереди расти: лучше дропнуть кадр, чем накапливать латентность
            while (_inputQueue.Count >= _maxInputQueue)
            {
                _inputQueue.Dequeue(); // текстура принадлежит пулу — не Dispose
                if (_inputAvailable.CurrentCount > 0) _inputAvailable.Wait(0);
                Interlocked.Increment(ref FramesDroppedQueue);
            }
            _inputQueue.Enqueue((tex, pts));
            Diagnostics.PipelineProbe.ReportQueueDepth(_inputQueue.Count);
        }
        _inputAvailable.Release();
    }

    /// <summary>
    /// Пейсер жёсткого CFR: длинные паузы захвата (статичный экран, меню) заполняет
    /// дубликатами последнего кадра в реальном времени. Каждый дубликат — СВОЯ копия
    /// в пуле: подача одной текстуры повторно заставляет NVENC сериализоваться на
    /// поверхности, и конвейер рушится до ~10 fps. Допуск 1.75 кадра — настоящий
    /// кадр слота всегда в приоритете; короткие гонки добирает backfill в SubmitFrame.
    /// </summary>
    private void PacerLoop()
    {
        // Пейсер — обычный фоновый поток, и необработанное исключение в нём убивает
        // процесс целиком, без шанса что-то записать в лог. Ронять запись из-за
        // одного не сдублированного кадра незачем: постоянный fps — удобство, а не
        // условие работоспособности.
        try
        {
            PacerLoopCore();
        }
        catch (Exception ex)
        {
            Log.Error("Encoder", $"Пейсер остановлен: {ex.Message}");
        }
    }

    private void PacerLoopCore()
    {
        while (_running)
        {
            Thread.Sleep(4);
            // Вне _cfrLock: смена пресета не должна держать подачу кадров
            _quality?.Tick(Interlocked.Read(ref FramesEncoded),
                           Interlocked.Read(ref PacerBlocked),
                           Interlocked.Read(ref FramesDroppedQueue));
            lock (_cfrLock)
            {
                if (_lastSubmittedTex is null || _cfrBase < 0 || _context is null) continue;
                // Пауза = сколько НАШЕГО времени прошло без реальных кадров; цель
                // заполнения отсчитывается от pts последнего кадра (часы WGC).
                // Отступ 5 кадров от «сейчас»: реальный кадр, идущий с задержкой
                // доставки 20-30 мс, всегда успевает занять свой слот первым.
                long silence = NowQpcTicks() - _lastRealArrivalWall;
                long fillTarget = _lastRealPts + silence - _frameDurationTicks * 5;
                int catchUp = 0;
                while (_lastCfrPts + _frameDurationTicks <= fillTarget && catchUp++ < 4)
                {
                    bool queueFull;
                    lock (_queueLock) queueFull = _inputQueue.Count >= _maxInputQueue - 1;
                    if (queueFull)
                    {
                        // Очередь и так полна — дубликаты в неё не пихаем. Но это значит,
                        // что сетка CFR рвётся: считаем такие случаи, иначе провал fps
                        // в файле выглядит как «непонятно почему».
                        Interlocked.Increment(ref PacerBlocked);
                        break;
                    }

                    long pts = _lastCfrPts + _frameDurationTicks;
                    _lastCfrPts = pts;
                    var dup = CopyIntoPool(_lastSubmittedTex);
                    _lastSubmittedTex = dup;
                    Interlocked.Increment(ref FramesDuplicated);
                    Enqueue(dup, pts);
                }
            }
        }
    }

    /// <summary>
    /// Цикл событий асинхронного MFT. Блокирующий GetEvent — нулевая задержка реакции.
    /// NeedInput здесь только учитывается (кормит отдельный поток), HaveOutput забирается сразу.
    /// При Dispose поток будится командой Drain (MFT шлёт DrainComplete).
    /// </summary>
    private void EventLoop()
    {
        while (_running)
        {
            IMFMediaEvent? ev = null;
            try
            {
                ev = _eventGen!.GetEvent(0);
                if (!_running) break;
                var type = ev.EventType;

                if (type == MediaEventTypes.TransformNeedInput)
                {
                    Interlocked.Increment(ref InputRequests);
                    _needInput.Release();
                }
                else if (type == MediaEventTypes.TransformHaveOutput)
                    DrainOutput();
            }
            catch (Exception ex)
            {
                if (!_running) break; // остановка: объекты освобождаются, выходим тихо
                Log.Error("Encoder", ex);
                Thread.Sleep(5); // не молотим бесконечный цикл ошибок
            }
            finally { ev?.Dispose(); }
        }
    }

    /// <summary>
    /// Питатель: на каждый запрос MFT ждёт кадр СКОЛЬКО УГОДНО долго (статичный экран,
    /// меню, пауза — кадров нет минутами, это нормально) и подаёт его. ProcessInput из
    /// отдельного потока — штатный режим асинхронного MFT.
    /// </summary>
    private void FeedLoop()
    {
        while (_running)
        {
            try
            {
                if (!_needInput.Wait(200)) continue;      // ждём запрос NeedInput
                bool gotFrame = false;
                while (_running && !(gotFrame = _inputAvailable.Wait(200))) { } // ждём кадр, запрос держим
                if (!gotFrame) break;

                (ID3D11Texture2D tex, long ticks) item;
                lock (_queueLock)
                {
                    if (_inputQueue.Count == 0)
                    {
                        // Кадр успел перехватить дроп-механизм очереди — возвращаем
                        // «долг» NeedInput и ждём следующий кадр, запрос не теряем.
                        _needInput.Release();
                        continue;
                    }
                    item = _inputQueue.Dequeue();
                }

                int inFlight = Interlocked.Increment(ref _inFlight);
                long peak;
                while (inFlight > (peak = Interlocked.Read(ref MaxInFlight)))
                    if (Interlocked.CompareExchange(ref MaxInFlight, inFlight, peak) == peak) break;

                // Просим keyframe ДО подачи кадра: ключ действует на следующий вход.
                MaybeForceKeyframe(item.ticks);

                try
                {
                    long piStart = Diagnostics.PipelineProbe.Now();
                    using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                        typeof(ID3D11Texture2D).GUID, item.tex, 0, false);
                    using var sample = MediaFactory.MFCreateSample();
                    sample.AddBuffer(buffer);
                    sample.SampleTime = item.ticks;
                    sample.SampleDuration = _frameDurationTicks;
                    _transform!.ProcessInput(0, sample, 0);
                    Diagnostics.PipelineProbe.ProcessInput.Add(piStart, Diagnostics.PipelineProbe.Now());
                    // текстура из кольцевого пула — не Dispose, слот переиспользуется
                }
                catch
                {
                    // Кадр внутрь MFT не попал — счётчик «в работе» обязан вернуться.
                    // Иначе он уползает вверх навсегда, а по нему в статистике
                    // различают две РАЗНЫЕ причины просадки: энкодер ждёт свою
                    // внутреннюю поверхность (число упирается в потолок) или ждёт
                    // очередь команд GPU (число низкое). Соврав здесь, мы отправляем
                    // разбор в неверную сторону.
                    Interlocked.Decrement(ref _inFlight);
                    throw;
                }
            }
            catch (Exception ex)
            {
                if (!_running) break;
                Log.Error("Encoder", ex);
                Thread.Sleep(5);
            }
        }
    }

    /// <summary>
    /// Буфер под сжатый кадр, переиспользуется между вызовами DrainOutput.
    /// Живёт только на время события FrameEncoded — подписчики копируют данные себе.
    /// </summary>
    private byte[] _scratch = new byte[256 * 1024];

    private bool _outputTypeRefreshed;

    /// <summary>
    /// После первого сжатого кадра забираем у MFT ФАКТИЧЕСКИЙ выходной тип.
    /// В него энкодер дописывает заголовки кодека (SPS/PPS у H.264, av1C у AV1) —
    /// без них MP4-мультиплексор не может собрать файл и Finalize падает с
    /// MF_E_SINK_HEADERS_NOT_FOUND. Тип, который мы задавали при инициализации,
    /// этих данных не содержит.
    /// </summary>
    private void RefreshOutputTypeOnce()
    {
        if (_outputTypeRefreshed) return;
        _outputTypeRefreshed = true;
        try
        {
            var current = _transform!.GetOutputCurrentType(0);
            // Старый тип НЕ освобождаем: его мог уже captured сохраняющий поток.
            // Это одна COM-обёртка за сеанс — дешевле, чем гонка при сохранении.
            OutputMediaType = current; // присваивание ссылки атомарно
            Log.Info("Encoder", "Выходной тип обновлён из энкодера (с заголовками кодека)");
            LogColorInfo(current);
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder", $"Не удалось получить актуальный выходной тип: {ex.Message}");
        }
    }

    /// <summary>
    /// Цветовые метаданные ФАКТИЧЕСКОГО выходного типа энкодера.
    ///
    /// Мы задаём BT.709 limited при инициализации, но MFT вправе выставить в своём
    /// типе что угодно, а в MP4 уезжает именно он (ReplaySaver и ManualRecorder
    /// открывают поток этим типом). Расхождение здесь не ломает файл, но плеер
    /// растянет или сожмёт диапазон яркости — картинка будет выглядеть хуже при
    /// том же битрейте. Поэтому пишем фактические значения в лог.
    /// </summary>
    private static void LogColorInfo(IMFMediaType type)
    {
        static string Name(IMFMediaType t, Guid key, string[] names)
        {
            try
            {
                uint v = t.GetUInt32(key);
                return v < names.Length ? $"{names[v]}" : $"код {v}";
            }
            catch { return "не задано"; }
        }

        string range = Name(type, MediaTypeAttributeKeys.VideoNominalRange,
            ["неизвестно", "0-255 full", "16-235 limited", "48-208", "64-127"]);
        string primaries = Name(type, MediaTypeAttributeKeys.VideoPrimaries,
            ["неизвестно", "reserved", "BT.709", "BT.470-2 M", "BT.470-2 BG", "SMPTE170M", "SMPTE240M"]);
        string transfer = Name(type, MediaTypeAttributeKeys.TransferFunction,
            ["неизвестно", "linear", "gamma 1.8", "gamma 2.0", "gamma 2.2", "BT.709", "SMPTE240M", "sRGB"]);
        string matrix = Name(type, MfMtYuvMatrix,
            ["неизвестно", "BT.709", "BT.601", "SMPTE240M"]);

        Log.Info("Encoder", $"Цвет в выходном типе: диапазон {range}, матрица {matrix}, " +
                            $"первичные {primaries}, гамма {transfer}");
    }

    private void DrainOutput()
    {
        long drainStart = Diagnostics.PipelineProbe.Now();
        var streamInfo = _transform!.GetOutputStreamInfo(0);
        bool providesSamples = (streamInfo.Flags & (int)(
            OutputStreamInfoFlags.OutputStreamProvidesSamples |
            OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;

        var outBuffer = new OutputDataBuffer { StreamID = 0 };
        IMFSample? ourSample = null;
        if (!providesSamples)
        {
            ourSample = MediaFactory.MFCreateSample();
            ourSample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(streamInfo.Size));
            outBuffer.Sample = ourSample;
        }

        var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);
        // Неудача — это чаще всего MF_E_TRANSFORM_NEED_MORE_INPUT: отдавать нечего,
        // и кадры законно остаются ВНУТРИ энкодера. Счётчик здесь трогать нельзя,
        // он опускается ровно тогда, когда кадр действительно вышел наружу.
        if (hr.Failure) { ourSample?.Dispose(); return; }
        Interlocked.Decrement(ref _inFlight);

        RefreshOutputTypeOnce();

        using IMFSample sample = outBuffer.Sample!;
        using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out IntPtr ptr, out _, out int currentLength);
        // Один переиспользуемый буфер вместо аллокации на кадр. DrainOutput зовётся
        // только из потока событий MFT, поэтому синхронизация не нужна, а подписчики
        // события обязаны скопировать данные себе (см. EncodedFrame) — оба так и делают.
        // Раньше здесь брался массив из ArrayPool и уходил во владение буферу: 60
        // массивов в секунду, каждый крупнее порога больших объектов, разных размеров.
        if (_scratch.Length < currentLength)
            _scratch = GC.AllocateUninitializedArray<byte>(Math.Max(currentLength, _scratch.Length * 2));
        Marshal.Copy(ptr, _scratch, 0, currentLength);
        contiguous.Unlock();

        bool keyframe = false;
        try { keyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; } catch { }
        // Отсчёт «давно ли был ключевой» ведём по факту выдачи, а не по нашим просьбам
        if (keyframe) _lastKeyframeTicks = sample.SampleTime;

        Interlocked.Increment(ref FramesEncoded);
        Diagnostics.PipelineProbe.DrainOutput.Add(drainStart, Diagnostics.PipelineProbe.Now());
        FrameEncoded?.Invoke(new EncodedFrame(_scratch, 0, currentLength,
                                              sample.SampleTime, sample.SampleDuration, keyframe));
    }

    private static ulong PackLong(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public void Dispose()
    {
        _running = false;
        // Drain будит поток, застрявший в блокирующем GetEvent: асинхронный MFT
        // в ответ обязан прислать METransformDrainComplete.
        try
        {
            _transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform?.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        }
        catch { }

        _needInput.Release(4);      // будим питателя, если ждал запрос
        _inputAvailable.Release(4); // и если ждал кадр
        _feedThread?.Join(2000);
        _pacerThread?.Join(1000);
        bool exited = _eventThread?.Join(2000) ?? true;
        lock (_queueLock) _inputQueue.Clear();
        _copyPool?.Dispose(); _copyPool = null;
        if (!exited)
        {
            // Поток так и висит в GetEvent — освобождать COM-объекты под ним нельзя
            // (это и был краш при выключении). Утечка одного MFT безопаснее.
            Log.Warn("Encoder", "Event-поток не завершился за 2 сек — MFT оставлен GC");
            _eventGen = null;
            _transform = null;
            _codecApi?.Abandon();   // отпускать нельзя: MFT ещё используется висящим потоком
        }
        else
        {
            try { _transform?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
            _eventGen?.Dispose();
            // Своя ссылка на ICodecAPI отпускается вместе с MFT и только вместе с ним:
            // пока трансформ жив, из него могут прийти запросы ключевого кадра.
            _codecApi?.Release();
            _transform?.Dispose();
        }
        _codecApi = null;
        _deviceManager?.Dispose();
        OutputMediaType?.Dispose();
    }
}
