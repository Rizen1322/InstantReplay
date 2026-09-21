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
    private readonly WindowFrameNormalizer? _windowNormalizer;
    private readonly CaptureFrameAdmissionGate _admissionGate;
    private readonly CaptureFrameBroker<GpuCaptureFrameSlot> _broker;
    private readonly ID3D11Device _device;
    private readonly object _publishSync = new();
    private ID3D11Texture2D? _windowCursorClean;
    private ID3D11Texture2D? _windowCursorComposed;
    private bool _disposed;
    private long _framesRejected;

    public GpuCaptureFrameBroker(
        ID3D11Device device,
        ID3D11DeviceContext context,
        int width,
        int height,
        bool separateCursor,
        long generation,
        long targetRevision = 0,
        bool windowEpisode = false,
        bool hybridEpisode = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        _device = device;
        _context = context;
        _separateCursor = separateCursor;
        _cursorOverlay = separateCursor ? new CursorOverlay(device, context) : null;
        if (windowEpisode && hybridEpisode)
            throw new ArgumentException("Episode не может быть одновременно window и hybrid");
        _windowNormalizer = windowEpisode || hybridEpisode
            ? new WindowFrameNormalizer(device, context, width, height)
            : null;
        _admissionGate = new CaptureFrameAdmissionGate(
            generation,
            targetRevision,
            hybridEpisode
                ? CaptureFrameAdmissionMode.Hybrid
                : windowEpisode
                    ? CaptureFrameAdmissionMode.Window
                    : CaptureFrameAdmissionMode.Monitor);
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
    public long FramesRejected => Interlocked.Read(ref _framesRejected);

    public CaptureBrokerDiagnostics GetDiagnostics(long invalidCursorShapes)
    {
        DdaCursorSnapshot cursor = _cursorState.Current;
        return new CaptureBrokerDiagnostics(
            _broker.Generation,
            _broker.FramesPublished,
            _broker.FramesDroppedNoSlot,
            FramesRejected,
            _broker.LatestTimestamp,
            cursor.Revision,
            invalidCursorShapes);
    }

    public bool Publish(in CapturedSurface surface)
    {
        lock (_publishSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CapturedSurface captured = surface;
            if (!_admissionGate.Accept(
                    captured.Generation,
                    captured.TargetRevision,
                    captured.Scope,
                    captured.RouteEpoch))
            {
                Interlocked.Increment(ref _framesRejected);
                return false;
            }

            DdaCursorSnapshot cursor = default;
            if (_separateCursor)
            {
                if (captured.Cursor.Mode == CaptureCursorMode.SystemComposed)
                    return _broker.Publish(surface.Generation, surface.Timestamp, slot =>
                    {
                        slot.Scope = captured.Scope;
                        slot.CleanCurrent = false;
                        CopyFrame(captured, slot.Output);
                    });

                _cursorState.Apply(captured.Generation, captured.Cursor);
                cursor = _cursorState.Current;
            }

            return _broker.Publish(surface.Generation, surface.Timestamp, slot =>
            {
                slot.Scope = captured.Scope;
                slot.CleanCurrent = false;
                if (!_separateCursor)
                {
                    CopyFrame(captured, slot.Output);
                    return;
                }

                if (captured.Scope == CaptureSurfaceScope.GameWindow)
                {
                    ComposeWindowFrame(captured.Texture, slot.Output, cursor);
                    return;
                }

                CopyFrame(captured, slot.Clean!);
                slot.CleanCurrent = true;
                _cursorOverlay!.Compose(slot.Clean!, slot.Output, cursor);
            });
        }
    }

    private void ComposeWindowFrame(
        ID3D11Texture2D source,
        ID3D11Texture2D destination,
        in DdaCursorSnapshot cursor)
    {
        EnsureWindowCursorTextures(source.Description);
        _context.CopyResource(_windowCursorClean!, source);
        _cursorOverlay!.Compose(_windowCursorClean!, _windowCursorComposed!, cursor);
        (_windowNormalizer ?? throw new InvalidOperationException(
            "Оконный normalizer не создан"))
            .Normalize(_windowCursorComposed!, destination);
    }

    private void EnsureWindowCursorTextures(in Texture2DDescription source)
    {
        if (_windowCursorClean is not null &&
            _windowCursorClean.Description.Width == source.Width &&
            _windowCursorClean.Description.Height == source.Height)
        {
            return;
        }

        _windowCursorComposed?.Dispose();
        _windowCursorClean?.Dispose();
        var description = new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
        };
        _windowCursorClean = _device.CreateTexture2D(description);
        _windowCursorComposed = _device.CreateTexture2D(description);
    }

    private void CopyFrame(in CapturedSurface captured, ID3D11Texture2D destination)
    {
        if (captured.Scope == CaptureSurfaceScope.GameWindow)
        {
            (_windowNormalizer ?? throw new InvalidOperationException(
                "Оконный кадр пришёл в мониторный GPU-брокер"))
                .Normalize(captured.Texture, destination);
            return;
        }

        _context.CopyResource(destination, captured.Texture);
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

    /// <param name="withoutCursor">
    /// Отдать кадр без дорисованного курсора, если он есть. Нужен оверлею выделения
    /// области: он показывает замороженный экран, а настоящий курсор человека ходит
    /// поверх. Вшитый в картинку курсор выглядел как второй, а если кадр попадал
    /// сразу после прошлого оверлея — как застывшее перекрестие выделения.
    /// У WGC отдельного чистого кадра нет, курсор там рисует система.
    /// </param>
    /// <param name="maxAge">
    /// Насколько старым позволено быть кадру, если нового так и не пришло.
    ///
    /// ЗАЧЕМ ОГРАНИЧЕНИЕ. Раньше запасным вариантом был последний готовый кадр
    /// ЛЮБОГО возраста. На статичном экране Desktop Duplication не присылает ничего,
    /// и «последний» мог быть снят минуты назад — до того, как человек переключился
    /// в другое окно. Снимок области выходил с содержимым, которого на экране уже
    /// нет: типичный отчёт — «снимал поверх своего окна, а получил окно под ним».
    /// Старый кадр честнее не отдавать вовсе: вызывающий откроет свою сессию
    /// захвата и снимет то, что на экране сейчас.
    /// </param>
    public bool TryUseFreshestMonitor(
        long generation,
        TimeSpan waitForNewFrame,
        TimeSpan maxAge,
        Action<ID3D11Texture2D> use,
        bool withoutCursor,
        out string rejection)
    {
        ArgumentNullException.ThrowIfNull(use);
        rejection = "";
        if (!_broker.TryLeaseFreshest(
                generation,
                waitForNewFrame,
                out CaptureFrameLease<GpuCaptureFrameSlot>? inner,
                out bool receivedNewFrame))
        {
            rejection = "готового кадра нет";
            return false;
        }

        using var lease = new GpuCaptureFrameLease(inner!);
        if (!ScreenshotFramePolicy.CanUseLiveFrame(lease.Scope))
        {
            rejection = $"кадр не мониторный ({lease.Scope})";
            return false;
        }

        long ageTicks = Math.Max(0, NowTicks() - lease.Timestamp);
        if (!receivedNewFrame && ageTicks > maxAge.Ticks)
        {
            rejection = $"последний кадр устарел на {ageTicks / 10_000} мс";
            return false;
        }

        Logging.Log.Info("Screenshot", receivedNewFrame
            ? "Кадр буфера: свежий"
            : $"Кадр буфера: последний готовый, возраст {ageTicks / 10_000} мс");

        use(withoutCursor ? lease.CleanTexture ?? lease.Texture : lease.Texture);
        return true;
    }

    /// <summary>Те же 100-нс тики, в которых источники захвата ставят метки кадрам.</summary>
    private static long NowTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
               (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public void Dispose()
    {
        lock (_publishSync)
        {
            if (_disposed) return;
            _disposed = true;
            _cursorOverlay?.Dispose();
            _windowCursorComposed?.Dispose();
            _windowCursorClean?.Dispose();
            _windowNormalizer?.Dispose();
            _broker.Dispose();
        }
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
    public CaptureSurfaceScope Scope { get; set; }

    /// <summary>
    /// Лежит ли в <see cref="Clean"/> этот же кадр. Чистая копия пишется не во всех
    /// ветках публикации: когда курсор уже нарисован системой, кадр идёт сразу в
    /// Output, и Clean остаётся от прошлого раза — или пустой, если слот новый.
    /// Отдать такую текстуру значило отдать чёрный или старый снимок.
    /// </summary>
    public bool CleanCurrent { get; set; }

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
    /// <summary>Тот же кадр до дорисовки курсора; null — чистой копии у источника нет.</summary>
    public ID3D11Texture2D? CleanTexture =>
        _inner is { } inner
            ? (inner.Slot.CleanCurrent ? inner.Slot.Clean : null)
            : throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));
    public long Timestamp =>
        _inner?.Timestamp ?? throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));
    public long Generation =>
        _inner?.Generation ?? throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));
    public CaptureSurfaceScope Scope =>
        _inner?.Slot.Scope ?? throw new ObjectDisposedException(nameof(GpuCaptureFrameLease));

    public void Dispose() => Interlocked.Exchange(ref _inner, null)?.Dispose();
}
