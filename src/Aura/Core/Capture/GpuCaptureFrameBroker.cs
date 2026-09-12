using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Diagnostics;

namespace Aura.Core.Capture;

internal sealed class GpuCaptureFrameBroker : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly bool _separateCursor;
    private readonly DdaCursorState _cursorState = new();
    private readonly CursorOverlay? _cursorOverlay;
    private readonly CaptureFrameBroker<GpuCaptureFrameSlot> _broker;
    private bool _disposed;

    public GpuCaptureFrameBroker(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int width,
        int height,
        bool separateCursor,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        _context = context;
        _separateCursor = separateCursor;
        _cursorOverlay = separateCursor ? new CursorOverlay(device, context) : null;
        _broker = new CaptureFrameBroker<GpuCaptureFrameSlot>(
            () => new GpuCaptureFrameSlot(device, width, height, separateCursor),
            slot => slot.Dispose());
        _cursorState.Reset(generation);
        _broker.Reset(generation);
    }

    public long FramesPublished => _broker.FramesPublished;
    public long FramesDroppedNoSlot => _broker.FramesDroppedNoSlot;
    public long LatestTimestamp => _broker.LatestTimestamp;
    public long Generation => _broker.Generation;

    public CaptureBrokerDiagnostics GetDiagnostics(long invalidCursorShapes)
    {
        DdaCursorSnapshot cursor = _cursorState.Current;
        return new CaptureBrokerDiagnostics(
            _broker.Generation,
            _broker.FramesPublished,
            _broker.FramesDroppedNoSlot,
            _broker.LatestTimestamp,
            cursor.Revision,
            invalidCursorShapes);
    }

    public bool Publish(in CapturedSurface surface)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CapturedSurface captured = surface;
        DdaCursorSnapshot cursor = default;
        if (_separateCursor)
        {
            _cursorState.Apply(captured.Generation, captured.Cursor);
            cursor = _cursorState.Current;
        }

        return _broker.Publish(surface.Generation, surface.Timestamp, slot =>
        {
            if (!_separateCursor)
            {
                _context.CopyResource(slot.Output, captured.Texture);
                return;
            }

            _context.CopyResource(slot.Clean!, captured.Texture);
            _cursorOverlay!.Compose(slot.Clean!, slot.Output, cursor);
        });
    }

    public bool TryLeaseLatest(long generation, out GpuCaptureFrameLease? lease)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lease = null;
        if (!_broker.TryLeaseLatest(generation, out CaptureFrameLease<GpuCaptureFrameSlot>? inner))
            return false;

        lease = new GpuCaptureFrameLease(inner!);
        return true;
    }

    public bool TryUseLatest(long generation, Action<ID3D11Texture2D> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        if (!TryLeaseLatest(generation, out GpuCaptureFrameLease? lease))
            return false;

        try
        {
            use(lease!.Texture);
            return true;
        }
        finally
        {
            lease!.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cursorOverlay?.Dispose();
        _broker.Dispose();
    }
}

internal sealed class GpuCaptureFrameSlot : IDisposable
{
    public GpuCaptureFrameSlot(
        ID3D11Device device,
        int width,
        int height,
        bool createCleanTexture)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        };

        Output = device.CreateTexture2D(description);
        if (createCleanTexture)
            Clean = device.CreateTexture2D(description);
    }

    public ID3D11Texture2D Output { get; }
    public ID3D11Texture2D? Clean { get; }

    public void Dispose()
    {
        Clean?.Dispose();
        Output.Dispose();
    }
}

internal sealed class GpuCaptureFrameLease : IDisposable
{
    private CaptureFrameLease<GpuCaptureFrameSlot>? _inner;

    internal GpuCaptureFrameLease(CaptureFrameLease<GpuCaptureFrameSlot> inner) =>
        _inner = inner;

    public ID3D11Texture2D Texture =>
        _inner?.Slot.Output ?? throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));
    public long Timestamp =>
        _inner?.Timestamp ?? throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));
    public long Generation =>
        _inner?.Generation ?? throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));

    public void Dispose() => Interlocked.Exchange(ref _inner, null)?.Dispose();
}
