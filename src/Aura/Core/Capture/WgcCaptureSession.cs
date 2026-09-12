using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>Общий GPU-resident цикл WGC для monitor- и HWND-источников.</summary>
internal sealed class WgcCaptureSession : IScreenCapture
{
    private readonly Func<GraphicsCaptureItem> _createItem;
    private readonly CaptureSurfaceScope _scope;
    private readonly long _targetRevision;
    private readonly string _sourceName;
    private readonly bool _forceCursorDisabled;
    private readonly bool _targetCanClose;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly object _sync = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private int _monitorIndex;
    private int _targetFps;
    private long _generation;
    private volatile bool _prepared;
    private bool _started;
    private long _minFrameIntervalTicks;
    private long _nextFrameDeadline;
    private long _framesReceived;
    private long _framesAccepted;
    private int _callbacksInFlight;
    private int _closedReported;

    public WgcCaptureSession(
        int monitorIndex,
        Func<GraphicsCaptureItem> createItem,
        CaptureSurfaceScope scope,
        long targetRevision,
        string sourceName,
        bool forceCursorDisabled,
        bool targetCanClose)
    {
        _createItem = createItem ?? throw new ArgumentNullException(nameof(createItem));
        _scope = scope;
        _targetRevision = targetRevision;
        _sourceName = sourceName;
        _forceCursorDisabled = forceCursorDisabled;
        _targetCanClose = targetCanClose;

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        using var adapter = GpuInfo.OpenAdapterForMonitor(monitorIndex);
        if (adapter is null)
            Log.Warn("Capture", $"Адаптер монитора #{monitorIndex} не определён — беру по умолчанию");

        D3D11.D3D11CreateDevice(
            adapter,
            adapter is null ? DriverType.Hardware : DriverType.Unknown,
            flags,
            levels,
            out ID3D11Device device,
            out _,
            out ID3D11DeviceContext context).CheckError();
        D3DDevice = device;
        D3DContext = context;

        using var multithread = D3DDevice.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
        GpuPriority.TryRaise(D3DDevice);
        _winrtDevice = CaptureInterop.CreateWinRtDevice(D3DDevice);
    }

