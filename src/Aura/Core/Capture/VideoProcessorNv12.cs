using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Capture;

/// <summary>
/// Конвертация BGRA (кадр захвата) → NV12 (вход энкодера) + масштабирование
/// до целевого разрешения — целиком на GPU через ID3D11VideoProcessor.
/// Это тот самый шаг, который в наивных реализациях делают через hwdownload
/// (GPU→RAM→CPU-swscale) и получают 30% CPU. Здесь копий в RAM нет вообще.
/// Держит пул выходных NV12-текстур, т.к. асинхронный MFT-энкодер может
/// удерживать несколько кадров одновременно.
/// </summary>
public sealed class VideoProcessorNv12 : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView?[] _outputViews = [];
    private ID3D11Texture2D?[] _pool = [];
    private int _poolIndex;
    private int _srcW, _srcH;

    public int OutWidth { get; private set; }
    public int OutHeight { get; private set; }

    /// <summary>
    /// Формат кадра на выходе: NV12 (восемь бит) или P010 (десять).
    ///
    /// Решается здесь, а не в настройках, потому что последнее слово за драйвером:
    /// видеопроцессор обязан уметь писать в этот формат, и спросить его об этом
    /// можно только у готового перечислителя.
    /// </summary>
    public Format OutputFormat { get; private set; } = Format.NV12;

    /// <summary>Пишем ли мы сейчас десять бит.</summary>
    public bool TenBit => OutputFormat == Format.P010;

    private const int PoolSize = 8;
    private readonly object _sync = new();

    public VideoProcessorNv12(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();
    }

    /// <summary>
    /// (Пере)инициализация под размер источника и целевую вертикаль (720/1080/1440/2160).
    /// Ширина считается по аспекту источника и выравнивается до чётной (требование NV12).
    /// </summary>
    /// <param name="outputBase">
    /// Размер, от которого считается выход. Обычно это разрешение рабочего стола из
    /// реестра, а не текущий режим экрана: так размер записи не меняется, когда игра
    /// переключает экран на своё разрешение. Кадр другой пропорции видеопроцессор
    /// растягивает на весь выход — без целевого прямоугольника он так и работает.
    /// null — считать от размера источника, как раньше.
    /// </param>
    public void Configure(int srcWidth, int srcHeight, int targetVertical, int fps,
                          bool preferTenBit = false, (int Width, int Height)? outputBase = null)
    {
        lock (_sync)
        {
            ReleaseCore();

            _srcW = srcWidth; _srcH = srcHeight;
            var (baseWidth, baseHeight) = outputBase ?? (srcWidth, srcHeight);
            OutHeight = Math.Min(targetVertical, baseHeight) & ~1;
            OutWidth = (int)Math.Round((double)baseWidth / baseHeight * OutHeight) & ~1;

            if (outputBase is not null && (long)srcWidth * baseHeight != (long)srcHeight * baseWidth)
                Logging.Log.Info("Capture", $"Экран {srcWidth}x{srcHeight} растягивается в запись " +
                                            $"{OutWidth}x{OutHeight} — размер записи держится по рабочему столу");

            var desc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)fps, 1),
                InputWidth = (uint)srcWidth,
                InputHeight = (uint)srcHeight,
                OutputFrameRate = new Rational((uint)fps, 1),
                OutputWidth = (uint)OutWidth,
                OutputHeight = (uint)OutHeight,
                // При уменьшении разрешения драйвер выбирает более качественный
                // фильтр масштабирования. В родном разрешении это только конверсия
                // RGB→NV12, поэтому лишней работы не добавляем.
                Usage = srcWidth != OutWidth || srcHeight != OutHeight
                    ? VideoUsage.OptimalQuality
                    : VideoUsage.PlaybackNormal
            };
            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

            OutputFormat = preferTenBit &&
                           SupportsOutput(_enumerator, Format.P010) &&
                           SupportsRenderTarget(_device, Format.P010)
                ? Format.P010
                : Format.NV12;
            if (preferTenBit && OutputFormat != Format.P010)
                Logging.Log.Info("Capture", "Видеопроцессор драйвера не пишет в P010 — остаёмся на восьми битах");


            // Вход: рабочий стол = full-range RGB (0-255). Выход: BT.709 limited (16-235) —
            // ровно то, что плееры ожидают от H.264/HEVC. Раньше выход был помечен как
            // full-range (Nominal_Range=2), а плееры декодировали как limited —
            // отсюда «накинутый цветокор» (пережатый контраст, серые чёрные).
            if (!TrySetColorSpaces())
            {
                _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
                {
                    Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 0
                });
                _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
                {
                    Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0,
                    Nominal_Range = 1 // D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235
                });
            }

            // Драйверная «автообработка» кадра — шумодав, повышение резкости и прочее,
            // что вендор включает по умолчанию для ВОСПРОИЗВЕДЕНИЯ видео. Для записи
            // это чужеродная постобработка: она мылит или перешарпливает картинку ещё
            // до энкодера, и мы платим за это битрейтом. Выключаем явно (так же
            // поступает OBS). Ошибка не фатальна — не все драйверы дают этот вызов.
            try { _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false); }
            catch (Exception ex) { Logging.Log.Warn("Capture", $"Автообработка видеопроцессора: {ex.Message}"); }

            if (srcWidth != OutWidth || srcHeight != OutHeight)
                Logging.Log.Info("Capture", $"Масштабирование {srcWidth}x{srcHeight} → {OutWidth}x{OutHeight} " +
                                            "выполняет видеопроцессор драйвера (алгоритм выбирает он сам). " +
                                            "Запись в родном разрешении монитора этот шаг убирает.");

            // Пул выходных кадров. Ответ CheckVideoProcessorFormat — это ещё не
            // обещание: у драйвера бывает заявлена поддержка формата на выходе, а
            // текстура с нужными флагами привязки в нём всё равно не создаётся.
            // В замере это выглядело как E_INVALIDARG при запуске записи, то есть
            // повтор не включался вовсе. Поэтому пробуем и, если не вышло,
            // возвращаемся на NV12 — восемь бит работают всегда.
            if (!TryBuildPool(OutputFormat) )
            {
                if (OutputFormat == Format.NV12)
                    throw new InvalidOperationException("Видеопроцессор не отдал пул кадров в NV12");

                Logging.Log.Warn("Capture",
                    "Видеопроцессор заявил P010, но кадр в нём не создался — возвращаюсь на восемь бит");
                OutputFormat = Format.NV12;
                if (!TryBuildPool(OutputFormat))
                    throw new InvalidOperationException("Видеопроцессор не отдал пул кадров в NV12");
            }
            _poolIndex = 0;
        }
    }

    /// <summary>
    /// Задать цветовые пространства современным способом — через DXGI_COLOR_SPACE_TYPE.
    ///
    /// ЗАЧЕМ ВМЕСТО СТАРОГО ВЫЗОВА. D3D11_VIDEO_PROCESSOR_COLOR_SPACE описывает цвет
    /// четырьмя разрозненными полями и не умеет выразить ничего, кроме BT.601/BT.709:
    /// для BT.2020 и кривой PQ там просто нет значений. Версия с DXGI-перечислением
    /// называет пространство целиком и однозначно, и это единственный путь, по которому
    /// в конвейер когда-нибудь войдёт HDR. Заодно исчезает двусмысленность полного и
    /// урезанного диапазона, из-за которой запись однажды уехала по контрасту.
    ///
    /// Значения подобраны так, чтобы поведение НЕ изменилось: на входе рабочий стол
    /// в полном диапазоне sRGB/BT.709, на выходе YCbCr BT.709 студийного диапазона
    /// (16-235) — ровно то, что задавали четыре поля старого вызова.
    ///
    /// false — драйвер не отдал ID3D11VideoContext1 (интерфейс появился в Windows 8),
    /// вызывающий оставляет старый путь.
    /// </summary>
    private bool TrySetColorSpaces()
    {
        try
        {
            using var context1 = _videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>();
            if (context1 is null) return false;

            context1.VideoProcessorSetStreamColorSpace1(
                _processor!, 0, ColorSpaceType.RgbFullG22NoneP709);
            context1.VideoProcessorSetOutputColorSpace1(
                _processor!, ColorSpaceType.YcbcrStudioG22LeftP709);
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Info("Capture", $"Цветовое пространство через DXGI не задано ({ex.Message}) — " +
                                        "остаётся прежний вызов");
            return false;
        }
    }

    /// <summary>
    /// Можно ли создать в этом формате текстуру, пригодную для вывода видеопроцессора.
    ///
    /// Представление вывода требует привязки RenderTarget, и это отдельный вопрос
    /// от того, умеет ли видеопроцессор писать в такой формат. У NV12 обе проверки
    /// проходят везде, у P010 — не у всех драйверов.
    /// </summary>
    private static bool SupportsRenderTarget(ID3D11Device device, Format format)
    {
        try
        {
            FormatSupport support = device.CheckFormatSupport(format);
            return (support & FormatSupport.RenderTarget) != 0;
        }
        catch (Exception ex)
        {
            Logging.Log.Info("Capture", $"Привязка RenderTarget для {format} не читается: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Создать кольцо выходных кадров в заданном формате. false — не получилось.
    /// Всё уже созданное при неудаче освобождается, чтобы можно было повторить
    /// попытку с другим форматом.
    /// </summary>
    private bool TryBuildPool(Format format)
    {
        // Флаги привязки пробуем по убыванию. VideoEncoder здесь не обязателен:
        // энкодер получает не эту текстуру, а её копию из своего пула, и та
        // создаётся вообще без привязок (см. EncoderTexturePool.Copy). Но на NV12
        // флаг годами стоял и ничего не ломал, поэтому оставляем его первой
        // попыткой, а второй идёт только RenderTarget.
        //
        // Ради этого всё и затевалось: в замере CheckFormatSupport подтверждал для
        // P010 привязку RenderTarget, а CreateTexture2D всё равно отвечал
        // E_INVALIDARG. Значит, драйверу мешает именно сочетание с VideoEncoder.
        return TryBuildPoolWith(format, BindFlags.RenderTarget | BindFlags.VideoEncoder) ||
               TryBuildPoolWith(format, BindFlags.RenderTarget);
    }

    private bool TryBuildPoolWith(Format format, BindFlags bindFlags)
    {
        var pool = new ID3D11Texture2D?[PoolSize];
        var views = new ID3D11VideoProcessorOutputView?[PoolSize];
        try
        {
            for (int i = 0; i < PoolSize; i++)
            {
                pool[i] = Aura.Core.Diagnostics.GpuResourceLedger.Track(_device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)OutWidth,
                    Height = (uint)OutHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = format,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = bindFlags,
                    CPUAccessFlags = CpuAccessFlags.None
                }), "конвертер цвета");
                views[i] = _videoDevice.CreateVideoProcessorOutputView(
                    pool[i]!, _enumerator!,
                    new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
            }

            _pool = pool;
            _outputViews = views;
            if (bindFlags != (BindFlags.RenderTarget | BindFlags.VideoEncoder))
                Logging.Log.Info("Capture", $"Кадр {format} создан с привязкой {bindFlags}");
            return true;
        }
        catch (Exception ex)
        {
            Logging.Log.Info("Capture",
                $"Кадр {format} с привязкой {bindFlags} не создался: {ex.Message}");
            foreach (var v in views) v?.Dispose();
            foreach (var t in pool) t?.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Умеет ли видеопроцессор писать кадр в этот формат.
    ///
    /// CheckVideoProcessorFormat отдаёт флаги поддержки отдельно для входа и для
    /// выхода. Нам нужен именно выход: в этот формат мы пишем результат.
    /// </summary>
    private static bool SupportsOutput(ID3D11VideoProcessorEnumerator enumerator, Format format)
    {
        try
        {
            VideoProcessorFormatSupport support = enumerator.CheckVideoProcessorFormat(format);
            return (support & VideoProcessorFormatSupport.Output) != 0;
        }
        catch (Exception ex)
        {
            Logging.Log.Info("Capture", $"Поддержка формата {format} не читается: {ex.Message}");
            return false;
        }
    }

    /// <summary>Конвертирует BGRA-кадр в NV12 или P010 из пула. Возвращает текстуру пула (не Dispose-ить!).</summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D bgraFrame)
    {
        lock (_sync)
        {
            if (_processor is null || _enumerator is null)
                throw new InvalidOperationException("VideoProcessorNv12 не сконфигурирован");

            int i = _poolIndex;
            _poolIndex = (_poolIndex + 1) % PoolSize;

            // Представление создаётся на каждый кадр и сразу освобождается.
            // Кешировать его нельзя: удержание ссылки на текстуру мешает пулу WGC
            // переиспользовать буферы — в играх это давало микрофризы.
            using var inputView = _videoDevice.CreateVideoProcessorInputView(
                bgraFrame, _enumerator,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
                });

            var stream = new VideoProcessorStream
            {
                Enable = true,
                InputSurface = inputView
            };
            _videoContext.VideoProcessorBlt(_processor, _outputViews[i]!, 0, 1, [stream]);
            return _pool[i]!;
        }
    }

    private void ReleaseCore()
    {
        foreach (var v in _outputViews) v?.Dispose();
        foreach (var t in _pool) t?.Dispose();
        _outputViews = []; _pool = [];
        _processor?.Dispose(); _processor = null;
        _enumerator?.Dispose(); _enumerator = null;
    }

    public void Dispose()
    {
        lock (_sync) ReleaseCore();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
