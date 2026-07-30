using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using InstantReplay.Core.Interop;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Capture;

/// <summary>
/// Захват монитора через Windows.Graphics.Capture (WGC).
/// Ключевой принцип производительности: кадр НИКОГДА не копируется в RAM
/// (никакого hwdownload) — BGRA-текстура передаётся дальше по конвейеру
/// (конвертация в NV12 и кодирование NVENC/AMF/QSV) целиком в видеопамяти.
/// WGC работает и с fullscreen, и с borderless (в отличие от Desktop Duplication,
/// WGC не отваливается при смене режимов и UAC).
/// </summary>
public sealed class ScreenCaptureSource : IScreenCapture
{
    public ID3D11Device D3DDevice { get; }
    public ID3D11DeviceContext D3DContext { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Кадр: текстура BGRA в VRAM + время кадра (QPC, 100-нс тики).</summary>
    public event Action<ID3D11Texture2D, long>? FrameArrived;

    private readonly IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private readonly object _sync = new();

    private long _minFrameIntervalTicks; // ограничение FPS на стороне захвата
    private long _nextFrameDeadline;     // абсолютный дедлайн следующего кадра (без биений)

    /// <summary>Сколько кадров WGC вообще принёс (до нашего 60fps-фильтра) — диагностика.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    /// <summary>Сколько прошло фильтр и ушло в конвейер.</summary>
    public long FramesAccepted => Interlocked.Read(ref _framesAccepted);
    private long _framesReceived, _framesAccepted;

    public ScreenCaptureSource()
    {
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels,
            out ID3D11Device device, out _, out ID3D11DeviceContext context).CheckError();
        D3DDevice = device;
        D3DContext = context;

        // Многопоточная защита контекста — кодировщик и захват живут в разных потоках
        using var mt = D3DDevice.QueryInterface<ID3D11Multithread>();
        mt.SetMultithreadProtected(true);

        _winrtDevice = CaptureInterop.CreateWinRtDevice(D3DDevice);
    }

    // ---------------- Кадр для скриншота ----------------
    // Постоянную копию каждого кадра не держим: это лишняя копия в горячем пути.
    // Вместо этого копия делается ПО ЗАПРОСУ — скриншот поднимает флаг, ближайший
    // кадр копируется в переиспользуемую текстуру, и вызывающий получает её.
    //
    // Раньше здесь стоял просто `=> false`, и скриншот на Windows 11 каждый раз
    // поднимал ВТОРУЮ сессию WGC: создание устройства, пула кадров и сессии, ожидание
    // кадра и снос всего этого — на глазах у пользователя приложение подвисало
    // на секунды. На Windows 10 (Desktop Duplication) кадр отдавался сразу, поэтому
    // там проблемы и не было.
    private readonly object _copyLock = new();
    private ID3D11Texture2D? _frameCopy;
    private readonly ManualResetEventSlim _copyReady = new(false);
    private volatile bool _copyRequested;

    public bool TryUseLatestFrame(Action<ID3D11Texture2D> use)
    {
        _copyReady.Reset();
        _copyRequested = true;

        // Полсекунды — с запасом на кадр даже при 2 fps на статичном экране
        if (!_copyReady.Wait(500))
        {
            _copyRequested = false;
            return false;
        }

        lock (_copyLock)
        {
            if (_frameCopy is null) return false;
            use(_frameCopy);
            return true;
        }
    }

    /// <summary>Скопировать кадр в собственную текстуру (вызывается только по запросу).</summary>
    private void CaptureCopy(ID3D11Texture2D source)
    {
        lock (_copyLock)
        {
            var desc = source.Description;
            if (_frameCopy is null || _frameCopy.Description.Width != desc.Width
                                   || _frameCopy.Description.Height != desc.Height)
            {
                _frameCopy?.Dispose();
                _frameCopy = D3DDevice.CreateTexture2D(desc with
                {
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                });
            }
            D3DContext.CopyResource(_frameCopy, source);
            D3DContext.Flush();
        }
        _copyReady.Set();
    }

    /// <summary>Буферов в пуле: на 4K кадр весит ~33 МБ, там пул меньше.</summary>
    private static int PoolSizeFor(int height) => height > 1440 ? 6 : 12;

