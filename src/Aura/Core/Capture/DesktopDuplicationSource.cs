using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

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
    private int _monitorIndex;
    private readonly object _sync = new();

    // Собственная копия кадра: держать захваченный кадр во время конвертации и
    // кодирования нельзя — пока кадр не отпущен, система не отдаёт следующий,
    // рабочий стол подтормаживает и половина слотов сетки теряется.
    private ID3D11Texture2D? _frameCopy;
    private CursorOverlay? _cursor;
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

            // Курсор дупликация отдаёт отдельно от кадра — дорисовываем сами.
            // Замер на 2560x1440@60: 60,3 кадра в секунду и ноль дропов, столько же,
            // сколько без курсора; подача в энкодер 0,6 мс против 0,4 мс.
            _cursor?.Dispose();
            _cursor = null;
            if (captureCursor)
            {
                _cursor = new CursorOverlay(_device!, _context!);
                Log.Info("Capture", "Курсор дорисовывается в кадр");
            }

            // Признак работы СВОЙ у каждого потока, а не общее поле. Иначе брошенный
            // поток (не успевший выйти к моменту перезапуска) оживал бы вместе с новым:
            // два потока на одной дупликации — гарантированный крах.
            var token = new RunToken();
            _run = token;
            _thread = new Thread(() => CaptureLoop(token))
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

            GpuPriority.TryRaise(_device);
        }

        _output = targetOutput.QueryInterface<IDXGIOutput1>();
        targetOutput.Dispose();
        targetAdapter.Dispose();

        _duplication = _output.DuplicateOutput(_device!);
    }

    /// <summary>Признак «этому потоку ещё работать». Свой на каждый запуск захвата.</summary>
    private sealed class RunToken { public volatile bool Running = true; }
    private RunToken? _run;

    private void CaptureLoop(RunToken token)
    {
        while (token.Running)
        {
            IDXGIResource? resource = null;
            bool frameHeld = false;
            IDXGIOutputDuplication? dupHeld = null;
            try
            {
                var dup = _duplication;
                if (dup is null) { Thread.Sleep(50); continue; }
                dupHeld = dup;

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
                    RecreateDuplication(token);
                    continue;
                }
                result.CheckError();
                frameHeld = true;

                if (resource is null) continue;
                // AccumulatedFrames == 0 — обновился только курсор, картинка та же.
                // Но ПЕРВЫЙ кадр после старта отдаём всегда: на статичном экране
                // (типичная ситуация при скриншоте) система иначе не присылает ни
                // одного кадра, и одноразовый захват отваливался по таймауту.
                // Позицию и форму курсора забираем ДО отбора кадров: система присылает
                // их и в кадрах, где картинка не менялась, а второй раз их не повторит.
                _cursor?.Update(dup, frameInfo);

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
                // Освобождаем кадр у ТОЙ дупликации, у которой его взяли: между
                // захватом и освобождением она могла быть пересоздана.
                try { dup.ReleaseFrame(); } catch { }
                frameHeld = false;

                // Курсор дорисовываем ПОСЛЕ возврата кадра системе: пока кадр у нас,
                // дупликация не отдаёт следующий, и любая задержка тут душит захват.
                // Рисуем в свою копию — исходная текстура принадлежит системе.
                if (_cursor is not null) lock (_frameLock) _cursor.Draw(_frameCopy!);

                FrameArrived?.Invoke(_frameCopy!, ticks);
            }
            catch (Exception ex)
            {
                if (!token.Running) break;
                Log.Error("Capture", ex);
                Thread.Sleep(50);
            }
            finally
            {
                resource?.Dispose();
                if (frameHeld && dupHeld is not null) { try { dupHeld.ReleaseFrame(); } catch { } }
            }
        }
    }

    private void RecreateDuplication(RunToken token)
    {
        try
        {
            _duplication?.Dispose(); _duplication = null;
            _output?.Dispose(); _output = null;
            Thread.Sleep(200); // системе нужно время на смену режима
            if (!token.Running) return; // захват уже останавливают — не создаём заново
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
        if (_run is not null) _run.Running = false;
        _run = null;

        var thread = _thread;
        _thread = null;

        // Ждём дольше прежних 1.5 секунды: пересоздание дупликации само по себе
        // занимает 200 мс сна плюс перебор адаптеров и создание устройства, и под
        // нагрузкой это укладывалось не всегда.
        if (thread is not null && !thread.Join(5000))
        {
            // Поток не вышел. Освобождать дупликацию и выход НЕЛЬЗЯ: он прямо сейчас
            // ими пользуется, а обращение к освобождённому COM-объекту роняет процесс
            // мгновенно и без единой строки в логе — ровно так приложение и падало
            // после «Дупликация потеряна». Отпускаем ссылки и оставляем сборщику:
            // утечка нескольких объектов безопаснее краха.
            Log.Warn("Capture", "Поток захвата не завершился за 5 секунд — " +
                                "объекты дупликации оставлены сборщику, чтобы не уронить процесс");
            _duplication = null;
            _output = null;
            lock (_frameLock) _frameCopy = null;
            return;
        }

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
