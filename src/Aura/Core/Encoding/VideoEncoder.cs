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

    /// <summary>
    /// Энкодер сломался и сам уже не оправится (неисправимая ошибка NVENC). Движок
    /// пересобирает конвейер; прямой NVENC к этому моменту выключен до перезапуска,
    /// так что новый энкодер будет MFT.
    /// </summary>
    public event Action<string>? Failed;

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

    /// <summary>
    /// Параметры сжатого потока (SPS/PPS/VPS и аналог). Они появляются после
    /// первого keyframe и определяют, можно ли смешивать кадры двух сессий MFT.
    /// </summary>
    public byte[]? TryGetSequenceHeader()
    {
        if (_nvenc is not null || _nvencHeader is not null) return _nvencHeader;
        var type = OutputMediaType;
        if (type is null) return null;
        try { return type.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); }
        catch { return null; }
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
    private readonly record struct EncoderInputFrame(
        ID3D11Texture2D Texture,
        long Ticks,
        bool IsDuplicate);

    private readonly Queue<EncoderInputFrame> _inputQueue = new();
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
    public long FramesDroppedRealQueue;
    /// <summary>
    /// Насколько время кадра в записи отходит от времени его захвата (за окно
    /// статистики). Больше кадра — видео уезжает от звука.
    /// </summary>
    public long MaxPtsLeadTicks = long.MinValue, MinPtsLeadTicks = long.MaxValue;
    /// <summary>Кадры, пришедшие позже, чем их слот закрыли повторы, — не записаны.</summary>
    public long FramesDroppedLate;
    /// <summary>Кадры, потерянные из-за того, что все слоты пула держал энкодер.</summary>
    public long FramesDroppedNoSlot;
    public long FramesDiscardedDuplicates;
    public long FramesSuppressedDuplicates;
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
    /// Сколько раз пейсер отказался ставить дубликат из-за давления очереди/MFT.
    /// Пока счётчик растёт, жёсткого CFR нет: в файле окажется меньше 60 кадров в
    /// секунду, и запись будет «дёргаться» независимо от того, что показывает игра.
    /// </summary>
    public long PacerBlocked;

    /// <summary>Сколько кадров сейчас ждёт в очереди — для посекундной диагностики.</summary>
    public int QueueDepth { get { lock (_queueLock) return _inputQueue.Count; } }

    /// <summary>
    /// Очередь забита: новый реальный кадр войдёт в неё только вместо другого.
    ///
    /// Нужна ДО преобразования кадра в NV12. Раньше порядок был обратный: кадр
    /// сначала прогонялся через видеопроцессор и копировался в пул, и лишь потом
    /// выяснялось, что в очереди нет места и кто-то будет вытеснен. Пока энкодер
    /// работает, это стоит недорого. Но когда он встаёт — а встаёт он ровно тогда,
    /// когда видеокарта занята игрой, — получается худшее из возможного: на
    /// перегруженной видеокарте мы делаем по сорок преобразований в секунду, все
    /// результаты выбрасываем и этим же продлеваем затор. В логе такой эпизод
    /// выглядел как «запросов MFT 0, очередь 33/33, дропов 46» подряд секунд по
    /// двадцать.
    /// </summary>
    public bool InputQueueSaturated { get { lock (_queueLock) return _inputQueue.Count >= _maxInputQueue; } }

    /// <summary>Кадры, не дошедшие даже до преобразования: очередь была забита.</summary>
    public long FramesSkippedBeforeConvert;

    /// <summary>Размер кольца текстур и потолок очереди — их считает бюджет видеопамяти.</summary>
    public int PoolSlots => _copyPool?.Slots ?? 0;
    public int MaxQueue => _maxInputQueue;

    private static long NowQpcTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
               (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>Пишем ли мы сейчас десять бит. Решается при инициализации.</summary>
    public bool TenBit { get; private set; }

    public void Initialize(ID3D11Device device, int width, int height, int fps, long bitrateBps,
                           VideoCodec codec, bool preferTenBit = false)
    {
        _device = device;
        Width = width; Height = height; Fps = fps;
        _frameDurationTicks = 10_000_000L / fps;

        // Десять бит просим у HEVC и AV1. У H.264 десятибитный профиль почти не
        // поддерживается плеерами, а NVENC его не умеет вовсе. AV1 Main по стандарту
        // включает 10 бит, и все декодеры AV1 (расширение Windows, браузеры,
        // видеокарты с аппаратным AV1) его понимают. Если конкретный MFT не примет
        // P010 или контейнер не соберётся, ниже и в движке есть откат на 8 бит.
        TenBit = preferTenBit && SupportsTenBit(codec);

        // На NVIDIA сначала прямой NVENC: у него есть то, чего нет в MFT (просмотр
        // вперёд, адаптивное квантование, B-кадры). Не открылся — обычный путь MFT.
        if (TryInitializeNvenc(device, width, height, fps, bitrateBps, codec)) return;

        _copyPool = new EncoderTexturePool(device, width, height, TenBit);
        _maxInputQueue = Math.Max(8, _copyPool.Slots - 8); // запас на кадры в работе у MFT

        // DXGI device manager — чтобы MFT работал на нашем D3D-устройстве
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(device);

        Guid subtype = HardwareEncoders.SubtypeFor(codec);

        (_transform, EncoderName) = HardwareEncoders.Find(subtype, AdapterLuidOf(device)) is { } found
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
        else
            // Номера профилей у HEVC и AV1 совпадают: eAVEncH265VProfile_Main_420_8/10
            // и eAVEncAV1VProfile_Main_420_8/10 — это 1 и 2 (codecapi.h).
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, TenBit ? Main10Profile : Main8Profile);
        SetColorInfo(outType);
        _transform.SetOutputType(0, outType, 0);
        OutputMediaType = outType;

        LogInputFormats();

        // --- Входной тип: NV12 или P010 того же размера ---
        //
        // Порядок здесь обратный привычному: выходной тип у энкодеров задаётся
        // первым, а профиль живёт именно в нём. Поэтому десятибитный профиль
        // приходится объявлять ДО того, как выяснится, примет ли энкодер P010 на
        // вход. Если не примет, ниже мы переобъявим оба типа восьмибитными: пока
        // поток не пошёл, менять типы можно сколько угодно.
        if (!TrySetInputFormat(width, height, fps, TenBit) && TenBit)
        {
            Log.Info("Encoder", "Энкодер не принял P010 — возвращаюсь к восьми битам");
            TenBit = false;

            // Профиль живёт в выходном типе, поэтому его надо переобъявить целиком.
            var eightBitOut = MediaFactory.MFCreateMediaType();
            outType.CopyAllItems(eightBitOut);
            eightBitOut.Set(MediaTypeAttributeKeys.Mpeg2Profile, Main8Profile);
            _transform.SetOutputType(0, eightBitOut, 0);
            OutputMediaType = eightBitOut;
            outType.Dispose();

            if (!TrySetInputFormat(width, height, fps, tenBit: false))
                throw new InvalidOperationException("Энкодер не принял ни P010, ни NV12");

            // Пул создавался в расчёте на десять бит, то есть на кадр вдвое тяжелее.
            // На восьми битах в тот же бюджет видеопамяти помещается вдвое больше
            // кадров, и отдавать этот запас незачем: от числа слотов зависит глубина
            // входной очереди, то есть то, сколько конвейер выдержит без потерь.
            _copyPool?.Dispose();
            _copyPool = new EncoderTexturePool(device, width, height, tenBit: false);
            _maxInputQueue = Math.Max(8, _copyPool.Slots - 8);
        }

        if (TenBit) Log.Info("Encoder", "Глубина цвета: десять бит (P010, профиль Main 4:2:0 10)");

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

    // ---------------- NVENC напрямую ----------------
    //
    // УСТРОЙСТВО. У NVENC своё устройство D3D11 на той же видеокарте, как в OBS
    // (plugins/obs-nvenc/nvenc-d3d11.c). Захват и пейсер копируют кадры в общие
    // текстуры пула на своём устройстве (keyed mutex, см. EncoderTexturePool), а
    // поток NVENC переносит каждый кадр в собственную входную текстуру на своём.
    // Прежде NVENC работал на общем с захватом устройстве, и драйвер внутри вызова
    // NVENC держал замок этого устройства, пока наши потоки захвата, пейсера и
    // копий ждали его же: в играх конвейер вставал намертво.
    //
    // ПОТОК ОДИН. Подача, забор выхода и снятие отображения идут из одного потока,
    // как у OBS. Прежде подача и выдача шли из двух потоков параллельно.

    private NvencSession? _nvenc;
    private readonly Queue<long> _nvencInputPts = new();
    private long _nvencLastDts = long.MinValue;
    private byte[]? _nvencHeader;
    private ID3D11Device? _nvencDevice;
    private ID3D11DeviceContext? _nvencContext;
    private ID3D11Texture2D[] _nvencInputs = [];
    private long _nvencSubmitted;

    /// <summary>
    /// Прямой NVENC выключен до конца работы программы: он уже вставал в этом
    /// процессе, и новая сессия NVENC рядом с зависшей не оживёт. Дальше — MFT.
    /// </summary>
    public static volatile bool DirectNvencDisabled;

    /// <summary>Кодирование идёт напрямую через NVENC, а не через MFT.</summary>
    public bool DirectNvenc => _nvenc is not null;

    /// <summary>Ресурсы кодирования для строки диагностики памяти.</summary>
    public string ResourceSummary() => _nvenc is { } session
        ? $"NVENC напрямую: входных поверхностей {_nvencInputs.Length}, выходных буферов {session.Settings.BufferCount}, " +
          $"общий пул {_copyPool?.Slots ?? 0} слотов"
        : $"MFT: пул копий {_copyPool?.Slots ?? 0} слотов";

    /// <summary>Поток готов к записи в файл: энкодер знает свои заголовки.</summary>
    public bool StreamReady => _nvenc is not null || OutputMediaType is not null;

    /// <summary>Состояние сессии NVENC и её последние вызовы — для лога, когда конвейер встал.</summary>
    public string? NvencTrace() => _nvenc?.Trace();

    private VideoCodec _nvencCodec;

    private bool TryInitializeNvenc(ID3D11Device device, int width, int height, int fps, long bitrateBps, VideoCodec codec)
    {
        _nvencCodec = codec;
        if (DirectNvencDisabled)
        {
            Interlocked.Increment(ref NvencStats.FallbackCount);
            return false;
        }
        string adapter = AdapterDescriptionOf(device);
        if (!NvencSession.Available(adapter)) return false;

        ID3D11Device? encoderDevice = null;
        ID3D11DeviceContext? encoderContext = null;
        NvencSession? session = null;
        try
        {
            using (var dxgi = device.QueryInterface<Vortice.DXGI.IDXGIDevice>())
            using (var gpu = dxgi.GetAdapter())
            {
                D3D11.D3D11CreateDevice(gpu, Vortice.Direct3D.DriverType.Unknown, DeviceCreationFlags.None,
                    [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0],
                    out encoderDevice, out _, out encoderContext).CheckError();
            }
            Aura.Core.Diagnostics.GpuResourceLedger.Track(encoderDevice!, "NVENC");
            using (var multithread = encoderDevice!.QueryInterface<ID3D11Multithread>())
                multithread.SetMultithreadProtected(true);
            Capture.GpuPriority.TryRaise(encoderDevice);

            var config = NvencSession.ConfigFor(codec, width, height, fps, bitrateBps, TenBit);
            session = NvencSession.TryCreate(encoderDevice.NativePointer, config, out string error);
            if (session is null)
            {
                Log.Info("Encoder", $"NVENC напрямую недоступен ({error}) — кодирую через MFT");
                encoderContext?.Dispose();
                encoderDevice.Dispose();
                return false;
            }

            bool tenBit = session.Settings.TenBit == 1;
            int buffers = session.Settings.BufferCount;
            // Входные текстуры энкодера: по одной на выходной буфер. Текстура i
            // снова в деле, только когда выход её номера забран и отображение снято.
            var inputDesc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = tenBit ? Vortice.DXGI.Format.P010 : Vortice.DXGI.Format.NV12,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
            };
            var inputs = new ID3D11Texture2D[buffers];
            for (int i = 0; i < buffers; i++) inputs[i] = Aura.Core.Diagnostics.GpuResourceLedger.Track(encoderDevice.CreateTexture2D(inputDesc), "входы NVENC");
            // Общий пул нужен только на время от копии захвата до переноса на
            // устройство энкодера — очередь плюс запас.
            var pool = new EncoderTexturePool(device, encoderDevice, width, height, tenBit, slots: 24);

            _nvenc = session;
            _nvencDevice = encoderDevice;
            _nvencContext = encoderContext;
            _nvencInputs = inputs;
            _copyPool = pool;
            TenBit = tenBit;
        }
        catch (Exception ex)
        {
            Log.Info("Encoder", $"NVENC напрямую не поднялся ({ex.Message}) — кодирую через MFT");
            session?.Dispose();
            encoderContext?.Dispose();
            encoderDevice?.Dispose();
            return false;
        }

        _maxInputQueue = Math.Max(8, _copyPool.Slots - 4);
        _nvencHeader = session.SequenceHeader();

        EncoderName = "NVIDIA NVENC (напрямую)";
        EncoderVendor = "NVIDIA";
        EncoderLabel = $"{codec.ToString().ToLowerInvariant()}_nvenc";

        _running = true;
        _eventThread = new Thread(NvencLoop) { IsBackground = true, Name = "VideoEncoder.Nvenc", Priority = ThreadPriority.AboveNormal };
        _eventThread.Start();
        _pacerThread = new Thread(PacerLoop) { IsBackground = true, Name = "VideoEncoder.Pacer", Priority = ThreadPriority.AboveNormal };
        _pacerThread.Start();

        long frameBytes = TenBit ? (long)width * height * 3 : (long)width * height * 3 / 2;
        Log.Info("Encoder", $"HW-энкодер: NVENC напрямую (своё устройство D3D11, один поток), {codec}, " +
                            $"{width}x{height}@{fps}, {bitrateBps / 1_000_000} Мбит/с " +
                            $"(VBR, пик {bitrateBps * 2 / 1_000_000}); {session.Describe()}");
        Log.Info("Encoder", $"Очередь кодирования: {_maxInputQueue} кадров, общий пул {_copyPool.Slots} " +
                            $"+ входов энкодера {_nvencInputs.Length} = " +
                            $"{frameBytes * (_copyPool.Slots + _nvencInputs.Length) / (1024 * 1024)} МБ видеопамяти");
        return true;
    }

    private static string AdapterDescriptionOf(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            return adapter.Description.Description;
        }
        catch { return ""; }
    }

    /// <summary>
    /// Единственный поток NVENC: забирает готовые выходы, подаёт новые кадры и ждёт,
    /// когда нечего делать. Ни один вызов NVENC не идёт параллельно другому.
    /// </summary>
    private void NvencLoop()
    {
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.Capture, "VideoEncoder.Nvenc");
        var session = _nvenc!;
        int buffers = session.Settings.BufferCount;
        // Сдвиг времени декодирования: с B-кадрами кадр N по порядку декодирования
        // декодируется на столько кадров раньше N-го входного. Без B-кадров сдвига нет.
        long reorderTicks = session.Settings.BFrames * _frameDurationTicks;
        _nvencReorderTicks = reorderTicks;
        while (_running)
        {
            try
            {
                if (_nvencFailed)
                {
                    // Сессия сломана: кадры очереди только возвращаем в пул, пока
                    // движок пересобирает конвейер на MFT
                    if (TakeInput(20, out var dropped)) _copyPool?.Release(dropped.Texture);
                    continue;
                }
                bool worked = false;
                while (session.PendingCount > 0 && session.TryGet(0) is { } done)
                {
                    DeliverNvenc(done.Data, done.Pts, done.PictureType, reorderTicks);
                    worked = true;
                }
                if (session.PendingCount < buffers && TakeInput(0, out var item))
                {
                    SubmitNvenc(session, item);
                    worked = true;
                }
                if (worked) continue;

                if (session.PendingCount >= buffers)
                {
                    // Все буферы в работе — ждём самый старый выход
                    if (session.TryGet(4) is { } frame) DeliverNvenc(frame.Data, frame.Pts, frame.PictureType, reorderTicks);
                }
                else
                {
                    // Буфер свободен: ждём кадр, но не дольше пары миллисекунд, если
                    // в работе есть выходы, которые пора забирать
                    Volatile.Write(ref _inputRequestWaitingForFrame, 1);
                    bool got;
                    EncoderInputFrame next;
                    try { got = TakeInput(session.PendingCount > 0 ? 2 : 20, out next); }
                    finally { Volatile.Write(ref _inputRequestWaitingForFrame, 0); }
                    if (got) SubmitNvenc(session, next);
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

    /// <summary>Взять кадр из очереди (ждать не дольше <paramref name="timeoutMs"/>).</summary>
    private bool TakeInput(int timeoutMs, out EncoderInputFrame item)
    {
        item = default;
        if (!_inputAvailable.Wait(timeoutMs)) return false;
        lock (_queueLock)
        {
            if (_inputQueue.Count == 0) return false;
            item = _inputQueue.Dequeue();
        }
        Interlocked.Increment(ref InputRequests);
        return true;
    }

    /// <summary>Перенести кадр на устройство энкодера и отправить в NVENC.</summary>
    private void SubmitNvenc(NvencSession session, EncoderInputFrame item)
    {
        var pool = _copyPool!;
        var shared = pool.AcquireForEncoder(item.Texture, 200);
        if (shared is null)
        {
            // Захват так и не отдал слот — кадр теряем, слот остаётся занятым
            Interlocked.Increment(ref FramesDroppedRealQueue);
            Interlocked.Increment(ref NvencStats.DroppedInputFrames);
            if (Interlocked.Increment(ref _nvencAcquireFailures) is 1 or 100)
                Log.Warn("Encoder", $"NVENC: слот общего пула не получен за 200 мс ({_nvencAcquireFailures} раз)");
            return;
        }
        var input = _nvencInputs[(int)(_nvencSubmitted % _nvencInputs.Length)];
        long start = Diagnostics.PipelineProbe.Now();
        _nvencContext!.CopyResource(input, shared);
        pool.ReleaseFromEncoder(item.Texture);        // слот свободен: копия уже в очереди видеокарты

        // Просьба ключевого кадра (буфер потерял кадр или начал копить заново)
        bool forceIdr = _keyframeRequested;
        if (forceIdr) _keyframeRequested = false;

        int result;
        for (int busyRetries = 0; ; busyRetries++)
        {
            lock (_nvencInputPts) _nvencInputPts.Enqueue(item.Ticks);
            int inFlight = Interlocked.Increment(ref _inFlight);
            long peak;
            while (inFlight > (peak = Interlocked.Read(ref MaxInFlight)))
                if (Interlocked.CompareExchange(ref MaxInFlight, inFlight, peak) == peak) break;

            result = session.Encode(input.NativePointer, item.Ticks, forceIdr);
            if (result >= 0) break;

            // Кадр не ушёл: прослойка уже сняла отображение входа, выходной буфер не
            // занят. Снимаем его метку, чтобы очередь меток совпадала с выходами.
            lock (_nvencInputPts) RemoveLast(_nvencInputPts);
            Interlocked.Decrement(ref _inFlight);
            if (-result != NvencSession.StatusEncoderBusy) break;

            // ENCODER_BUSY по nvEncodeAPI.h — «повторите через несколько миллисекунд»,
            // а не повод терять кадр. Пока ждём, забираем готовый выход, если он есть:
            // это и освобождает энкодер. Кадр теряем, только если NVENC занят дольше
            // нескольких попыток — дальше ждать значит задерживать весь поток.
            Interlocked.Increment(ref NvencStats.BusyCount);
            if (busyRetries >= NvencBusyRetries || !_running) break;
            if (session.PendingCount > 0 && session.TryGet(1) is { } done)
                DeliverNvenc(done.Data, done.Pts, done.PictureType, _nvencReorderTicks);
            else
                Thread.Sleep(1);
        }
        if (result < 0)
        {
            Interlocked.Increment(ref FramesDroppedRealQueue);
            Interlocked.Increment(ref NvencStats.DroppedInputFrames);
            if (forceIdr) _keyframeRequested = true;         // просьба ключевого кадра не теряется
            int status = -result;
            if (NvencSession.IsTransient(status))
            {
                if (Interlocked.Increment(ref _nvencTransientDrops) is 1 or 100 or 1000)
                    Log.Warn("Encoder", $"NVENC: {NvencSession.StatusName(status)} — кадр пропущен ({_nvencTransientDrops} раз)");
            }
            else FailNvenc($"отправка кадра → {NvencSession.StatusName(status)}");
            return;
        }
        if (result == 0)
        {
            // Буфер занят — так быть не должно (поток сам считает свободные). Кадр теряем честно.
            lock (_nvencInputPts) RemoveLast(_nvencInputPts);
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref FramesDroppedRealQueue);
            Interlocked.Increment(ref NvencStats.DroppedInputFrames);
            if (forceIdr) _keyframeRequested = true;
            return;
        }
        if (result == NvencSession.AcceptedNeedMoreInput) Interlocked.Increment(ref NvencStats.NeedMoreInputCount);
        if (forceIdr) Interlocked.Increment(ref NvencStats.ForcedIdrCount);
        _nvencSubmitted++;
        Diagnostics.PipelineProbe.ProcessInput.Add(start, Diagnostics.PipelineProbe.Now());
    }

    private long _nvencAcquireFailures;

    /// <summary>Сколько раз повторить кадр после ENCODER_BUSY, прежде чем его потерять.</summary>
    private const int NvencBusyRetries = 5;

    /// <summary>Сдвиг декодирования для выходов, забранных внутри SubmitNvenc.</summary>
    private long _nvencReorderTicks;
    private long _nvencTransientDrops;

    /// <summary>Сессия NVENC сломана: дальше в неё ничего не отправляем.</summary>
    private volatile bool _nvencFailed;

    /// <summary>
    /// Выход был пропущен: следующие кадры могут ссылаться на пропавший опорный,
    /// поэтому до ближайшего ключевого кадра ничего не отдаём (и просим его).
    /// </summary>
    private bool _nvencResync;

    private void FailNvenc(string reason)
    {
        if (_nvencFailed) return;
        _nvencFailed = true;
        DirectNvencDisabled = true;
        Interlocked.Increment(ref NvencStats.FatalErrors);
        Log.Error("Encoder", $"NVENC: {reason} — сессия непригодна, дальше кодирую через MFT. " +
                             $"Состояние NVENC:\n{_nvenc?.Trace()}");
        var handler = Failed;
        if (handler is not null) _ = Task.Run(() => handler(reason));
    }

    private static void RemoveLast<T>(Queue<T> queue)
    {
        int count = queue.Count;
        for (int i = 0; i < count; i++)
        {
            T v = queue.Dequeue();
            if (i < count - 1) queue.Enqueue(v);
        }
    }

    private void DeliverNvenc(ArraySegment<byte> data, long pts, int pictureType, long reorderTicks)
    {
        long inputPts;
        lock (_nvencInputPts) inputPts = _nvencInputPts.Count > 0 ? _nvencInputPts.Dequeue() : pts;
        Interlocked.Decrement(ref _inFlight);
        if (pictureType == NvencSession.SkippedPicture)
        {
            // Выход пропущен: метку сняли, чтобы следующие кадры не съехали на один
            Interlocked.Increment(ref FramesDroppedRealQueue);
            Interlocked.Increment(ref NvencStats.DroppedOutputFrames);
            var session = _nvenc;
            if (session is { LastSkipFatal: true }) FailNvenc(session.LastSkipReason);
            else
            {
                Log.Warn("Encoder", $"NVENC: {session?.LastSkipReason} — кадр пропущен, жду ключевой");
                _nvencResync = true;
                _keyframeRequested = true;
            }
            return;
        }

        long dts = Math.Min(inputPts - reorderTicks, pts);
        if (_nvencLastDts != long.MinValue && dts <= _nvencLastDts) dts = _nvencLastDts + 1;
        _nvencLastDts = dts;

        // Точка входа в поток — только IDR. Обычный I-кадр с B-кадрами может быть
        // «открытой» группой: B-кадры после него ссылаются на кадры ДО него, и клип,
        // начатый с такого кадра, рассыпается в первые мгновения. У AV1 ключевой
        // кадр драйвер может помечать как I — там это и есть точка входа.
        bool keyframe = pictureType == NvencSession.PictureIdr ||
                        (pictureType == NvencSession.PictureI && _nvencCodec == VideoCodec.AV1);
        if (_nvencResync)
        {
            if (!keyframe)
            {
                Interlocked.Increment(ref FramesDroppedRealQueue);
                Interlocked.Increment(ref NvencStats.DroppedOutputFrames);
                return;
            }
            _nvencResync = false;
        }
        if (keyframe)
        {
            _lastKeyframeTicks = pts;
            _nvencHeader ??= _nvenc?.SequenceHeader();
        }

        Interlocked.Increment(ref FramesEncoded);
        FrameEncoded?.Invoke(new EncodedFrame(data.Array!, data.Offset, data.Count,
                                              pts, _frameDurationTicks, keyframe, dts));
    }

    /// <summary>Брошенные сессии NVENC: их поток завис в драйвере, освобождать под ним нельзя.</summary>
    private static readonly List<object> s_abandonedNvenc = [];

    private void DisposeNvenc()
    {
        _running = false;
        bool loopExited = _eventThread?.Join(3000) ?? true;
        bool pacerExited = _pacerThread?.Join(5000) ?? true;

        var session = Interlocked.Exchange(ref _nvenc, null);
        if (!loopExited)
        {
            // Поток всё ещё внутри вызова NVENC. Уничтожить сессию, устройство или
            // текстуры под ним — обращение к освобождённой памяти. Бросаем всё разом
            // и держим ссылки, чтобы сборщик мусора не освободил их позже.
            Log.Warn("Encoder", "Поток NVENC не завершился — сессия и её устройство оставлены. " +
                                $"Состояние NVENC:\n{session?.Trace()}");
            lock (s_abandonedNvenc)
                s_abandonedNvenc.AddRange(new object?[] { session, _nvencDevice, _nvencContext, _nvencInputs, _copyPool }
                                              .Where(o => o is not null)!);
            _copyPool = null;
            _nvencDevice = null;
            _nvencContext = null;
            _nvencInputs = [];
            return;
        }
        if (session is not null)
        {
            // Конец потока: NVENC отдаёт то, что держит у себя, и снимает отображение
            // входов. Кадры уже никому не нужны — просто забираем их.
            try
            {
                session.EndOfStream();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 1000 && session.PendingCount > 0 && session.TryGet(50) is not null) { }
            }
            catch (Exception ex) { Log.Info("Encoder", $"NVENC: хвост не выдан при закрытии ({ex.Message})"); }
            session.Dispose();
        }
        lock (_queueLock) _inputQueue.Clear();
        foreach (var input in _nvencInputs) input.Dispose();
        _nvencInputs = [];
        if (pacerExited) { _copyPool?.Dispose(); _copyPool = null; }
        else
        {
            Log.Warn("Encoder", "Пейсер не завершился за 5 секунд — пул текстур оставлен сборщику");
            _copyPool = null;
        }
        _nvencContext?.Dispose();
        _nvencContext = null;
        _nvencDevice?.Dispose();
        _nvencDevice = null;
    }

    /// <summary>Есть ли у кодека десятибитный профиль, который мы готовы просить.</summary>
    public static bool SupportsTenBit(VideoCodec codec) => codec is VideoCodec.HEVC or VideoCodec.AV1;

    /// <summary>LUID видеокарты, на которой создано устройство; null — узнать не удалось.</summary>
    internal static long? AdapterLuidOf(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var luid = adapter.Description.Luid;
            return ((long)luid.HighPart << 32) | luid.LowPart;
        }
        catch (Exception ex)
        {
            Log.Info("Encoder", $"Видеокарта устройства не определилась: {ex.Message}");
            return null;
        }
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
    public uint QualityPreset => _quality?.Preset ?? 0;

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

        // Режим управления битрейтом — из той же семьи структурных параметров.
        // Поставленный ПОСЛЕ медиатипов, VBR с потолком у NVENC не удерживался:
        // в логе стояло «VBR с потолком не принят энкодером», и чтение обратно
        // возвращало CBR. Пробуем до типов; удержалось или нет, проверит
        // ConfigureRateControl, который зовут уже после.
        _codecApi.Set(CodecApiGuids.AVEncCommonRateControlMode, PeakConstrainedVbr, optional: true);
        _codecApi.Set(CodecApiGuids.AVEncCommonMeanBitRate, (uint)bitrateBps, optional: true);
        _codecApi.Set(
            CodecApiGuids.AVEncCommonMaxBitRate,
            PeakBitrate(bitrateBps),
            optional: true);

        // Размер группы кадров — тоже структурный параметр, и ровно на нём это
        // подтвердилось замером. Выставленный ПОСЛЕ медиатипов, он у NVIDIA HEVC
        // принимается без ошибки и даже читается обратно, но энкодер продолжает жить
        // со своим значением по умолчанию: ключевой кадр каждые 60 кадров, то есть
        // каждую секунду, а не каждые две. В сохранённых клипах так и было — 181
        // ключевой кадр на три минуты, и каждый весил в 12 раз больше обычного:
        // 17.6% всего битрейта уходило на ключевые кадры, а кадры сразу после них
        // сжимались сильнее, чтобы отдать долг буфера. Выставленный здесь, до типов,
        // он соблюдается: на RTX 3070 группа стала 120 кадров. Позднее присваивание
        // в ConfigureCodecApi оставлено для энкодеров, которые понимают только его.
        _codecApi.Set(CodecApiGuids.AVEncMPVGOPSize, (uint)(Fps * 2), optional: true);

        ConfigureReferenceFrames();
    }

    /// <summary>
    /// Потолок битрейта для VBR — вдвое выше среднего.
    ///
    /// ЗАЧЕМ ВДВОЕ, А НЕ В ПОЛТОРА РАЗА. Средний битрейт от потолка не меняется, а
    /// сложным сценам (взрыв, резкий разворот камеры) разрешено занять больше. Замер
    /// на реальной записи 1440p60 при 15 Мбит/с, VMAF по модели для близкого
    /// просмотра: потолок ×1.5 — 96.48 (5% худших кадров 92.35), ×2 — 96.50 (92.63).
    /// Выигрыш небольшой, но он весь приходится на худшие кадры — те, что и видно.
    /// Длину буфера повтора это не меняет: арена считается по среднему битрейту.
    /// </summary>
    private static uint PeakBitrate(long bitrateBps) =>
        (uint)Math.Min(bitrateBps * 2, uint.MaxValue);

    /// <summary>Сколько опорных кадров просим у энкодера.</summary>
    private const uint WantedReferenceFrames = 3;

    /// <summary>
    /// Больше опорных кадров — меньше битрейта на неподвижный фон.
    ///
    /// ЗАЧЕМ. В режиме низкой задержки энкодер держит минимум опорных кадров, обычно
    /// один: каждый кадр предсказывается только от предыдущего. В геймплее фон и
    /// интерфейс почти не меняются, но их всё равно приходится описывать заново после
    /// каждого резкого движения камеры. С тремя опорными кадрами энкодер находит
    /// неизменившийся кусок на кадр-два назад и тратит на него биты один раз.
    /// Задержки это не добавляет: её дают B-кадры, а они у нас выключены.
    ///
    /// Ключ СТАТИЧЕСКИЙ: Microsoft прямо пишет, что он задаётся только до начала
    /// сеанса кодирования. Поэтому он здесь, вместе с остальными структурными
    /// параметрами, а не в ConfigureCodecApi — там энкодер уже отвечал бы отказом.
    ///
    /// Диапазон спрашиваем у драйвера: рекомендованное значение по умолчанию — 2,
    /// но верхняя граница у разных MFT разная, и запрос выше неё драйвер либо молча
    /// зажимает, либо отвергает целиком.
    /// </summary>
    private void ConfigureReferenceFrames()
    {
        if (_codecApi is null || !_codecApi.IsSupported(CodecApiGuids.AVEncVideoMaxNumRefFrame)) return;

        uint wanted = WantedReferenceFrames;
        if (_codecApi.TryGetUIntRange(CodecApiGuids.AVEncVideoMaxNumRefFrame, out uint min, out uint max))
            wanted = Math.Clamp(wanted, min, Math.Max(min, max));

        _codecApi.Set(CodecApiGuids.AVEncVideoMaxNumRefFrame, wanted, optional: true);
        if (_codecApi.TryReadUInt(CodecApiGuids.AVEncVideoMaxNumRefFrame, out uint accepted))
            Log.Info("Encoder", $"Опорных кадров: {accepted}");
    }

    /// <summary>eAVEncCommonRateControlMode: VBR со средним битрейтом и потолком.</summary>
    private const uint PeakConstrainedVbr = 1;

    /// <summary>eAVEncCommonRateControlMode: постоянный битрейт.</summary>
    private const uint ConstantBitRate = 0;

    /// <summary>eAVEncH265VProfile_Main_420_8.</summary>
    private const uint Main8Profile = 1;

    /// <summary>eAVEncH265VProfile_Main_420_10.</summary>
    private const uint Main10Profile = 2;

    /// <summary>Объявить входной тип кадра. false — энкодер его не принял.</summary>
    private bool TrySetInputFormat(int width, int height, int fps, bool tenBit)
    {
        try
        {
            using var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype,
                       tenBit ? VideoFormatGuids.P010 : VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, PackLong(width, height));
            inType.Set(MediaTypeAttributeKeys.FrameRate, PackLong(fps, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            SetColorInfo(inType);
            _transform!.SetInputType(0, inType, 0);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("Encoder", $"Входной тип {(tenBit ? "P010" : "NV12")} не принят: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Какие форматы кадра энкодер согласен принимать. Только запись в лог.
    ///
    /// ЗАЧЕМ. Сейчас мы подаём NV12, то есть восемь бит на канал. Десятибитный P010
    /// заметно лучше держит плавные переходы: небо, дым, тёмные сцены перестают
    /// расслаиваться на полосы. Но поддержка зависит от конкретного MFT, а не от
    /// кодека: программный энкодер HEVC от Microsoft умеет только восемь бит, тогда
    /// как энкодеры NVIDIA и Intel десять бит обычно умеют.
    ///
    /// Гадать по названию драйвера бессмысленно, поэтому спрашиваем сам энкодер.
    /// Список типов он отдаёт после того, как задан выходной тип, и чтение его
    /// ничего не меняет. По этой строке в логе и станет ясно, стоит ли переводить
    /// конвейер на десять бит.
    /// </summary>
    private void LogInputFormats()
    {
        if (_transform is null) return;

        var names = new List<string>();
        bool tenBit = false;
        try
        {
            for (int index = 0; index < 32; index++)
            {
                IMFMediaType? available = null;
                try { available = _transform.GetInputAvailableType(0, index); }
                catch { break; }   // MF_E_NO_MORE_TYPES — список кончился
                if (available is null) break;

                using (available)
                {
                    Guid sub = available.GetGUID(MediaTypeAttributeKeys.Subtype);
                    if (sub == VideoFormatGuids.NV12) names.Add("NV12");
                    else if (sub == VideoFormatGuids.P010) { names.Add("P010 (10 бит)"); tenBit = true; }
                    else if (sub == VideoFormatGuids.Argb32) names.Add("ARGB32");
                    else names.Add(sub.ToString());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("Encoder", $"Список входных форматов не читается: {ex.Message}");
            return;
        }

        if (names.Count == 0) return;
        Log.Info("Encoder", $"Энкодер принимает: {string.Join(", ", names)}" +
                            (tenBit ? " — десять бит доступны" : " — только восемь бит"));
    }

    /// <summary>
    /// Битрейт: VBR с потолком, с откатом на CBR там, где энкодер его не принял.
    ///
    /// eAVEncCommonRateControlMode: 0 = CBR, 1 = PeakConstrainedVBR,
    /// 2 = UnconstrainedVBR, 3 = Quality.
    ///
    /// Здесь по очереди стояли два неверных значения. Сначала 3 под комментарием
    /// «CBR»: это режим ПО КАЧЕСТВУ, заданный битрейт он игнорирует, и в замерах
    /// по сохранённым клипам фактический битрейт гулял от 15.0 до 58.1 Мбит/с при
    /// настройке 50. Потом 0, настоящий CBR, «как у NVIDIA App». Но NVIDIA App
    /// пишет как раз переменным битрейтом, и это видно на глаз: при CBR энкодер
    /// обязан выдать одни и те же 30 Мбит/с и на статичном меню, и на взрыве в
    /// пол-экрана. На простой сцене биты уходят впустую, на сложной их не хватает,
    /// и картинка разваливается на блоки ровно там, где это заметно.
    ///
    /// Режим 1 берёт лучшее от обоих: СРЕДНИЙ битрейт равен заданному, поэтому
    /// расчёт длины буфера повтора остаётся верным, а на сложных сценах энкодер
    /// может занять до потолка. Потолок — вдвое выше среднего (см. PeakBitrate).
    /// </summary>
    private void ConfigureRateControl(long bitrateBps)
    {
        if (_codecApi is null) return;

        uint mean = (uint)bitrateBps;
        uint peak = PeakBitrate(bitrateBps);

        // Режим уже пробовали поставить до медиатипов (ConfigureCodecApiEarly).
        // Повторяем на случай энкодеров, которые принимают его только здесь, и
        // проверяем чтением обратно: SetValue у NVIDIA возвращает успех и на
        // ключах, которые ничего не меняют.
        _codecApi.Set(CodecApiGuids.AVEncCommonRateControlMode, PeakConstrainedVbr, optional: true);
        bool vbrAccepted =
            _codecApi.TryReadUInt(CodecApiGuids.AVEncCommonRateControlMode, out uint actual) &&
            actual == PeakConstrainedVbr;

        if (vbrAccepted)
        {
            _codecApi.Set(CodecApiGuids.AVEncCommonMeanBitRate, mean);
            _codecApi.Set(CodecApiGuids.AVEncCommonMaxBitRate, peak, optional: true);
            Log.Info("Encoder", $"Битрейт: VBR с потолком, средний {mean / 1_000_000} Мбит/с, " +
                                $"пик {peak / 1_000_000} Мбит/с");
            return;
        }

        // Энкодер не принял режим с потолком. CBR хуже по качеству, но предсказуем,
        // и заданный битрейт он соблюдает — в отличие от режима по качеству.
        _codecApi.Set(CodecApiGuids.AVEncCommonRateControlMode, ConstantBitRate);
        _codecApi.Set(CodecApiGuids.AVEncCommonMeanBitRate, mean);
        Log.Info("Encoder", $"Битрейт: VBR с потолком не принят энкодером, " +
                            $"остаётся CBR {mean / 1_000_000} Мбит/с");
    }

    /// <summary>Тюнинг через ICodecAPI: битрейт, GOP = 2 сек, качество записи. Ошибки не фатальны.</summary>
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
        ConfigureRateControl(bitrateBps);
        _codecApi.Set(CodecApiGuids.AVEncMPVGOPSize, (uint)(fps * 2));
        // AVEncCommonLowLatency энкодер NVIDIA отвергает с E_INVALIDARG в ЛЮБОМ
        // виде (пробовали и VT_BOOL, и VT_UI4) — в логе это годами висело как
        // «ICodecAPI 9d3ecd55…: Value does not fall within the expected range».
        // Оставляем документированный VT_BOOL для тех MFT, где ключ работает,
        // а до NVENC добираемся двумя другими ключами ниже.
        _codecApi.Set(CodecApiGuids.AVEncCommonLowLatency, true, optional: true);

        // На NVIDIA этот режим нужен не только для задержки: под игровой нагрузкой
        // без него MFT в реальном логе запрашивал лишь 39–44 кадра/с при полной
        // очереди. С ним тот же NVENC держал целевые 60 кадров/с.
        _codecApi.Set(CodecApiGuids.AVLowLatencyMode, true);

        // Буфер VBV — запас, из которого энкодер берёт биты на резкое усложнение
        // картинки, не разваливая её в блоки. Задаётся СТРОГО ПОСЛЕ режима низкой
        // задержки: тот выставляет свой крошечный буфер (в замере — 3 Мбит при
        // 40 Мбит/с, меньше пяти кадров), и наше значение, выставленное раньше,
        // затиралось. Секунда — как у ShadowPlay.
        _codecApi.Set(CodecApiGuids.AVEncCommonBufferSize, (uint)bitrateBps, optional: true);

        // Стартовый пресет ставит сам адаптер — он же решает, поддерживает ли его
        // этот энкодер, и дальше подстраивает под нагрузку.
        // Низкая задержка включена выше; адаптер знает об этом и может попробовать
        // отдать её обратно ради качества, когда энкодер уверенно справляется.
        _quality = new QualityAdapter(_codecApi, fps, lowLatencyOn: true);

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
    /// <summary>
    /// Попросить ключевой кадр на ближайшем входе, не дожидаясь штатной группы.
    ///
    /// Зовёт буфер повтора: он потерял кадр посреди группы или начал копить заново
    /// после сохранения и до ключевого кадра ничего не принимает. Без просьбы это
    /// до двух секунд выброшенного видео.
    /// </summary>
    public void RequestKeyframe() => _keyframeRequested = true;

    private volatile bool _keyframeRequested;

    private void MaybeForceKeyframe(long sampleTicks)
    {
        if (_forceKeyframeUnavailable || _codecApi is null) return;
        bool requested = _keyframeRequested;
        if (!requested && _lastKeyframeTicks != long.MinValue &&
            sampleTicks - _lastKeyframeTicks < KeyframeIntervalTicks) return;
        // Просьба, пришедшая слишком скоро после прошлой, не теряется: флаг остаётся
        // и сработает на одном из следующих кадров.
        if (_lastKeyframeRequestTicks != long.MinValue &&
            sampleTicks - _lastKeyframeRequestTicks < KeyframeRequestGapTicks) return;
        _keyframeRequested = false;

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

    /// <summary>
    /// GPU-копия источника в свободный слот пула; null — свободных нет (энкодер
    /// держит всё, что есть). Текстура занята ссылкой вызывающего, см.
    /// <see cref="EncoderTexturePool"/>.
    /// </summary>
    private ID3D11Texture2D? CopyIntoPool(ID3D11Texture2D src) => _copyPool!.TryCopy(src, _context!);

    /// <summary>Кадр вышел из энкодера: слот его входа свободен.</summary>
    private void ReleaseSubmitted()
    {
        ID3D11Texture2D? texture = null;
        lock (_submittedTextures) if (_submittedTextures.Count > 0) texture = _submittedTextures.Dequeue();
        _copyPool?.Release(texture);
    }

    /// <summary>Текстуры, поданные в энкодер, в порядке подачи: слот отпускается по выходу кадра.</summary>
    private readonly Queue<ID3D11Texture2D> _submittedTextures = new();

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
            // Квантование PTS к сетке CFR. Слот считается ДО копии в пул: кадр,
            // опоздавший за уже закрытый повторами слот, в энкодер не идёт вовсе.
            long pts;
            long natural;
            bool first = _cfrBase < 0;
            if (first)
            {
                natural = pts = ticks;
            }
            else
            {
                // Счёт слотов и число дубликатов задним числом — в EncoderCfrPolicy:
                // это единственная часть конвейера, которую можно проверить тестом.
                natural = EncoderCfrPolicy.NaturalSlot(ticks, _cfrBase, _frameDurationTicks);
                if (EncoderCfrPolicy.QuantizePts(ticks, _cfrBase, _lastCfrPts, _frameDurationTicks) is not long placed)
                {
                    // Слот давно закрыт повторами. Картинку сохраняем как источник
                    // следующих повторов, а время для пейсера — настоящее.
                    _copyPool!.KeepLatest(nv12PoolTexture, _context!);
                    _lastRealPts = natural;
                    _lastRealArrivalWall = NowQpcTicks();
                    Interlocked.Increment(ref FramesDroppedLate);
                    return;
                }
                pts = placed;
            }

            var copy = CopyIntoPool(nv12PoolTexture);
            if (copy is null)
            {
                // Все слоты держит энкодер: кадр теряем, конвейер не ждёт
                Interlocked.Increment(ref FramesDroppedRealQueue);
                Interlocked.Increment(ref FramesDroppedNoSlot);
                return;
            }

            if (first)
            {
                _cfrBase = ticks;
                _lastCfrPts = ticks;
            }
            else
            {
                // Пропущенные слоты между прошлым кадром и этим — дубликаты задним числом
                if (_copyPool!.Latest is not null &&
                    EncoderCfrPolicy.BackfillSlots(_lastCfrPts, pts, _frameDurationTicks, MaxBackfillSlots) > 0)
                {
                    while (_lastCfrPts + _frameDurationTicks < pts)
                    {
                        long duplicatePts = _lastCfrPts + _frameDurationTicks;
                        bool encoderBehind = EncoderIsBehind();
                        if (!CanEnqueueDuplicate(encoderBehind))
                        {
                            Interlocked.Increment(ref FramesSuppressedDuplicates);
                            Interlocked.Increment(ref PacerBlocked);
                            break;
                        }
                        var dup = CopyIntoPool(_copyPool.Latest);
                        if (dup is null)
                        {
                            Interlocked.Increment(ref PacerBlocked);
                            break;
                        }
                        if (!Enqueue(dup, duplicatePts, isDuplicate: true, encoderBehind))
                        {
                            _copyPool!.Release(dup);
                            Interlocked.Increment(ref PacerBlocked);
                            break;
                        }
                        _lastCfrPts = duplicatePts;
                        Interlocked.Increment(ref FramesDuplicated);
                    }
                }
                _lastCfrPts = pts;
            }
            // Источник дубликатов — отдельная копия, НЕ текстура, ушедшая в энкодер
            // (см. EncoderTexturePool.Latest). После досыпки: та брала прошлый кадр.
            _copyPool!.KeepLatest(nv12PoolTexture, _context!);
            long lead = pts - ticks;
            if (lead > Interlocked.Read(ref MaxPtsLeadTicks)) Interlocked.Exchange(ref MaxPtsLeadTicks, lead);
            if (lead < Interlocked.Read(ref MinPtsLeadTicks)) Interlocked.Exchange(ref MinPtsLeadTicks, lead);
            // Пейсер отсчитывает паузу от НАСТОЯЩЕГО времени кадра. Раньше сюда
            // шёл сдвинутый вперёд слот, повторы строились от него, и сдвиг видео
            // относительно звука не рассасывался никогда.
            _lastRealPts = natural;
            _lastRealArrivalWall = NowQpcTicks();
            Interlocked.Increment(ref FramesSubmitted);
            if (!Enqueue(copy, pts, isDuplicate: false, encoderBehind: false)) _copyPool!.Release(copy);
        }
    }

    private bool Enqueue(
        ID3D11Texture2D tex,
        long pts,
        bool isDuplicate,
        bool encoderBehind)
    {
        lock (_queueLock)
        {
            int firstDuplicate = -1;
            if (!isDuplicate && _inputQueue.Count >= _maxInputQueue)
            {
                int index = 0;
                foreach (EncoderInputFrame frame in _inputQueue)
                {
                    if (frame.IsDuplicate)
                    {
                        firstDuplicate = index;
                        break;
                    }
                    index++;
                }
            }

            EncoderQueueAdmission admission = EncoderQueueAdmissionPolicy.Decide(
                isDuplicate,
                _inputQueue.Count,
                _maxInputQueue,
                encoderBehind,
                firstDuplicate);
            if (admission == EncoderQueueAdmission.RejectDuplicate)
            {
                Interlocked.Increment(ref FramesSuppressedDuplicates);
                return false;
            }

            if (admission == EncoderQueueAdmission.EvictDuplicate)
            {
                RemoveQueuedFrameAt(firstDuplicate);
                Interlocked.Increment(ref FramesDiscardedDuplicates);
                Interlocked.Increment(ref FramesDroppedQueue);
            }
            else if (admission == EncoderQueueAdmission.EvictOldestReal)
            {
                _copyPool?.Release(_inputQueue.Dequeue().Texture);
                RemoveOneAvailableSignal();
                Interlocked.Increment(ref FramesDroppedRealQueue);
                Interlocked.Increment(ref FramesDroppedQueue);
            }

            _inputQueue.Enqueue(new EncoderInputFrame(tex, pts, isDuplicate));
            Diagnostics.PipelineProbe.ReportQueueDepth(_inputQueue.Count);
        }
        _inputAvailable.Release();
        return true;
    }

    private bool CanEnqueueDuplicate(bool encoderBehind)
    {
        lock (_queueLock)
        {
            return EncoderQueueAdmissionPolicy.Decide(
                incomingDuplicate: true,
                queueDepth: _inputQueue.Count,
                maximumDepth: _maxInputQueue,
                encoderBehind: encoderBehind,
                firstDuplicateIndex: -1) == EncoderQueueAdmission.Append;
        }
    }

    private void RemoveQueuedFrameAt(int removalIndex)
    {
        int count = _inputQueue.Count;
        for (int index = 0; index < count; ++index)
        {
            EncoderInputFrame frame = _inputQueue.Dequeue();
            if (index != removalIndex) _inputQueue.Enqueue(frame);
            else _copyPool?.Release(frame.Texture);
        }
        RemoveOneAvailableSignal();
    }

    private void RemoveOneAvailableSignal()
    {
        if (_inputAvailable.CurrentCount > 0) _inputAvailable.Wait(0);
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

    /// <summary>
    /// Успевает ли энкодер за настроенной частотой.
    ///
    /// Смотрим на темп запросов MFT: подать больше, чем у нас попросили, всё равно
    /// нельзя, и именно этот темп показывает реальный потолок кодирования. Меньше
    /// трёх четвертей от нужного — значит очередь и так не разгребается, и добавлять
    /// в неё дубликаты вредно.
    /// </summary>
    private bool EncoderIsBehind()
    {
        bool requestWaitingForFrame = Volatile.Read(ref _inputRequestWaitingForFrame) != 0;
        long now = NowQpcTicks();
        long elapsed = now - _rateWindowStart;
        if (elapsed < 10_000_000)
            return requestWaitingForFrame ? false : _encoderBehind;   // окно — секунда

        long requests = Interlocked.Read(ref InputRequests);
        double perSecond = (requests - _rateWindowRequests) * 10_000_000.0 / elapsed;

        _rateWindowStart = now;
        _rateWindowRequests = requests;
        _encoderBehind = EncoderPacingPolicy.IsBehind(perSecond, Fps, requestWaitingForFrame);
        return _encoderBehind;
    }

    private long _rateWindowStart;
    private long _rateWindowRequests;
    private volatile bool _encoderBehind;
    private int _inputRequestWaitingForFrame;

    private void PacerLoopCore()
    {
        // Высокоточный таймер вместо Thread.Sleep: без глобального timeBeginPeriod(1)
        // сон квантуется по 15.6 мс, и пейсер ставил бы дубликаты пачками.
        using var timer = new Interop.PreciseTimer();
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.Capture, "VideoEncoder.Pacer");
        while (_running)
        {
            timer.Wait(40_000); // 4 мс
            // Вне _cfrLock: смена пресета не должна держать подачу кадров
            _quality?.Tick(Interlocked.Read(ref FramesEncoded),
                           Interlocked.Read(ref FramesSubmitted),
                           Interlocked.Read(ref PacerBlocked),
                           Interlocked.Read(ref FramesDroppedRealQueue));
            lock (_cfrLock)
            {
                if (_copyPool?.Latest is null || _cfrBase < 0 || _context is null) continue;
                // Пауза = сколько НАШЕГО времени прошло без реальных кадров; цель
                // заполнения отсчитывается от pts последнего кадра (часы WGC).
                // Отступ 5 кадров от «сейчас»: реальный кадр, идущий с задержкой
                // доставки 20-30 мс, всегда успевает занять свой слот первым.
                long silence = NowQpcTicks() - _lastRealArrivalWall;
                long fillTarget = _lastRealPts + silence - _frameDurationTicks * 5;
                int catchUp = 0;
                while (_lastCfrPts + _frameDurationTicks <= fillTarget && catchUp++ < 4)
                {
                    // Дубликаты держат CFR. Настоящий кадр всё равно имеет приоритет:
                    // при полной очереди он вытеснит первый накопленный дубликат.
                    bool encoderBehind = EncoderIsBehind();
                    if (!CanEnqueueDuplicate(encoderBehind))
                    {
                        Interlocked.Increment(ref FramesSuppressedDuplicates);
                        Interlocked.Increment(ref PacerBlocked);
                        break;
                    }

                    long pts = _lastCfrPts + _frameDurationTicks;
                    var dup = CopyIntoPool(_copyPool.Latest);
                    if (dup is null)
                    {
                        Interlocked.Increment(ref PacerBlocked);
                        break;
                    }
                    if (!Enqueue(dup, pts, isDuplicate: true, encoderBehind))
                    {
                        _copyPool!.Release(dup);
                        Interlocked.Increment(ref PacerBlocked);
                        break;
                    }
                    _lastCfrPts = pts;
                    Interlocked.Increment(ref FramesDuplicated);
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
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.Capture, "VideoEncoder.Events");
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
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.Capture, "VideoEncoder.Feed");
        while (_running)
        {
            try
            {
                if (!_needInput.Wait(200)) continue;      // ждём запрос NeedInput
                bool gotFrame = false;
                Volatile.Write(ref _inputRequestWaitingForFrame, 1);
                try
                {
                    while (_running && !(gotFrame = _inputAvailable.Wait(200))) { } // ждём кадр, запрос держим
                }
                finally
                {
                    Volatile.Write(ref _inputRequestWaitingForFrame, 0);
                }
                if (!gotFrame) break;

                EncoderInputFrame item;
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
                MaybeForceKeyframe(item.Ticks);

                try
                {
                    long piStart = Diagnostics.PipelineProbe.Now();
                    using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                        typeof(ID3D11Texture2D).GUID, item.Texture, 0, false);
                    using var sample = MediaFactory.MFCreateSample();
                    sample.AddBuffer(buffer);
                    sample.SampleTime = item.Ticks;
                    sample.SampleDuration = _frameDurationTicks;
                    lock (_submittedTextures) _submittedTextures.Enqueue(item.Texture);
                    try { _transform!.ProcessInput(0, sample, 0); }
                    catch
                    {
                        lock (_submittedTextures) RemoveLast(_submittedTextures);
                        _copyPool?.Release(item.Texture);
                        throw;
                    }
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
    private bool _loggedBFrames;

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
            // Буфер отпускаем сразу после AddBuffer: сэмпл держит свою ссылку. Иначе
            // наша висела бы до финализатора, а их при SustainedLowLatency почти нет —
            // та же утечка, что уже чинили в MfMp4Writer.CreateSample.
            using var outputMemory = MediaFactory.MFCreateMemoryBuffer(streamInfo.Size);
            ourSample.AddBuffer(outputMemory);
            outBuffer.Sample = ourSample;
        }

        var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);
        // Коллекция событий, если MFT её вернул, принадлежит вызывающему.
        outBuffer.Events?.Dispose();
        // Неудача — это чаще всего MF_E_TRANSFORM_NEED_MORE_INPUT: отдавать нечего,
        // и кадры законно остаются ВНУТРИ энкодера. Счётчик здесь трогать нельзя,
        // он опускается ровно тогда, когда кадр действительно вышел наружу.
        if (hr.Failure) { ourSample?.Dispose(); return; }
        Interlocked.Decrement(ref _inFlight);
        ReleaseSubmitted();

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

        // Время декодирования. Без B-кадров энкодер его не ставит, и оно равно
        // времени показа. С B-кадрами (их может включить сам драйвер, когда адаптер
        // качества снимает режим низкой задержки) кадры идут в порядке
        // декодирования, и время показа скачет — контейнеру нужны оба.
        long dts = long.MinValue;
        try { dts = (long)sample.GetUInt64(SampleAttributeKeys.DecodeTimestamp); } catch { }
        if (dts != long.MinValue && dts != sample.SampleTime && !_loggedBFrames)
        {
            _loggedBFrames = true;
            Log.Info("Encoder", "Энкодер выдаёт кадры в порядке декодирования (B-кадры) — время декодирования учитывается");
        }

        Interlocked.Increment(ref FramesEncoded);
        Diagnostics.PipelineProbe.DrainOutput.Add(drainStart, Diagnostics.PipelineProbe.Now());
        FrameEncoded?.Invoke(new EncodedFrame(_scratch, 0, currentLength,
                                              sample.SampleTime, sample.SampleDuration, keyframe, dts));
    }

    private static ulong PackLong(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public void Dispose()
    {
        if (_nvenc is not null) { DisposeNvenc(); return; }
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

        // Пейсер держит КОНТЕКСТ захвата и текстуры пула: пока он жив, ни то ни
        // другое трогать нельзя. Секунды мало ровно в том сценарии, где он и
        // залипает (GPU завален работой игры и CopyResource стоит), поэтому ждём
        // дольше, а при неудаче оставляем пул сборщику — как это уже сделано с MFT.
        bool pacerExited = _pacerThread?.Join(5000) ?? true;
        bool exited = _eventThread?.Join(2000) ?? true;
        lock (_queueLock) _inputQueue.Clear();

        if (pacerExited) { _copyPool?.Dispose(); _copyPool = null; }
        else
        {
            Log.Warn("Encoder", "Пейсер не завершился за 5 секунд — пул текстур оставлен сборщику");
            _copyPool = null;
        }
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