    public ID3D11Device D3DDevice { get; }
    public ID3D11DeviceContext D3DContext { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesAccepted => Interlocked.Read(ref _framesAccepted);
    public long InvalidCursorShapes => 0;

    public event Action<CapturedSurface>? FrameArrived;
    public event Action<CaptureFailure>? Failed;

    public void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation)
    {
        lock (_sync)
        {
            StopInternal();
            _monitorIndex = monitorIndex;
            _targetFps = targetFps;
            _generation = generation;
            _prepared = false;
            _started = false;
            _minFrameIntervalTicks = targetFps > 0 ? 10_000_000L / targetFps : 0;
            _nextFrameDeadline = 0;
            Interlocked.Exchange(ref _framesReceived, 0);
            Interlocked.Exchange(ref _framesAccepted, 0);
            Interlocked.Exchange(ref _closedReported, 0);

            _item = _createItem();
            if (_targetCanClose) _item.Closed += OnItemClosed;
            Width = _item.Size.Width;
            Height = _item.Size.Height;
            if (Width <= 0 || Height <= 0)
                throw new InvalidOperationException($"{_sourceName}: пустой размер {Width}x{Height}");

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                PoolSizeFor(Height),
                _item.Size);
            _framePool.FrameArrived += OnFrameArrived;

            CaptureAccess.EnsureBorderlessAccess();
            _session = _framePool.CreateCaptureSession(_item);
            DisableBorder();
            try { _session.IsCursorCaptureEnabled = !_forceCursorDisabled && captureCursor; }
            catch (Exception ex) { Log.Warn("Capture", $"Настройка курсора недоступна: {ex.Message}"); }

            _prepared = true;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (!_prepared || _session is null)
                throw new InvalidOperationException($"{_sourceName} не подготовлен");
            if (_started)
                throw new InvalidOperationException($"{_sourceName} уже запущен");

            _session.StartCapture();
            _started = true;
            Log.Info("Capture", $"Захват запущен ({_sourceName}): монитор #{_monitorIndex}, " +
                                $"{Width}x{Height}, target {_targetFps} fps");
            LogAdapters(_monitorIndex);
        }
    }

    private void DisableBorder()
    {
        if (!CaptureAccess.IsBorderControlSupported || _session is null) return;
        try
        {
            _session.IsBorderRequired = false;
            if (!CaptureAccess.BorderlessGranted)
                Log.Warn("Capture", "Права на захват без рамки нет — рамка записи останется");
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Не удалось отключить рамку записи: {ex.Message}");
        }
    }

    private int PoolSizeFor(int height)
    {
        int generous = height > 1440 ? 6 : 12;
        if (GpuInfo.Usage(D3DDevice) is not { } vram || vram.BudgetMb <= 0) return generous;

        long frameMb = Math.Max((long)Math.Max(Width, 1) * height * 4 >> 20, 1);
        int affordable = (int)Math.Max(2, vram.BudgetMb / 10 / frameMb);
        int chosen = Math.Min(generous, affordable);
        if (chosen < generous)
            Log.Warn("Capture", $"Видеопамяти мало (бюджет {vram.BudgetMb} МБ) — " +
                                $"беру {chosen} буферов вместо {generous}");
        return chosen;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Interlocked.Increment(ref _callbacksInFlight);
        try
        {
            DrainFrames(sender);
        }
        catch (Exception ex) when (DeviceLoss.IsDeviceLost(ex))
        {
            Log.Warn("Capture", $"{_sourceName}: потеряно устройство ({ex.Message})");
            Failed?.Invoke(new CaptureFailure(
                CaptureFailureKind.DeviceLost,
                ex,
                $"{_sourceName}: потеряно GPU-устройство",
                _generation,
                _targetRevision));
        }
        catch (Exception ex)
        {
            Log.Error("Capture", $"{_sourceName}: кадр не обработан: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _callbacksInFlight);
        }
    }

    private void DrainFrames(Direct3D11CaptureFramePool sender)
    {
        while (true)
        {
            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null) return;

            if (frame.ContentSize.Width != Width || frame.ContentSize.Height != Height)
            {
                int previousWidth = Width;
                int previousHeight = Height;
                Width = frame.ContentSize.Width;
                Height = frame.ContentSize.Height;
                if (Width <= 0 || Height <= 0)
                {
                    ReportFormatChange(previousWidth, previousHeight);
                    return;
                }

                sender.Recreate(
                    _winrtDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    PoolSizeFor(Height),
                    new SizeInt32 { Width = Width, Height = Height });
                ReportFormatChange(previousWidth, previousHeight);
                return;
            }

            long ticks = frame.SystemRelativeTime.Ticks;
            Interlocked.Increment(ref _framesReceived);
            if (_minFrameIntervalTicks > 0)
            {
                if (_nextFrameDeadline == 0) _nextFrameDeadline = ticks;
                if (ticks < _nextFrameDeadline) continue;
                _nextFrameDeadline += _minFrameIntervalTicks;
                if (ticks - _nextFrameDeadline > _minFrameIntervalTicks * 4)
                    _nextFrameDeadline = ticks + _minFrameIntervalTicks;
            }

            Interlocked.Increment(ref _framesAccepted);
            using var texture = CaptureInterop.GetTexture(frame.Surface);
            CaptureCursorUpdate cursor = _forceCursorDisabled
                ? new CaptureCursorUpdate(
                    CaptureCursorMode.Separate,
                    HasPosition: false,
                    Visible: false,
                    X: 0,
                    Y: 0,
                    Shape: null)
                : CaptureCursorUpdate.SystemComposed;
            FrameArrived?.Invoke(new CapturedSurface(
                texture,
                ticks,
                _generation,
                cursor,
                _scope,
                _targetRevision));
        }
    }

    private void ReportFormatChange(int previousWidth, int previousHeight)
    {
        var error = new InvalidOperationException(
            $"Размер источника изменился: {previousWidth}x{previousHeight} → {Width}x{Height}");
        Log.Warn("Capture", $"{_sourceName}: {error.Message} — пересобираю видеоконвейер");
        Failed?.Invoke(new CaptureFailure(
            CaptureFailureKind.CaptureFormatChanged,
            error,
            $"{_sourceName}: изменился размер источника",
            _generation,
            _targetRevision));
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (!_prepared || Interlocked.Exchange(ref _closedReported, 1) != 0) return;
        var error = new InvalidOperationException($"{_sourceName}: GraphicsCaptureItem закрыт");
        Log.Warn("Capture", error.Message);
        Failed?.Invoke(new CaptureFailure(
            CaptureFailureKind.CaptureTargetClosed,
            error,
            $"{_sourceName}: игровое окно закрыто",
            _generation,
            _targetRevision));
    }

    private void LogAdapters(int monitorIndex)
    {
        var used = GpuInfo.Of(D3DDevice);
        var owner = GpuInfo.ForMonitor(monitorIndex);
        Log.Info("Capture", $"Адаптер захвата: {used?.ToString() ?? "неизвестен"}; " +
                            $"монитор #{monitorIndex} на: {owner?.ToString() ?? "неизвестен"}");
        if (used is { } actual && owner is { } expected && actual.Luid != expected.Luid)
            Log.Warn("Capture", "Захват идёт не с адаптера выбранного монитора");
        if (GpuInfo.Usage(D3DDevice) is { } usage)
            Log.Info("Capture", $"Видеопамять при старте: занято {usage.UsedMb} из {usage.BudgetMb} МБ бюджета");
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
    }

    private void StopInternal()
    {
        _prepared = false;
        _started = false;
        _session?.Dispose();
        _session = null;
        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }
        if (_item is not null && _targetCanClose) _item.Closed -= OnItemClosed;
        _item = null;

        long deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref _callbacksInFlight) > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(1);
        if (Volatile.Read(ref _callbacksInFlight) > 0)
            Log.Warn("Capture", $"Колбэк {_sourceName} не завершился за 2 секунды");
    }

    public void Dispose()
    {
        Stop();
        _winrtDevice.Dispose();
        D3DContext.Dispose();
        D3DDevice.Dispose();
    }
}
