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

    // Кольцевой пул текстур-копий: CreateTexture2D на каждый кадр (60/с) — это
    // аллокации VRAM в горячем пути, в играх они дают фризы записи.
    // Слотов заметно больше, чем _maxInputQueue + кадры в работе у MFT, иначе
    // пул пойдёт по второму кругу и перезапишет текстуры, ещё лежащие в очереди.
    private ID3D11Texture2D?[] _copyPool = [];
    private int _copyPoolSize;
    private int _copyPoolIndex;

    /// <summary>
    /// Бюджет видеопамяти под пул текстур. На 1080p даёт ~70 кадров (≈1.2 с запаса
    /// на всплеск), на 4K упирается в нижнюю границу 24 — столько же, сколько было
    /// раньше на всех разрешениях, так что тяжёлые режимы ничего не теряют.
    /// </summary>
    private const long PoolVramBudgetBytes = 220L << 20;

    /// <summary>Глубина пула и очереди под конкретное разрешение.</summary>
    private void ConfigureQueueDepth(int width, int height)
    {
        long frameBytes = Math.Max((long)width * height * 3 / 2, 1); // NV12
        _copyPoolSize = (int)Math.Clamp(PoolVramBudgetBytes / frameBytes, 24, 96);
        _maxInputQueue = Math.Max(8, _copyPoolSize - 8); // запас на кадры в работе у MFT
        _copyPool = new ID3D11Texture2D?[_copyPoolSize];
        _copyPoolIndex = 0;
    }

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

    private static long NowQpcTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
               (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public void Initialize(ID3D11Device device, int width, int height, int fps, long bitrateBps, VideoCodec codec)
    {
        _device = device;
        Width = width; Height = height; Fps = fps;
        _frameDurationTicks = 10_000_000L / fps;
        ConfigureQueueDepth(width, height);

        // DXGI device manager — чтобы MFT работал на нашем D3D-устройстве
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device);

        Guid subtype = SubtypeFor(codec);

        (_transform, EncoderName) = FindHardwareEncoder(subtype) is { } found
            ? found
            : throw new NotSupportedException($"Аппаратный энкодер {codec} не найден. " +
               "Проверьте драйвер GPU или выберите H264.");
        EncoderVendor = VendorOf(EncoderName);
        EncoderLabel = $"{codec.ToString().ToLowerInvariant()}_{VendorTag(EncoderName)}";

        using var attrs = _transform.Attributes;
        // Асинхронный MFT обязателен к "разблокировке"
        attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        // Отдаём MFT наш D3D-девайс: вход принимается прямо как GPU-текстуры
        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)_deviceManager.NativePointer);

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

        Log.Info("Encoder", $"HW-энкодер инициализирован: {codec}, {width}x{height}@{fps}, {bitrateBps / 1_000_000} Mbps");
        Log.Info("Encoder", $"Очередь кодирования: {_maxInputQueue} кадров " +
            $"(~{_maxInputQueue * 1000 / Math.Max(fps, 1)} мс запаса), пул {_copyPoolSize} текстур " +
            $"= {(long)width * height * 3 / 2 * _copyPoolSize / (1024 * 1024)} МБ видеопамяти");
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

    private const uint MftEnumFlagHardware = 0x00000004;      // MFT_ENUM_FLAG_HARDWARE
    private const uint MftEnumFlagSortAndFilter = 0x00000040; // MFT_ENUM_FLAG_SORTANDFILTER

    /// <summary>
    /// MFVideoFormat_AV1 = {31305641-0000-0010-8000-00AA00389B71}.
    /// Data1 — это FourCC 'AV01' как DWORD little-endian: 'A'|'V'<<8|'0'<<16|'1'<<24.
    /// (Раньше здесь было 0x41313041 — то есть "A01A"; такого подтипа не существует,
    /// поэтому энкодер AV1 не находился даже на RTX 40, где он есть.)
    /// </summary>
    private static readonly Guid VideoFormatAV1 = new("31305641-0000-0010-8000-00AA00389B71");

    public static Guid SubtypeFor(VideoCodec codec) => codec switch
    {
        VideoCodec.HEVC => VideoFormatGuids.Hevc,
        VideoCodec.AV1  => VideoFormatAV1,
        _               => VideoFormatGuids.H264
    };

    private static string VendorOf(string mftName) =>
        mftName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : mftName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : mftName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
          mftName.Contains("Quick", StringComparison.OrdinalIgnoreCase) ? "Intel"
        : "GPU";

    private static string VendorTag(string mftName) => VendorOf(mftName) switch
    {
        "NVIDIA" => "nvenc", "AMD" => "amf", "Intel" => "qsv", _ => "hw"
    };

    private static IMFActivate[] EnumHardwareEncoders(Guid outputSubtype)
    {
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = outputSubtype };
        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            MftEnumFlagHardware | MftEnumFlagSortAndFilter,
            null, outInfo, out IntPtr pActivates, out uint count);

        var activates = new IMFActivate[count];
        for (int i = 0; i < count; i++)
            activates[i] = new IMFActivate(Marshal.ReadIntPtr(pActivates, i * IntPtr.Size));
        Marshal.FreeCoTaskMem(pActivates);
        return activates;
    }

    /// <summary>
    /// Умеет ли Windows положить этот кодек в MP4.
    ///
    /// Кодировать AV1 видеокарта может и на Windows 10 (RTX 40+ это умеет), но
    /// MP4-мультиплексор системы про AV1 не знает: SinkWriter падает с
    /// MF_E_SINK_HEADERS_NOT_FOUND уже на финализации файла — то есть запись идёт,
    /// а сохранить её невозможно. Поддержка появилась только в Windows 11.
    /// </summary>
    public static bool CanSaveToMp4(VideoCodec codec) =>
        codec != VideoCodec.AV1 || Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// Какие кодеки реально доступны аппаратно (по энумерации MFT) и чей энкодер
    /// будет выбран. Для инфопанели UI, конвейер не трогает.
    /// </summary>
    public static (string Vendor, List<VideoCodec> Codecs) ProbeSupport()
    {
        var codecs = new List<VideoCodec>();
        var names = new List<string>();
        foreach (VideoCodec codec in Enum.GetValues<VideoCodec>())
        {
            var activates = EnumHardwareEncoders(SubtypeFor(codec));
            try
            {
                if (activates.Length == 0) continue;
                codecs.Add(codec);
                foreach (var a in activates)
                    try { names.Add(a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)); } catch { }
            }
            finally { foreach (var a in activates) a.Dispose(); }
        }
        // Предпочтение как в реальном выборе: NVIDIA > Intel > AMD (у AMD активация чаще ломается)
        string vendor =
            names.Any(n => VendorOf(n) == "NVIDIA") ? "NVIDIA"
            : names.Any(n => VendorOf(n) == "Intel") ? "Intel"
            : names.Any(n => VendorOf(n) == "AMD") ? "AMD"
            : "—";
        return (vendor, codecs);
    }

    /// <summary>Поиск аппаратного MFT-энкодера для нужного выходного subtype.</summary>
    private static (IMFTransform Transform, string Name)? FindHardwareEncoder(Guid outputSubtype)
    {
        var activates = EnumHardwareEncoders(outputSubtype);
        try
        {
            // На гибридных системах (iGPU + dGPU) активация MFT одного из вендоров
            // может падать (например, AMD AMF с E_OUTOFMEMORY) — пробуем всех по очереди.
            foreach (var activate in activates)
            {
                string name = "?";
                try { name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                try
                {
                    var transform = activate.ActivateObject<IMFTransform>();
                    Log.Info("Encoder", $"Выбран MFT: {name}");
                    return (transform, name);
                }
                catch (Exception ex)
                {
                    Log.Warn("Encoder", $"MFT '{name}' не активировался: {ex.Message}");
                }
            }
            return null;
        }
        finally
        {
            foreach (var a in activates) a.Dispose();
        }
    }

    /// <summary>Тюнинг через ICodecAPI: CBR, GOP = 2 сек, low-latency. Ошибки не фатальны.</summary>
    private void ConfigureCodecApi(int fps, long bitrateBps)
    {
        IntPtr pCodecApi = IntPtr.Zero;
        try
        {
            Guid iid = typeof(ICodecAPI).GUID;
            Marshal.QueryInterface(_transform!.NativePointer, in iid, out pCodecApi);
            var codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(pCodecApi);

            SetCodecValue(codecApi, CodecApiGuids.AVEncCommonRateControlMode, 3u /* CBR */);
            SetCodecValue(codecApi, CodecApiGuids.AVEncCommonMeanBitRate, (uint)bitrateBps);
            SetCodecValue(codecApi, CodecApiGuids.AVEncMPVGOPSize, (uint)(fps * 2));
            SetCodecValue(codecApi, CodecApiGuids.AVEncCommonLowLatency, true);
            // Скорость важнее качества (0..100): под 100% загрузкой GPU игрой энкодер
            // на «качественном» пресете не вытягивает 60 fps — кадры дропаются.
            SetCodecValue(codecApi, CodecApiGuids.AVEncCommonQualityVsSpeed, 25u);
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder", $"ICodecAPI недоступен/частично применён: {ex.Message}");
        }
        finally
        {
            if (pCodecApi != IntPtr.Zero) Marshal.Release(pCodecApi);
        }
    }

    private static void SetCodecValue(ICodecAPI api, Guid guid, object value)
    {
        try { api.SetValue(ref guid, ref value); }
        catch (Exception ex) { Log.Warn("Encoder", $"ICodecAPI {guid}: {ex.Message}"); }
    }

    /// <summary>
    /// Подать кадр на кодирование. Текстура из пула VideoProcessorNv12 —
    /// делаем собственную GPU-копию, т.к. пул будет перезаписан.
    /// </summary>
    private ID3D11DeviceContext? _context;

    /// <summary>GPU-копия источника в следующий слот кольцевого пула.</summary>
    private ID3D11Texture2D CopyIntoPool(ID3D11Texture2D src)
    {
        int slot = _copyPoolIndex;
        _copyPoolIndex = (_copyPoolIndex + 1) % _copyPoolSize;

        var dst = _copyPool[slot];
        var srcDesc = src.Description;
        if (dst is null || dst.Description.Width != srcDesc.Width || dst.Description.Height != srcDesc.Height)
        {
            dst?.Dispose();
            srcDesc.BindFlags = Vortice.Direct3D11.BindFlags.None;
            srcDesc.MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.None;
            dst = _copyPool[slot] = _device!.CreateTexture2D(srcDesc);
        }
        lock (_device!) _context!.CopyResource(dst, src);
        return dst;
    }

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
        while (_running)
        {
            Thread.Sleep(4);
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
                    lock (_queueLock)
                        if (_inputQueue.Count >= _maxInputQueue - 1)
                            break; // очередь и так полна — дубликаты в неё не пихаем

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
                    _needInput.Release();
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
            catch (Exception ex)
            {
                if (!_running) break;
                Log.Error("Encoder", ex);
                Thread.Sleep(5);
            }
        }
    }

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
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder", $"Не удалось получить актуальный выходной тип: {ex.Message}");
        }
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
        if (hr.Failure) { ourSample?.Dispose(); return; }

        RefreshOutputTypeOnce();

        using IMFSample sample = outBuffer.Sample!;
        using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out IntPtr ptr, out _, out int currentLength);
        // ArrayPool вместо new byte[]: 60 аллокаций ~94 КБ/с — это LOH и GC-паузы,
        // которые фризили запись. Владение массивом переходит кольцевому буферу.
        var data = System.Buffers.ArrayPool<byte>.Shared.Rent(currentLength);
        Marshal.Copy(ptr, data, 0, currentLength);
        contiguous.Unlock();

        bool keyframe = false;
        try { keyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint) != 0; } catch { }

        Interlocked.Increment(ref FramesEncoded);
        Diagnostics.PipelineProbe.DrainOutput.Add(drainStart, Diagnostics.PipelineProbe.Now());
        FrameEncoded?.Invoke(new EncodedFrame(data, currentLength, sample.SampleTime, sample.SampleDuration, keyframe));
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
        for (int i = 0; i < _copyPool.Length; i++) { _copyPool[i]?.Dispose(); _copyPool[i] = null; }
        if (!exited)
        {
            // Поток так и висит в GetEvent — освобождать COM-объекты под ним нельзя
            // (это и был краш при выключении). Утечка одного MFT безопаснее.
            Log.Warn("Encoder", "Event-поток не завершился за 2 сек — MFT оставлен GC");
            _eventGen = null;
            _transform = null;
        }
        else
        {
            try { _transform?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
            _eventGen?.Dispose();
            _transform?.Dispose();
        }
        _deviceManager?.Dispose();
        OutputMediaType?.Dispose();
    }
}

/// <summary>
/// Ручной COM-интероп ICodecAPI: в Vortice.MediaFoundation 3.x обёртки нет.
/// Порядок методов строго по vtable (strmif.h), объявлены только нужные + предшествующие.
/// </summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] out object valueMin,
        [MarshalAs(UnmanagedType.Struct)] out object valueMax,
        [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);
    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);
    [PreserveSig] int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    void SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>GUID'ы ICodecAPI, которых может не быть в обёртке.</summary>
internal static class CodecApiGuids
{
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate     = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncMPVGOPSize            = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncCommonLowLatency      = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    public static readonly Guid AVEncCommonQualityVsSpeed  = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
}