    /// <summary>Возвращает HMONITOR монитора по индексу (0 = основной по порядку DXGI).</summary>
    private static IntPtr GetMonitorHandle(int index)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var monitors = new List<IntPtr>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                    using (output) monitors.Add(output.Description.Monitor);
        }
        if (monitors.Count == 0) throw new InvalidOperationException("Мониторы не найдены");
        return monitors[Math.Clamp(index, 0, monitors.Count - 1)];
    }

    public void Start(int monitorIndex, int targetFps, bool captureCursor = true)
    {
        lock (_sync)
        {
            StopInternal();

            _minFrameIntervalTicks = targetFps > 0 ? 10_000_000L / targetFps : 0;
            _nextFrameDeadline = 0;

            _item = CaptureInterop.CreateItemForMonitor(GetMonitorHandle(monitorIndex));
            Width = _item.Size.Width;
            Height = _item.Size.Height;

            // FreeThreaded: колбэк приходит в пуле потоков, не трогаем UI-поток.
            // Размер пула критичен для плавности: с 4 буферами система отдавала
            // 57.4 кадра/с вместо 60 (не успевала освобождать), и недостающие слоты
            // приходилось заполнять дубликатами. С 12 — 59.7 к/с; выше прироста нет,
            // поэтому на 4K берём меньше, чтобы не занимать лишнюю видеопамять.
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolSizeFor(Height), _item.Size);
            _framePool.FrameArrived += OnFrameArrived;

            // Право на захват без рамки нужно получить ДО создания сессии
            CaptureAccess.EnsureBorderlessAccess();

            _session = _framePool.CreateCaptureSession(_item);

            // Жёлтая рамка записи. Ошибку НЕ глотаем молча: раньше на части систем
            // свойство отваливалось незаметно и пользователь видел рамку весь сеанс.
            if (CaptureAccess.IsBorderControlSupported)
            {
                try
                {
                    _session.IsBorderRequired = false;
                    if (_session.IsBorderRequired)
                        Log.Warn("Capture", "Система не дала отключить рамку записи (нет права graphicsCaptureWithoutBorder)");
                }
                catch (Exception ex)
                {
                    Log.Warn("Capture", $"Не удалось отключить рамку записи: {ex.Message}");
                }
            }

            try { _session.IsCursorCaptureEnabled = captureCursor; }
            catch (Exception ex) { Log.Warn("Capture", $"Настройка курсора недоступна: {ex.Message}"); }

            _session.StartCapture();

            Log.Info("Capture", $"Захват запущен: монитор #{monitorIndex}, {Width}x{Height}, target {targetFps} fps");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // ВАЖНО: выгребаем ВСЕ накопившиеся кадры, а не один. Под игровой нагрузкой
        // события коалесцируются: один кадр за событие → пул (4 буфера) переполняется,
        // WGC молча дропает — в записи дыры по 100+ мс.
        while (true)
        {
            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null) return;

            // Смена разрешения монитора — пересоздаём пул
            if (frame.ContentSize.Width != Width || frame.ContentSize.Height != Height)
            {
                Width = frame.ContentSize.Width;
                Height = frame.ContentSize.Height;
                sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolSizeFor(Height),
                    new SizeInt32 { Width = Width, Height = Height });
                return;
            }

            long ticks = frame.SystemRelativeTime.Ticks; // QPC-время в 100-нс единицах
            Interlocked.Increment(ref _framesReceived);

            // Ограничение FPS: WGC отдаёт кадры с частотой монитора (например 144/240 Гц),
            // лишние отбрасываем ДО конвертации/кодирования. Отбор по АБСОЛЮТНЫМ дедлайнам:
            // сравнение «с прошлым кадром» даёт биения 2/3 vsync-интервалов и дёрганую запись.
            if (_minFrameIntervalTicks > 0)
            {
                if (_nextFrameDeadline == 0) _nextFrameDeadline = ticks;
                if (ticks < _nextFrameDeadline) continue;
                _nextFrameDeadline += _minFrameIntervalTicks;
                // Большой провал (игра грузилась, alt-tab) — ресинхронизируемся, не «догоняем»
                if (ticks - _nextFrameDeadline > _minFrameIntervalTicks * 4)
                    _nextFrameDeadline = ticks + _minFrameIntervalTicks;
            }

            Interlocked.Increment(ref _framesAccepted);
            using var texture = CaptureInterop.GetTexture(frame.Surface);

            // Скриншот попросил кадр — копируем его себе, пока текстура ещё жива
            if (_copyRequested)
            {
                _copyRequested = false;
                try { CaptureCopy(texture); }
                catch (Exception ex) { Log.Warn("Capture", $"Копия кадра для скриншота: {ex.Message}"); }
            }

            FrameArrived?.Invoke(texture, ticks);
            // texture освобождается на каждой итерации; получатель обязан скопировать её
            // (GPU-copy в свою NV12-текстуру) внутри колбэка.
        }
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
    }

    private void StopInternal()
    {
        _session?.Dispose(); _session = null;
        if (_framePool is not null) { _framePool.FrameArrived -= OnFrameArrived; _framePool.Dispose(); _framePool = null; }
        _item = null;
    }

    public void Dispose()
    {
        Stop();
        lock (_copyLock) { _frameCopy?.Dispose(); _frameCopy = null; }
        _copyReady.Dispose();
        _winrtDevice?.Dispose();
        D3DContext.Dispose();
        D3DDevice.Dispose();
    }
}
