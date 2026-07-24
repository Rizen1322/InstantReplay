using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Capture;

/// <summary>
/// Захват экрана через DXGI Desktop Duplication (Windows 8+).
///
/// Зачем при наличии WGC: на Windows 10 система ВСЕГДА рисует жёлтую рамку вокруг
/// захватываемого экрана, а свойства для её отключения там нет (появилось только
/// в Windows 11). Desktop Duplication рамку не рисует вовсе.
///
/// Особенности, учтённые здесь:
/// • Устройство D3D11 создаётся на ТОМ адаптере, которому принадлежит монитор —
///   иначе DuplicateOutput падает с E_INVALIDARG на системах с двумя GPU.
/// • При смене режима/переключении на fullscreen-игру или UAC система рвёт
///   дупликацию (ACCESS_LOST) — пересоздаём её на лету, запись не прерывается.
/// • Аппаратный курсор в кадр не входит (DDA отдаёт его отдельно). Курсор,
///   нарисованный самой игрой, в кадре есть — для записи геймплея это то, что нужно.
/// </summary>
public sealed class DesktopDuplicationSource : IScreenCapture
{
    public ID3D11Device D3DDevice => _device ?? throw new InvalidOperationException("Захват не запущен");
    public ID3D11DeviceContext D3DContext => _context ?? throw new InvalidOperationException("Захват не запущен");
    public int Width { get; private set; }
    public int Height { get; private set; }

    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesAccepted => Interlocked.Read(ref _framesAccepted);
    private long _framesReceived, _framesAccepted;

    public event Action<ID3D11Texture2D, long>? FrameArrived;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;

    private Thread? _thread;
    private volatile bool _running;
    private int _monitorIndex;
    private readonly object _sync = new();

    // Собственная копия кадра: держать захваченный кадр во время конвертации и
    // кодирования нельзя — пока кадр не отпущен, система не отдаёт следующий,
    // рабочий стол подтормаживает и половина слотов сетки теряется.
    private ID3D11Texture2D? _frameCopy;
    // Защищает _frameCopy от пересоздания/освобождения, пока его читает скриншот.
    // В горячем пути лок незанят (скриншот — редкость), стоит десятки наносекунд.
    private readonly object _frameLock = new();

    /// <summary>
    /// Последний захваченный кадр — для скриншота при включённом буфере.
    /// Вторую дупликацию того же монитора DXGI не даёт (E_INVALIDARG), поэтому
    /// скриншот переиспользует уже захваченный кадр. Заодно работает и на
    /// статичном экране: здесь лежит последний реальный кадр.
    /// </summary>
    public bool TryUseLatestFrame(Action<ID3D11Texture2D> use)
    {
        lock (_frameLock)
        {
            if (_frameCopy is null) return false;
            use(_frameCopy);
            return true;
        }
    }

    private long _minFrameIntervalTicks;
    private long _nextFrameDeadline;
    private bool _firstFrameSinceStart;

    private static long QpcToTicks(long qpc) =>
        (long)(qpc * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public void Start(int monitorIndex, int targetFps, bool captureCursor = true)
    {
        lock (_sync)
        {
            StopInternal();

            _monitorIndex = monitorIndex;
            _minFrameIntervalTicks = targetFps > 0 ? 10_000_000L / targetFps : 0;
            _nextFrameDeadline = 0;
            _firstFrameSinceStart = true;

            CreateDeviceAndDuplication();

            if (captureCursor)
                Log.Info("Capture", "Аппаратный курсор в записи не отображается (особенность Windows 10)");

            _running = true;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "DesktopDuplication",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();

            Log.Info("Capture", $"Захват запущен (Desktop Duplication): монитор #{monitorIndex}, " +
                                $"{Width}x{Height}, target {targetFps} fps");
        }
    }

    /// <summary>Создаёт устройство на адаптере нужного монитора и открывает дупликацию.</summary>
    private void CreateDeviceAndDuplication()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // Ищем адаптер+выход по сквозному индексу монитора (тот же порядок, что у WGC-пути)
        IDXGIAdapter1? targetAdapter = null;
        IDXGIOutput? targetOutput = null;
        int index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            bool used = false;
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
            {
                if (index++ == _monitorIndex) { targetAdapter = adapter; targetOutput = output; used = true; break; }
                output.Dispose();
            }
            if (used) break;
            adapter.Dispose();
        }

        // Индекс вне диапазона — берём первый доступный выход
        if (targetOutput is null)
        {
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
            {
                if (adapter.EnumOutputs(0, out IDXGIOutput output).Success)
                { targetAdapter = adapter; targetOutput = output; break; }
                adapter.Dispose();
            }
        }
        if (targetAdapter is null || targetOutput is null)
            throw new InvalidOperationException("Мониторы не найдены");

        var desc = targetOutput.Description;
        Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

