using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Aura.Core.Capture;

/// <summary>GPU-масштабирование оконного BGRA-кадра в фиксированный чёрный холст.</summary>
internal sealed class WindowFrameNormalizer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private int _sourceWidth;
    private int _sourceHeight;

    public WindowFrameNormalizer(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int canvasWidth,
        int canvasHeight)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (canvasWidth <= 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        if (canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(canvasHeight));
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();
    }

    public void Normalize(ID3D11Texture2D source, ID3D11Texture2D destination)
    {
        Texture2DDescription sourceDescription = source.Description;
        Texture2DDescription destinationDescription = destination.Description;
        int sourceWidth = checked((int)sourceDescription.Width);
        int sourceHeight = checked((int)sourceDescription.Height);
        if (destinationDescription.Width != (uint)_canvasWidth ||
            destinationDescription.Height != (uint)_canvasHeight)
        {
            throw new InvalidOperationException("Размер texture-назначения не совпадает с canvas");
        }

        if (sourceWidth == _canvasWidth && sourceHeight == _canvasHeight)
        {
            _context.CopyResource(destination, source);
            return;
        }

        EnsureProcessor(sourceWidth, sourceHeight);
        VideoRect fit = VideoOutputGeometry.Fit(
            sourceWidth, sourceHeight, _canvasWidth, _canvasHeight);

        using ID3D11RenderTargetView renderTarget = _device.CreateRenderTargetView(destination);
        _context.ClearRenderTargetView(renderTarget, new Color4(0, 0, 0, 1));

        using ID3D11VideoProcessorInputView input = _videoDevice.CreateVideoProcessorInputView(
            source,
            _enumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
            });
        using ID3D11VideoProcessorOutputView output = _videoDevice.CreateVideoProcessorOutputView(
            destination,
            _enumerator!,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });

        _videoContext.VideoProcessorSetStreamSourceRect(
            _processor!,
            0,
            true,
            new RawRect(0, 0, sourceWidth, sourceHeight));
        _videoContext.VideoProcessorSetStreamDestRect(
            _processor!,
            0,
            true,
            new RawRect(fit.X, fit.Y, fit.X + fit.Width, fit.Y + fit.Height));

        var stream = new VideoProcessorStream { Enable = true, InputSurface = input };
        _videoContext.VideoProcessorBlt(_processor!, output, 0, 1, [stream]);
    }

    private void EnsureProcessor(int sourceWidth, int sourceHeight)
    {
        if (_processor is not null &&
            sourceWidth == _sourceWidth && sourceHeight == _sourceHeight)
        {
            return;
        }

        _processor?.Dispose();
        _enumerator?.Dispose();
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;

        var description = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(60, 1),
            InputWidth = (uint)sourceWidth,
            InputHeight = (uint)sourceHeight,
            OutputFrameRate = new Rational(60, 1),
            OutputWidth = (uint)_canvasWidth,
            OutputHeight = (uint)_canvasHeight,
            Usage = VideoUsage.OptimalQuality
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(description);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        try { _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false); }
        catch { }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
