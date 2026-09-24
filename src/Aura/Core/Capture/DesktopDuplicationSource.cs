using Aura.Core.Interop;
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
internal sealed class DesktopDuplicationSource : IScreenCapture
{
    public ID3D11Device D3DDevice => _device ?? throw new InvalidOperationException("Захват не запущен");
    public ID3D11DeviceContext D3DContext => _context ?? throw new InvalidOperationException("Захват не запущен");
    public int Width { get; private set; }
    public int Height { get; private set; }

    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesAccepted => Interlocked.Read(ref _framesAccepted);
    public long InvalidCursorShapes => Interlocked.Read(ref _invalidCursorShapes);
    private long _framesReceived, _framesAccepted;
    private long _invalidCursorShapes;

    public event Action<CapturedSurface>? FrameArrived;

    /// <inheritdoc />
    public event Action<CaptureFailure>? Failed;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _duplication;

    private Thread? _thread;
    private int _monitorIndex;
    private int _targetFps;
    private long _generation;
    private bool _captureCursor;
    private bool _prepared;
    private int _resetCursorOnNextFrame;
    private readonly HashSet<string> _cursorValidationWarnings = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    private long _minFrameIntervalTicks;
    private long _nextFrameDeadline;
    private bool _firstFrameSinceStart;
    private readonly DdaLifecycleMonitor _lifecycle = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        stormThreshold: 3);

    private static long QpcToTicks(long qpc) =>
        (long)(qpc * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    private static TimeSpan LifecycleNow() => TimeSpan.FromSeconds(
        (double)System.Diagnostics.Stopwatch.GetTimestamp() /
        System.Diagnostics.Stopwatch.Frequency);

    public void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation)
    {
        lock (_sync)
        {
            StopInternal();

            _monitorIndex = monitorIndex;
            _targetFps = targetFps;
            _generation = generation;
            _captureCursor = captureCursor;
            _prepared = false;
            _minFrameIntervalTicks = targetFps > 0 ? 10_000_000L / targetFps : 0;
            _nextFrameDeadline = 0;
            _firstFrameSinceStart = true;
            _lifecycle.Reset();
            _resetCursorOnNextFrame = 1;
            _cursorValidationWarnings.Clear();
            Interlocked.Exchange(ref _framesReceived, 0);
            Interlocked.Exchange(ref _framesAccepted, 0);
            Interlocked.Exchange(ref _invalidCursorShapes, 0);

            CreateDeviceAndDuplication();
            _prepared = true;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (!_prepared || _duplication is null)
                throw new InvalidOperationException("DDA не подготовлен");
            if (_run is not null)
                throw new InvalidOperationException("DDA уже запущен");

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

            Log.Info("Capture", $"Захват запущен (Desktop Duplication): монитор #{_monitorIndex}, " +
                                $"{Width}x{Height}, target {_targetFps} fps");
        }
    }

    /// <summary>Создаёт устройство на адаптере нужного монитора и открывает дупликацию.</summary>
    private void CreateDeviceAndDuplication()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // Ищем адаптер+выход по сквозному индексу монитора (тот же порядок, что у WGC-пути)
        IDXGIAdapter1? targetAdapter = null;
        IDXGIOutput? targetOutput = null;
        try
        {
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

            // Публикуем новые COM-объекты в поля только после успешного
            // DuplicateOutput: при ошибке временные ссылки освободятся здесь же.
            IDXGIOutput1? newOutput = null;
            IDXGIOutputDuplication? newDuplication = null;
            try
            {
                newOutput = targetOutput.QueryInterface<IDXGIOutput1>();
                try
                {
                    using IDXGIOutput5 output5 = targetOutput.QueryInterface<IDXGIOutput5>();
                    newDuplication = output5.DuplicateOutput1(
                        _device!, [Format.B8G8R8A8_UNorm]);
                    Log.Info("Capture", "DDA DuplicateOutput1: BGRA8, " +
                        $"режим {newDuplication.Description.ModeDescription.Format}");
                }
                catch (Exception ex) when (DdaDuplicationPolicy.ShouldFallBackToLegacy(
                                               DuplicationHResult(ex)))
                {
                    Log.Info("Capture", "DuplicateOutput1 недоступен — использую DuplicateOutput");
                    newDuplication = newOutput.DuplicateOutput(_device!);
                }

                _output = newOutput;
                newOutput = null; // владение перешло в поле
                _duplication = newDuplication;
                newDuplication = null;
            }
            finally
            {
                newDuplication?.Dispose();
                newOutput?.Dispose();
            }
        }
        finally
        {
            targetOutput?.Dispose();
            targetAdapter?.Dispose();
        }
    }

    /// <summary>
    /// Сколько ждать «затравочный» кадр сразу после создания дупликации.
    ///
    /// Короткий намеренно: если системе нечего отдать, ждать нечего — настоящий
    /// первый кадр заберёт обычный цикл.
    /// </summary>
    private const int PrimeTimeoutMs = 16;

    /// <summary>
    /// Выбросить первый кадр дупликации, если он не несёт ни одного обновления экрана.
    ///
    /// ЗАЧЕМ. Сразу после DuplicateOutput система отдаёт поверхность, в которую ещё
    /// не скопировала ни одного обновления рабочего стола: AcquireNextFrame отвечает
    /// успехом, а AccumulatedFrames равен нулю. Содержимое такой поверхности — это
    /// рабочий стол на момент, который нам неизвестен, и он может быть заметно старше
    /// текущего. Для записи это незаметно (следующий кадр всё перекроет), а для
    /// скриншота это ровно тот симптом, с которым пришёл отчёт: человек снимает
    /// область поверх своего окна, а на снимке оказывается окно, лежащее ПОД ним.
    /// Собственный образец Microsoft (DesktopDuplication) так же пропускает первый
    /// кадр и начинает со второго.
    ///
    /// ПОЧЕМУ ЭТО ВАЖНО НЕ ТОЛЬКО ПРИ СТАРТЕ. Дупликация пересоздаётся на каждой
    /// смене режима экрана, а она происходит ровно тогда, когда человек выходит из
    /// игры в своё окно. После пересоздания <c>_firstFrameSinceStart</c> публикует
    /// первый же кадр без условий — то есть ту самую неинициализированную
    /// поверхность, и она уезжает в буфер как «текущий экран».
    ///
    /// Забранный кадр приходится отпускать в любом случае: вернуть его системе
    /// непрочитанным нельзя. Для записи потеря одного кадра ничего не стоит —
    /// пейсер закрывает дыру дубликатом, — а ждём мы не дольше PrimeTimeoutMs.
    /// </summary>
    private static void DiscardStaleFirstFrame(IDXGIOutputDuplication duplication)
    {
        IDXGIResource? resource = null;
        bool acquired = false;
        try
        {
            SharpGen.Runtime.Result result =
                duplication.AcquireNextFrame(PrimeTimeoutMs, out var info, out resource);
            if (result.Failure) return;                    // таймаут — отдавать было нечего
            acquired = true;

            if (info.AccumulatedFrames == 0)
                Log.Info("Capture", "DDA: первый кадр дупликации без обновлений — пропущен как устаревший");
        }
        catch (Exception ex)
        {
            Log.Info("Capture", $"DDA: затравочный кадр не взят ({ex.Message})");
        }
        finally
        {
            resource?.Dispose();
            // Отпускаем только то, что взяли: ReleaseFrame без кадра — это
            // DXGI_ERROR_INVALID_CALL, а на нём цикл захвата пересоздаёт сессию.
            if (acquired)
                try { duplication.ReleaseFrame(); } catch { }
        }
    }

    /// <summary>Признак «этому потоку ещё работать». Свой на каждый запуск захвата.</summary>
    private sealed class RunToken { public volatile bool Running = true; }
    private RunToken? _run;

    private void CaptureLoop(RunToken token)
    {
        // Свой высокоточный таймер на поток захвата: ожидание слота сетки кадров
        // должно быть точнее системных 15.6 мс, а глобального timeBeginPeriod больше нет.
        using var timer = new Interop.PreciseTimer();
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.Capture, "DDA");
        while (token.Running)
        {
            IDXGIResource? resource = null;
            bool frameHeld = false;
            IDXGIOutputDuplication? dupHeld = null;
            try
            {
                var dup = _duplication;
                if (dup is null)
                {
                    // Предыдущая попытка могла попасть в короткое окно E_ACCESSDENIED
                    // при смене режима/secure desktop. Не остаёмся навсегда с null:
                    // повторяем создание, пока доступ не вернётся или нас не остановят.
                    RecreateDuplication(token);
                    continue;
                }
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
                    if (waitTicks > 5_000) timer.Wait(waitTicks);
                }

                // БЕЗ ожидания внутри системы. AcquireNextFrame с таймаутом ждёт новый
                // кадр рабочего стола, ДЕРЖА замок устройства D3D11 (устройство общее и
                // защищено для многопоточности). На статичном экране это 100 мс из
                // каждых 100: энкодер, пейсер и копии кадров не могли взять устройство,
                // и кодировалось 0-2 кадра в секунду вместо 60 (снято стеками: поток
                // подачи NVENC ждёт замок, поток захвата сидит в AcquireKeyedMutex).
                // Поэтому спрашиваем без ожидания, а ждём сами — уже без замка.
                SharpGen.Runtime.Result result = dup.AcquireNextFrame(0, out var frameInfo, out resource);

                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    // Статичный экран — норма, ровный CFR добивает пейсер дубликатами
                    timer.Wait(20_000); // 2 мс
                    continue;
                }
                if (DdaDuplicationPolicy.ShouldRecreateFrameSession(result.Code))
                {
                    // ACCESS_LOST — штатная смена fullscreen/desktop mode.
                    // INVALID_CALL означает, что предыдущий ReleaseFrame сорвался;
                    // повтор на той же duplication-сессии будет вечным циклом ошибок.
                    string cause = result == Vortice.DXGI.ResultCode.InvalidCall
                        ? "нарушен жизненный цикл кадра"
                        : "смена режима";
                    DdaLifecycleState lifecycle = _lifecycle.RecordInvalidation(LifecycleNow());
                    if (lifecycle == DdaLifecycleState.Storm)
                    {
                        ReportTransitionStorm(token, cause);
                        break;
                    }
                    if (_lifecycle.InvalidationsInWindow == 1)
                        Log.Warn("Capture", $"Дупликация повреждена ({cause}, {DeviceHealth()}) — пересоздаю");
                    RecreateDuplication(token);
                    continue;
                }
                result.CheckError();
                frameHeld = true;

                if (resource is null) continue;
                if (_lifecycle.ObserveUsefulFrame(LifecycleNow()) != DdaLifecycleState.Stable)
                    continue;
                // AccumulatedFrames == 0 — обновился только курсор, картинка та же.
                // Но ПЕРВЫЙ кадр после старта отдаём всегда: на статичном экране
                // (типичная ситуация при скриншоте) система иначе не присылает ни
                // одного кадра, и одноразовый захват отваливался по таймауту.
                // Позицию и форму курсора забираем ДО отбора кадров: система присылает
                // их и в кадрах, где картинка не менялась, а второй раз их не повторит.
                CaptureCursorUpdate cursor = ReadCursorUpdate(dup, frameInfo);
                bool cursorChanged = cursor.HasPosition || cursor.Shape is not null;

                if (!DesktopFramePolicy.ShouldCapture(
                        frameInfo.AccumulatedFrames, _firstFrameSinceStart, cursorChanged))
                    continue;
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

                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                FrameArrived?.Invoke(new CapturedSurface(
                    texture,
                    ticks,
                    _generation,
                    cursor,
                    CaptureSurfaceScope.Monitor,
                    TargetRevision: 0));
            }
            catch (Exception ex)
            {
                if (!token.Running) break;
                // Потеря устройства неисправима на месте: и дупликация, и текстуры, и
                // само устройство мертвы. Раньше цикл продолжал крутиться, писал в лог
                // одну и ту же ошибку и запись не возвращалась до перезапуска приложения.
                if (DeviceLoss.IsDeviceLost(ex))
                {
                    Log.Warn("Capture", $"DDA: потеряно устройство ({ex.Message}) — прошу пересобрать конвейер");
                    Failed?.Invoke(new CaptureFailure(
                        CaptureFailureKind.DeviceLost, ex,
                        "DDA: потеряно GPU-устройство", _generation));
                    break;
                }
                Log.Error("Capture", ex);
                Thread.Sleep(50);
            }
            finally
            {
                resource?.Dispose();
                if (frameHeld && dupHeld is not null)
                    ReleaseFrameOrRecover(token, dupHeld);
            }
        }
    }

    private void ReleaseFrameOrRecover(RunToken token, IDXGIOutputDuplication duplication)
    {
        try
        {
            duplication.ReleaseFrame();
        }
        catch (Exception ex)
        {
            if (!token.Running) return;

            // После неуспешного ReleaseFrame следующий AcquireNextFrame возвращает
            // INVALID_CALL. Эталонный DDA sample Microsoft завершает текущую сессию;
            // здесь делаем то же, но сразу пересоздаём её без заморозки replay.
            DdaLifecycleState lifecycle = _lifecycle.RecordInvalidation(LifecycleNow());
            if (lifecycle == DdaLifecycleState.Storm)
            {
                ReportTransitionStorm(token, "ошибка ReleaseFrame");
                return;
            }
            if (_lifecycle.InvalidationsInWindow == 1)
                Log.Warn("Capture", $"DDA ReleaseFrame завершился ошибкой — пересоздаю дупликацию: {ex.Message}");
            RecreateDuplication(token);
        }
    }

    private void ReportTransitionStorm(RunToken token, string cause)
    {
        token.Running = false;
        var error = new InvalidOperationException(
            $"DDA потеряла {_lifecycle.InvalidationsInWindow} frame-сессии за 2 секунды ({cause})");
        Log.Warn("Capture", $"{error.Message} — прекращаю пересоздания и прошу сменить источник");
        Failed?.Invoke(new CaptureFailure(
            CaptureFailureKind.BackendTransitionStorm,
            error,
            "DDA: цикл потери дупликации при смене fullscreen-режима",
            _generation));
    }

    private CaptureCursorUpdate ReadCursorUpdate(
        IDXGIOutputDuplication duplication,
        in OutduplFrameInfo info)
    {
        bool resetState = Interlocked.Exchange(ref _resetCursorOnNextFrame, 0) != 0;
        if (!_captureCursor)
            return new CaptureCursorUpdate(
                CaptureCursorMode.Separate, false, false, 0, 0, null, resetState);

        bool ddaHasPosition = info.LastMouseUpdateTime != 0;
        var (hasPosition, visible) = DdaCursorVisibility.Merge(
            ddaHasPosition,
            info.PointerPosition.Visible,
            SystemCursorShowing());
        int x = ddaHasPosition ? info.PointerPosition.Position.X : 0;
        int y = ddaHasPosition ? info.PointerPosition.Position.Y : 0;
        DdaCursorShape? cursorShape = null;

        if (info.PointerShapeBufferSize > 0)
        {
            try
            {
                int bufferSize = checked((int)info.PointerShapeBufferSize);
                if (bufferSize > 16 * 1024 * 1024)
                {
                    WarnInvalidCursorShape($"Буфер формы курсора слишком велик: {bufferSize} байт.");
                }
                else
                {
                    byte[] buffer = new byte[bufferSize];
                    unsafe
                    {
                        fixed (byte* pointer = buffer)
                        {
                            duplication.GetFramePointerShape(
                                (uint)buffer.Length,
                                (nint)pointer,
                                out uint required,
                                out OutduplPointerShapeInfo shapeInfo).CheckError();

                            int payloadLength = checked((int)Math.Min(required, (uint)buffer.Length));
                            if (!DdaCursorShape.TryCreate(
                                    (int)shapeInfo.Type,
                                    checked((int)shapeInfo.Width),
                                    checked((int)shapeInfo.Height),
                                    checked((int)shapeInfo.Pitch),
                                    buffer.AsSpan(0, payloadLength),
                                    out cursorShape,
                                    out string reason))
                            {
                                WarnInvalidCursorShape(reason);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WarnInvalidCursorShape($"Форма курсора не прочитана: {ex.Message}");
            }
        }

        return new CaptureCursorUpdate(
            CaptureCursorMode.Separate,
            hasPosition,
            visible,
            x,
            y,
            cursorShape,
            resetState);
    }

    /// <summary>
    /// Показывает ли система курсор прямо сейчас. null — спросить не удалось.
    /// Вызов дешёвый, его и так делают на каждый кадр: см. DdaCursorVisibility.
    /// </summary>
    private static bool? SystemCursorShowing()
    {
        try
        {
            var cursorInfo = new NativeMethods.CURSORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.CURSORINFO>()
            };
            if (!NativeMethods.GetCursorInfo(ref cursorInfo)) return null;
            return (cursorInfo.flags & NativeMethods.CURSOR_SHOWING) != 0;
        }
        catch
        {
            return null;
        }
    }

    private void WarnInvalidCursorShape(string reason)
    {
        Interlocked.Increment(ref _invalidCursorShapes);
        if (_cursorValidationWarnings.Add(reason))
            Log.Warn("Capture", $"DDA: форма курсора пропущена: {reason}");
    }

    /// <summary>
    /// Жив ли ещё GPU-девайс на момент потери дупликации.
    ///
    /// ЗАЧЕМ. DXGI_ERROR_ACCESS_LOST в логе выглядит одинаково в двух совершенно разных
    /// случаях: обычное переключение полноэкранного режима (игра свернулась, сменилось
    /// разрешение) и сброс драйвера видеокарты, от которого у пользователя вылетают
    /// программы. Различает их только GetDeviceRemovedReason: при смене режима девайс
    /// цел и вернёт S_OK, при сбросе драйвера — DXGI_ERROR_DEVICE_REMOVED/RESET/HUNG.
    /// Без этой строки в логе остаётся гадание, виноват ли рекордер или драйвер.
    /// </summary>
    private string DeviceHealth()
    {
        try
        {
            if (_device is null) return "устройство не создано";
            SharpGen.Runtime.Result reason = _device.DeviceRemovedReason;
            return reason.Success
                ? "устройство цело"
                : $"устройство потеряно: 0x{unchecked((uint)reason.Code):X8}";
        }
        catch (Exception ex)
        {
            return $"состояние устройства неизвестно: {ex.Message}";
        }
    }

    private void RecreateDuplication(RunToken token)
    {
        int previousWidth = Width;
        int previousHeight = Height;
        var result = DuplicationRecovery.Run(
            isRunning: () => token.Running,
            resetCurrent: () =>
            {
                _duplication?.Dispose(); _duplication = null;
                _output?.Dispose(); _output = null;
            },
            create: CreateDeviceAndDuplication,
            delay: Thread.Sleep,
            isTemporary: IsTemporaryDuplicationFailure,
            maxTemporaryMilliseconds: 5_000,
            elapsedMilliseconds: () => Environment.TickCount64,
            temporaryFailure: ex =>
                Log.Warn("Capture", $"Не удалось восстановить дупликацию: {ex.Message}"));

        if (result.Status == DuplicationRecoveryStatus.Restored)
        {
            if (Width != previousWidth || Height != previousHeight)
            {
                token.Running = false;
                var formatError = new InvalidOperationException(
                    $"Размер экрана изменился: {previousWidth}x{previousHeight} → {Width}x{Height}");
                Log.Warn("Capture", $"DDA: {formatError.Message} — пересобираю видеоконвейер");
                Failed?.Invoke(new CaptureFailure(
                    CaptureFailureKind.CaptureFormatChanged, formatError,
                    "DDA: сменился режим монитора", _generation));
                return;
            }
            Volatile.Write(ref _resetCursorOnNextFrame, 1);
            _firstFrameSinceStart = true;
            // Только здесь, а не при первом создании: одноразовый скриншот при
            // выключенном буфере создаёт дупликацию заново и на статичном экране живёт
            // ровно одним первым кадром. Выброси мы его — снимок ждал бы движения
            // мыши и падал по таймауту. А вот после смены режима экрана первый кадр
            // и есть та самая устаревшая поверхность (см. DiscardStaleFirstFrame).
            if (_duplication is { } restored) DiscardStaleFirstFrame(restored);
            Log.Info("Capture", "Дупликация восстановлена");
            return;
        }

        if (result.Status is DuplicationRecoveryStatus.Failed or DuplicationRecoveryStatus.TimedOut &&
            result.Error is { } error)
        {
            token.Running = false;
            Log.Warn("Capture", $"Дупликацию нельзя восстановить на текущем GPU-устройстве: {error.Message}");
            var kind = DeviceLoss.IsDeviceLost(error)
                ? CaptureFailureKind.DeviceLost
                : CaptureFailureKind.BackendUnavailable;
            Failed?.Invoke(new CaptureFailure(kind, error,
                result.Status == DuplicationRecoveryStatus.TimedOut
                    ? "DDA: доступ не вернулся за 5 секунд"
                    : "DDA: дупликация недоступна",
                _generation));
        }
    }

    private static bool IsTemporaryDuplicationFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            int code = current is SharpGen.Runtime.SharpGenException sharpGen
                ? sharpGen.ResultCode.Code
                : current.HResult;
            if (DuplicationRecovery.IsTemporaryHResult(code))
                return true;
        }

        return false;
    }

    private static int DuplicationHResult(Exception ex) =>
        ex is SharpGen.Runtime.SharpGenException sharpGen
            ? sharpGen.ResultCode.Code
            : ex.HResult;

    public void Stop()
    {
        lock (_sync) StopInternal();
    }

    private void StopInternal()
    {
        _prepared = false;
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
            _threadStuck = true;   // Dispose не должен трогать устройство и контекст
            return;
        }

        _duplication?.Dispose(); _duplication = null;
        _output?.Dispose(); _output = null;
    }

    /// <summary>Поток захвата не вышел — им ещё пользуются устройство и контекст.</summary>
    private volatile bool _threadStuck;

    public void Dispose()
    {
        Stop();

        // Если поток застрял, он продолжает звать AcquireNextFrame и обработчик кадра
        // ровно на этих объектах. StopInternal их уже пощадил — здесь тоже нельзя,
        // иначе получается ровно тот краш, от которого защищались строчкой выше.
        if (_threadStuck)
        {
            Log.Warn("Capture", "Устройство DDA оставлено сборщику: поток захвата всё ещё жив");
            _context = null;
            _device = null;
            return;
        }

        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
}