        if (_device is null)
        {
            var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            D3D11.D3D11CreateDevice(targetAdapter, DriverType.Unknown, flags, levels,
                out ID3D11Device device, out _, out ID3D11DeviceContext context).CheckError();
            _device = device;
            _context = context;

            // Кодировщик и захват работают в разных потоках
            using var mt = _device.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);
        }

        _output = targetOutput.QueryInterface<IDXGIOutput1>();
        targetOutput.Dispose();
        targetAdapter.Dispose();

        _duplication = _output.DuplicateOutput(_device!);
    }

    private void CaptureLoop()
    {
        while (_running)
        {
            IDXGIResource? resource = null;
            bool frameHeld = false;
            try
            {
                var dup = _duplication;
                if (dup is null) { Thread.Sleep(50); continue; }

                // Ритм захвата: ждём слот сетки ДО обращения к системе.
                // Иначе на 240-Гц мониторе мы забираем ~200 кадров/с вместо 60 —
                // каждый захват синхронизируется с GPU и отбирает ресурсы у энкодера
                // (в тестах: 1000 дропнутых кадров в минуту против нуля).
                // Desktop Duplication накапливает обновления и отдаёт самый свежий кадр,
                // так что ожидание ничего не теряет.
                if (_minFrameIntervalTicks > 0 && _nextFrameDeadline > 0)
                {
                    long waitTicks = _nextFrameDeadline - QpcToTicks(System.Diagnostics.Stopwatch.GetTimestamp());
                    if (waitTicks > 15_000) Thread.Sleep((int)(waitTicks / 10_000));
                }

                // 100 мс: на статичном экране система просто не отдаёт кадры — это норма,
                // ровный CFR добивает пейсер энкодера дубликатами.
                SharpGen.Runtime.Result result = dup.AcquireNextFrame(100, out var frameInfo, out resource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    // Смена режима/полноэкранное приложение/UAC — пересоздаём дупликацию
                    Log.Warn("Capture", "Дупликация потеряна (смена режима?) — восстанавливаю");
                    RecreateDuplication();
                    continue;
                }
                result.CheckError();
                frameHeld = true;

                if (resource is null) continue;
                // AccumulatedFrames == 0 — обновился только курсор, картинка та же.
                // Но ПЕРВЫЙ кадр после старта отдаём всегда: на статичном экране
                // (типичная ситуация при скриншоте) система иначе не присылает ни
                // одного кадра, и одноразовый захват отваливался по таймауту.
                if (frameInfo.AccumulatedFrames == 0 && !_firstFrameSinceStart) continue;
                _firstFrameSinceStart = false;

                Interlocked.Increment(ref _framesReceived);

                long ticks = frameInfo.LastPresentTime > 0
                    ? QpcToTicks(frameInfo.LastPresentTime)
                    : QpcToTicks(System.Diagnostics.Stopwatch.GetTimestamp());

                // Отбор по абсолютным дедлайнам — как в WGC-пути (без биений)
                if (_minFrameIntervalTicks > 0)
                {
                    if (_nextFrameDeadline == 0) _nextFrameDeadline = ticks;
                    if (ticks < _nextFrameDeadline) continue;
                    _nextFrameDeadline += _minFrameIntervalTicks;
                    if (ticks - _nextFrameDeadline > _minFrameIntervalTicks * 4)
                        _nextFrameDeadline = ticks + _minFrameIntervalTicks;
                }

                Interlocked.Increment(ref _framesAccepted);

                // Быстрая GPU-копия и немедленный ReleaseFrame — только после этого
                // отдаём кадр в конвейер (конвертация NV12 + кодирование).
                long copyStart = Diagnostics.PipelineProbe.Now();
                using (var texture = resource.QueryInterface<ID3D11Texture2D>())
                lock (_frameLock)
                {
                    var srcDesc = texture.Description;
                    if (_frameCopy is null ||
                        _frameCopy.Description.Width != srcDesc.Width ||
                        _frameCopy.Description.Height != srcDesc.Height)
                    {
                        _frameCopy?.Dispose();
                        srcDesc.MiscFlags = ResourceOptionFlags.None; // снимаем «расшаренность»
                        srcDesc.CPUAccessFlags = CpuAccessFlags.None;
                        srcDesc.Usage = ResourceUsage.Default;
                        srcDesc.BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget;
                        _frameCopy = _device!.CreateTexture2D(srcDesc);
                    }
                    _context!.CopyResource(_frameCopy, texture);
                }
                Diagnostics.PipelineProbe.CaptureCopy.Add(copyStart, Diagnostics.PipelineProbe.Now());

                resource.Dispose(); resource = null;
                try { _duplication?.ReleaseFrame(); } catch { }
                frameHeld = false;

                FrameArrived?.Invoke(_frameCopy!, ticks);
            }
            catch (Exception ex)
            {
                if (!_running) break;
                Log.Error("Capture", ex);
                Thread.Sleep(50);
            }
            finally
            {
                resource?.Dispose();
                if (frameHeld) { try { _duplication?.ReleaseFrame(); } catch { } }
            }
        }
    }

    private void RecreateDuplication()
    {
        try
        {
            _duplication?.Dispose(); _duplication = null;
            _output?.Dispose(); _output = null;
            Thread.Sleep(200); // системе нужно время на смену режима
            CreateDeviceAndDuplication();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture", $"Не удалось восстановить дупликацию: {ex.Message}");
            Thread.Sleep(500);
        }
    }

    public void Stop()
    {
        lock (_sync) StopInternal();
    }

    private void StopInternal()
    {
        _running = false;
        _thread?.Join(1500);
        _thread = null;
        _duplication?.Dispose(); _duplication = null;
        _output?.Dispose(); _output = null;
        lock (_frameLock) { _frameCopy?.Dispose(); _frameCopy = null; }
    }

    public void Dispose()
    {
        Stop();
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
}
