using Vortice.Direct3D11;
using Vortice.DXGI;

namespace InstantReplay.Core.Capture;

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
    public void Configure(int srcWidth, int srcHeight, int targetVertical, int fps)
    {
        lock (_sync)
        {
            ReleaseCore();

            _srcW = srcWidth; _srcH = srcHeight;
            OutHeight = Math.Min(targetVertical, srcHeight) & ~1;
            OutWidth = (int)Math.Round((double)srcWidth / srcHeight * OutHeight) & ~1;

            var desc = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational((uint)fps, 1),
                InputWidth = (uint)srcWidth,
                InputHeight = (uint)srcHeight,
                OutputFrameRate = new Rational((uint)fps, 1),
                OutputWidth = (uint)OutWidth,
                OutputHeight = (uint)OutHeight,
                Usage = VideoUsage.PlaybackNormal
            };
            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(desc);
            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

            // Вход: рабочий стол = full-range RGB (0-255). Выход: BT.709 limited (16-235) —
            // ровно то, что плееры ожидают от H.264/HEVC. Раньше выход был помечен как
            // full-range (Nominal_Range=2), а плееры декодировали как limited —
            // отсюда «накинутый цветокор» (пережатый контраст, серые чёрные).
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
            {
                Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 0
            });
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
            {
                Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0,
                Nominal_Range = 1 // D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235
            });

            _pool = new ID3D11Texture2D?[PoolSize];
            _outputViews = new ID3D11VideoProcessorOutputView?[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                _pool[i] = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)OutWidth,
                    Height = (uint)OutHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.VideoEncoder,
                    CPUAccessFlags = CpuAccessFlags.None
                });
                _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(
                    _pool[i]!, _enumerator,
                    new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D });
            }
            _poolIndex = 0;
        }
    }

    /// <summary>Конвертирует BGRA-кадр в NV12 из пула. Возвращает текстуру пула (не Dispose-ить!).</summary>
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
