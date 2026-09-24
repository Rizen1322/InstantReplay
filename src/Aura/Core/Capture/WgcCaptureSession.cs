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
    private readonly Func<CaptureCursorUpdate>? _sampleCursor;
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

    /// <summary>
    /// Насколько раньше своего слота кадр ещё принимается. Ноль, пока система сама
    /// шлёт кадры с избытком (как на Windows 10): там лишние есть всегда.
    /// </summary>
    private long _earlyToleranceTicks;
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
        bool targetCanClose,
        Func<CaptureCursorUpdate>? sampleCursor = null)
    {
        _createItem = createItem ?? throw new ArgumentNullException(nameof(createItem));
        _scope = scope;
        _targetRevision = targetRevision;
        _sourceName = sourceName;
        _forceCursorDisabled = forceCursorDisabled;
        _targetCanClose = targetCanClose;
        _sampleCursor = sampleCursor;

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
        D3DDevice = Aura.Core.Diagnostics.GpuResourceLedger.Track(device, "захват WGC");
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
            _earlyToleranceTicks = 0;
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
            LimitUpdateRate(targetFps);
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

    /// <summary>
    /// Попросить систему не присылать кадры чаще, чем нам нужно.
    ///
    /// ЗАЧЕМ. Отбор по времени в <see cref="DrainFrames"/> отбрасывает лишние кадры
    /// УЖЕ ПОСЛЕ того, как система их сделала: на мониторе 144 или 240 Гц DWM
    /// композитит и копирует вдвое-вчетверо больше кадров, чем попадёт в запись,
    /// и платит за это видеокарта — та самая, на которой в этот момент идёт игра.
    /// MinUpdateInterval переносит ограничение на сторону системы: лишние кадры
    /// просто не делаются.
    ///
    /// Свойство живёт на IGraphicsCaptureSession5 и появилось в Windows 11 22H2.
    /// На Windows 10 обращение к нему отвечает E_NOINTERFACE, поэтому программный
    /// отбор остаётся на месте и работает как раньше — он же страхует и случай,
    /// когда система приняла интервал, но соблюдает его неточно.
    /// </summary>
    private void LimitUpdateRate(int targetFps)
    {
        if (targetFps <= 0 || _session is null) return;
        try
        {
            // Просим ПОЛОВИНУ интервала кадра, а не весь.
            //
            // ЗАЧЕМ. DWM отдаёт кадры только на синхроимпульсах монитора. Попроси мы
            // ровно 16.67 мс при 60 fps — на мониторе 144 Гц следующий разрешённый
            // импульс придётся через три периода, 20.8 мс, и запись получила бы 48 кадров
            // в секунду вместо 60 (остальное пейсер закрыл бы дубликатами — рывки). На
            // 120 Гц интервал совпадает с двумя периодами впритык, и любой джиттер
            // выбрасывал бы кадр без замены. С половиной интервала система отдаёт с
            // запасом (на 144 Гц — 72 кадра вместо 144, то есть вдвое меньше работы
            // для DWM), а ровно 60 из них выбирает программный отбор ниже.
            long wanted = 10_000_000L / targetFps / 2;
            TimeSpan? accepted = Interop.CaptureInterop.TrySetMinUpdateInterval(
                _session, TimeSpan.FromTicks(wanted));
            if (accepted is { } interval)
            {
                // Когда кадров приходит впритык, их метки времени гуляют вокруг сетки на
                // доли периода. Четверть кадра допуска не даёт выбросить кадр, пришедший
                // чуть раньше своего слота: средняя частота выше заданной всё равно не
                // поднимется — дедлайн каждый раз сдвигается на целый интервал.
                _earlyToleranceTicks = _minFrameIntervalTicks / 4;
                Log.Info("Capture", $"{_sourceName}: система ограничена интервалом " +
                                    $"{interval.TotalMilliseconds:F2} мс (MinUpdateInterval), " +
                                    $"точную частоту {targetFps} кадров/с держит программный отбор");
            }
            else
            {
                Log.Info("Capture", $"{_sourceName}: MinUpdateInterval недоступен — частоту держит программный отбор");
            }
        }
        catch (Exception ex)
        {
            // Ожидаемо на Windows 10 — пишем один раз при подготовке сессии, не в цикле.
            Log.Info("Capture", $"{_sourceName}: MinUpdateInterval не применился ({ex.Message}) — " +
                                "частоту держит программный отбор");
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
            RecordArrivalLag(ticks);
            if (_minFrameIntervalTicks > 0)
            {
                if (_nextFrameDeadline == 0) _nextFrameDeadline = ticks;
                if (ticks < _nextFrameDeadline - _earlyToleranceTicks) continue;
                _nextFrameDeadline += _minFrameIntervalTicks;
                if (ticks - _nextFrameDeadline > _minFrameIntervalTicks * 4)
                    _nextFrameDeadline = ticks + _minFrameIntervalTicks;
            }

            Interlocked.Increment(ref _framesAccepted);
            using var texture = CaptureInterop.GetTexture(frame.Surface);
            CaptureCursorUpdate cursor = _sampleCursor?.Invoke() ??
                (_forceCursorDisabled
                ? new CaptureCursorUpdate(
                    CaptureCursorMode.Separate,
                    HasPosition: false,
                    Visible: false,
                    X: 0,
                    Y: 0,
                    Shape: null)
                : CaptureCursorUpdate.SystemComposed);
            FrameArrived?.Invoke(new CapturedSurface(
                texture,
                ticks,
                _generation,
                cursor,
                _scope,
                _targetRevision));
        }
    }

    // Насколько поздно кадр приходит к нам относительно своей метки времени. Под
    // тяжёлой игрой система может собирать кадр для захвата с запаздыванием; если
    // метка отражает это позднее время, видео в записи отстаёт от звука.
    private long _lagSum, _lagMax, _lagCount;

    private void RecordArrivalLag(long ticks)
    {
        long now = (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
                          (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
        long lag = now - ticks;
        _lagSum += lag; _lagCount++;
        if (lag > _lagMax) _lagMax = lag;
        if (_lagCount >= 3600)
        {
            Log.Info("Capture", $"{_sourceName}: кадр приходит через {_lagSum / _lagCount / 10_000.0:F1} мс " +
                                $"после своей метки (пик {_lagMax / 10_000.0:F1} мс)");
            _lagSum = _lagMax = _lagCount = 0;
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
        if (_framePool is not null) _framePool.FrameArrived -= OnFrameArrived;
        if (_item is not null && _targetCanClose) _item.Closed -= OnItemClosed;
        _item = null;

        // Сначала дожидаемся колбэка, и только потом закрываем пул кадров: закрытие
        // пула ждёт колбэк БЕЗ ограничения. Если колбэк завис (видеокарта или драйвер
        // встали на копии кадра), закрытие вешало весь снос конвейера под замком
        // жизненного цикла, а с ним и приложение. Пул и сессию тогда бросаем.
        long deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref _callbacksInFlight) > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(1);
        if (Volatile.Read(ref _callbacksInFlight) > 0)
        {
            Log.Warn("Capture", $"Колбэк {_sourceName} не завершился за 2 секунды — пул кадров оставлен, чтобы не повиснуть");
            _abandoned = true;
            _session = null;
            _framePool = null;
            return;
        }

        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
    }

    /// <summary>Колбэк завис, и объекты захвата брошены: устройство под ним освобождать нельзя.</summary>
    private bool _abandoned;

    public void Dispose()
    {
        Stop();
        if (_abandoned) return;   // висящий колбэк ещё держит устройство
        _winrtDevice.Dispose();
        D3DContext.Dispose();
        D3DDevice.Dispose();
    }
}
