using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Aura.Core.Logging;
using Aura.Core.Capture.Host;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Capture;

/// <summary>
/// Захват монитора через дочерний процесс <c>Aura.exe --capture-host</c>.
///
/// ЗАЧЕМ. Зависший внутри драйвера нативный вызов DDA в процессе Aura нельзя ни
/// прервать, ни безопасно освободить: поток держит дупликацию до конца процесса.
/// Воркер изолирует этот отказ. Экран Aura видит через собственное processing-
/// устройство, а кадры получает по кольцу из трёх собственных разделяемых
/// BGRA-текстур.
///
/// СИНХРОНИЗАЦИЯ — ID3D11Fence, без IDXGIKeyedMutex:
/// • воркер скопировал кадр в слот → Signal(childFence, v), потом будит Aura;
/// • Aura перед чтением слота ставит Wait(childFence, v) в свою очередь GPU,
///   копирует слот в брокер и после этого Signal(parentFence, r);
/// • воркер перед повторным использованием слота ставит Wait(parentFence, r).
/// Текстуры, NT-хендлы и fence создаются один раз при инициализации сессии.
/// </summary>
internal sealed class RemoteDesktopDuplicationSource : IScreenCapture
{
    private readonly string? _requestedMonitorIdentity;
    private CaptureHostSession? _session;
    private Thread? _frameThread;
    private volatile bool _running;
    private volatile bool _failed;
    private volatile bool _stuck;
    private long _generation;
    private long _framesReceived;
    private long _framesAccepted;
    private EventWaitHandle? _stopEvent;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;

    public RemoteDesktopDuplicationSource(string? monitorIdentity) => _requestedMonitorIdentity = monitorIdentity;

    public ID3D11Device D3DDevice => _device ?? throw new InvalidOperationException("Захват не запущен");
    public ID3D11DeviceContext D3DContext => _context ?? throw new InvalidOperationException("Захват не запущен");
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesAccepted => Interlocked.Read(ref _framesAccepted);
    public long InvalidCursorShapes => 0;
    public bool StopCompleted => _stuck is false && (_session is null || _session.ChildExited);
    public event Action<CapturedSurface>? FrameArrived;
    public event Action<CaptureFailure>? Failed;

    public void Prepare(int monitorIndex, int targetFps, bool captureCursor, long generation)
    {
        if (RemoteCaptureFailureBudget.HasUnretiredWorker)
            throw new RemoteCaptureHostUnavailableException(
                "предыдущий воркер захвата не завершился даже после принудительного завершения");
        if (RemoteCaptureFailureBudget.Exhausted)
            throw new RemoteCaptureHostUnavailableException(
                $"воркер захвата не поднимается после {RemoteCaptureFailureBudget.ConsecutiveFailures} попыток");
        if (targetFps <= 0) targetFps = 60;
        string identity = _requestedMonitorIdentity ?? MonitorLayout.IdentityFor(monitorIndex);
        _generation = generation;
            try
            {
                _session = CaptureHostSession.Start(identity, targetFps, captureCursor, generation);
            }
            catch (Exception ex)
            {
                RemoteCaptureFailureBudget.NoteFailure();
                Log.Error("Capture", $"Изолированный захват не поднялся ({ex.Message}); " +
                                     $"неудач подряд {RemoteCaptureFailureBudget.ConsecutiveFailures}");
                if (RemoteCaptureFailureBudget.Exhausted)
                    throw new RemoteCaptureHostUnavailableException(
                        $"воркер захвата не поднимается после {RemoteCaptureFailureBudget.MaxConsecutiveFailures} попыток");
                throw;
            }
        _device = _session.Device;
        _context = _session.Context;
        Width = _session.Width;
        Height = _session.Height;
        Log.Info("Capture", $"Изолированный DDA готов: {Width}x{Height}, монитор {identity}, " +
                            $"PID воркера {_session.ChildProcessId}, адаптер LUID 0x{_session.AdapterLuid:X16}");
    }

    public void Start()
    {
        if (_session is null) throw new InvalidOperationException("Изолированный DDA не подготовлен");
        if (_running) throw new InvalidOperationException("Изолированный DDA уже запущен");
        _stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _running = true;
        _frameThread = new Thread(FrameLoop)
        {
            IsBackground = true,
            Name = "AuraRemoteDda",
            Priority = ThreadPriority.AboveNormal
        };
        _frameThread.Start();
    }

    private void FrameLoop()
    {
        using var stop = _stopEvent;
        if (stop is null) return;
        var watchdog = new DdaProgressWatchdog(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2.5),
            requiredConfirmations: 2, qpcFrequency: Stopwatch.Frequency);
        var waits = new WaitHandle[] { _session!.FrameEvent, stop };
        bool progressLogged = false;
        while (_running && _session is not null)
        {
            if (_session.ChildExited)
            {
                HandleHostGone();
                return;
            }
            int index = WaitHandle.WaitAny(waits, 100);
            if (!_running) return;
            if (index == 1) return;
            bool processed = false;
            if (index == 0)
            {
                while (_running && TryProcessNextFrame()) processed = true;
            }

            long now = Stopwatch.GetTimestamp();
            CaptureDiagnosticsSnapshot? relayed = _session.ReadRelayedDiagnostics(now);
            if (relayed is { } snapshot)
            {
                long lastProgress = snapshot.LastLoopProgressQpc;
                double stalled = (now - lastProgress) / (double)Stopwatch.Frequency;
                var verdict = watchdog.Observe(new DdaProgressSample(
                    snapshot.LoopSequence, snapshot.LastLoopProgressQpc,
                    snapshot.PublishedQpc, snapshot.Phase, snapshot.Generation), now);
                if (verdict is DdaProgressVerdict.Suspect && !progressLogged)
                {
                    progressLogged = true;
                    Log.Warn("Capture", $"Воркер DDA не продвигается {stalled:F1} с — проверю ещё раз перед перезапуском");
                }
                else if (verdict == DdaProgressVerdict.Progressing)
                {
                    progressLogged = false;
                }
                else if (verdict == DdaProgressVerdict.Confirmed)
                {
                    Fail(CaptureFailureKind.BackendStalled,
                        $"поток Desktop Duplication в воркере не продвигается " +
                        $"({snapshot.Describe(now, Stopwatch.Frequency)})");
                    return;
                }
            }
            else if (_session.HeartbeatAgeSeconds() > 5)
            {
                Fail(CaptureFailureKind.BackendUnavailable, "воркер захвата перестал отвечать");
                return;
            }
        }
    }

    private unsafe bool TryProcessNextFrame()
    {
        if (_session is null) return false;
        int slot = -1;
        long best = long.MaxValue;
        for (int i = 0; i < CaptureHostProtocol.SlotCount; i++)
        {
            long sequence = _session.ReadSlotSequence(i);
            if (sequence > _session.LastProcessedSequence(i) && sequence < best)
            {
                best = sequence;
                slot = i;
            }
        }
        if (slot < 0) return false;

        long published = _session.ReadSlotPublishedFence(slot);
        long timestamp = _session.ReadSlotTimestamp(slot);
        CaptureCursorWire wire = _session.ReadCursorWire(slot);
        DdaCursorShape? shape = _session.ReadCursorShape(slot, wire);
        if (published <= 0) return false;

        // GPU-очередь Aura ждёт, пока воркер закончит копию в слот, и только затем
        // копирует его брокером/энкодером. Порядок команд — в одной очереди.
        _session.WaitChildFence(published);
        var surface = new CapturedSurface(
            _session.SlotTexture(slot),
            timestamp,
            _generation,
            wire.ToUpdate(shape),
            CaptureSurfaceScope.Monitor,
            TargetRevision: 0);
        Interlocked.Increment(ref _framesReceived);
        try
        {
            FrameArrived?.Invoke(surface);
        }
        catch (Exception ex)
        {
            Log.Error("Capture", $"Обработчик удалённого кадра упал: {ex}");
        }
        finally
        {
            Interlocked.Increment(ref _framesAccepted);
            _session.MarkProcessed(slot, best);
            _session.ReleaseSlot(slot);
            RemoteCaptureFailureBudget.NoteHealthy(Interlocked.Read(ref _framesAccepted));
        }
        return true;
    }

    private void HandleHostGone()
    {
        if (_failed) return;
        CaptureFailureKind kind = _session?.ReadFailureKind() ?? CaptureFailureKind.BackendUnavailable;
        string reason = _session?.StateText() ?? "воркер захвата завершился";
        Fail(kind, reason);
    }

    private void Fail(CaptureFailureKind kind, string reason)
    {
        if (_failed) return;
        _failed = true;
        _running = false;
        RemoteCaptureFailureBudget.NoteFailure();
        Log.Error("Capture", $"Изолированный захват отказал ({kind}): {reason}; " +
                             $"неудач подряд {RemoteCaptureFailureBudget.ConsecutiveFailures}");
        try { _session?.RequestStop(); } catch { }
        Failed?.Invoke(new CaptureFailure(kind,
            new InvalidOperationException(reason), reason, _generation));
    }

    public void Stop()
    {
        _running = false;
        try { _stopEvent?.Set(); } catch { }
        var thread = _frameThread;
        _frameThread = null;
        bool threadStopped = thread is null || thread.Join(6000);
        if (!threadStopped)
        {
            Log.Warn("Capture", "Поток удалённого захвата не завершился за 6 секунд");
            _stuck = true;
        }
        bool exited = _session?.StopHost(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3)) ?? true;
        if (!exited)
        {
            Log.Error("Capture", "Воркер изолированного захвата не вышел за отведённое время — " +
                                 "новые воркеры в этом процессе не запускаю");
            _stuck = true;
            RemoteCaptureFailureBudget.NoteUnretiredWorker();
            RemoteCaptureFailureBudget.NoteFailure();
        }
    }

    public void Dispose()
    {
        Stop();
        if (_stuck)
        {
            // Объекты держит живой воркер: освобождать их под ним нельзя, как и DDA.
            Log.Warn("Capture", "Ресурсы изолированного захвата сохранены до выхода: воркер не завершился");
            return;
        }
        _session?.Dispose();
        _session = null;
        _device = null;
        _context = null;
    }
}

/// <summary>
/// Одна сессия с воркером: общая память, события, дочерний процесс, открытые
/// на устройстве Aura разделяемые текстуры и два fence. Создаёт и открывает всё
/// ровно один раз; на кадр — только чтение метаданных и GPU-операции.
/// </summary>
internal sealed class CaptureHostSession : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly CaptureHostControl _control;
    private readonly EventWaitHandle _ready, _attach, _frame, _stop;
    private readonly Process _process;
    private readonly IntPtr _processHandle;
    private readonly ID3D11Texture2D[] _slots = new ID3D11Texture2D[CaptureHostProtocol.SlotCount];
    private readonly long[] _lastProcessed = { -1, -1, -1 };
    private readonly long[] _releasedFence = new long[CaptureHostProtocol.SlotCount];
    private readonly ID3D11Device1 _device1;
    private readonly ID3D11Device5 _device5;
    private ID3D11Fence? _childFence;
    private ID3D11Fence? _parentFence;
    /// <summary>Начальное значение parent-fence — 1: столько родитель пишет в ReleasedFence слотов.</summary>
    private long _releaseValue = 1;
    private bool _disposed;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11DeviceContext4 Context4 { get; }
    public EventWaitHandle FrameEvent => _frame;
    public int ChildProcessId => _process.Id;
    public long AdapterLuid { get; }
    public int Width { get; }
    public int Height { get; }
    public bool ChildExited { get { try { return _process.HasExited; } catch { return true; } } }

    private CaptureHostSession(MemoryMappedFile mapping, CaptureHostControl control,
        EventWaitHandle ready, EventWaitHandle attach, EventWaitHandle frame, EventWaitHandle stop,
        Process process, IntPtr processHandle,
        ID3D11Device device, ID3D11DeviceContext context, ID3D11DeviceContext4 context4,
        ID3D11Device1 device1, ID3D11Device5 device5,
        long adapterLuid, int width, int height)
    {
        _mapping = mapping;
        _control = control;
        _ready = ready;
        _attach = attach;
        _frame = frame;
        _stop = stop;
        _process = process;
        _processHandle = processHandle;
        Device = device;
        Context = context;
        Context4 = context4;
        _device1 = device1;
        _device5 = device5;
        AdapterLuid = adapterLuid;
        Width = width;
        Height = height;
    }

    public static CaptureHostSession Start(string monitorIdentity, int targetFps, bool captureCursor, long generation)
    {
        string session = CaptureHostSessionNames.CreateSessionName();
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        CaptureHostControl? control = null;
        EventWaitHandle? ready = null, attach = null, frame = null, stop = null;
        Process? process = null;
        IntPtr processHandle = IntPtr.Zero;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ID3D11DeviceContext4? context4 = null;
        ID3D11Device1? device1 = null;
        ID3D11Device5? device5 = null;
        var openedHandles = new List<IntPtr>();
        try
        {
            mapping = MemoryMappedFile.CreateNew(CaptureHostSessionNames.Mapping(session), CaptureHostProtocol.MappingBytes);
            view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            control = new CaptureHostControl(view);
            control.WriteInt(CaptureHostProtocol.OffMagic, CaptureHostProtocol.Magic);
            control.WriteInt(CaptureHostProtocol.OffVersion, CaptureHostProtocol.Version);
            control.WriteInt(CaptureHostProtocol.OffState, (int)CaptureHostState.Starting);
            control.WriteUtf16(CaptureHostProtocol.OffMonitorIdentity,
                CaptureHostProtocol.MonitorIdentityBytes, monitorIdentity);
            control.WriteInt(CaptureHostProtocol.OffTargetFps, targetFps);
            control.WriteInt(CaptureHostProtocol.OffCaptureCursor, captureCursor ? 1 : 0);
            control.WriteLong(CaptureHostProtocol.OffGeneration, generation);
            control.WriteInt(CaptureHostProtocol.OffParentPid, Environment.ProcessId);

            ready = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureHostSessionNames.Ready(session));
            attach = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureHostSessionNames.Attach(session));
            frame = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureHostSessionNames.Frame(session));
            stop = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureHostSessionNames.Stop(session));

            string exe = Environment.ProcessPath ??
                         throw new InvalidOperationException("путь к exe не определён");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            psi.ArgumentList.Add("--capture-host");
            psi.ArgumentList.Add(session);
            process = Process.Start(psi) ?? throw new InvalidOperationException("воркер не запустился");
            processHandle = process.Handle;
            Log.Info("Capture", $"Воркер изолированного захвата запущен, PID {process.Id}");

            bool readySignaled = false;
            var readyDeadline = Stopwatch.StartNew();
            while (readyDeadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (ready.WaitOne(100)) { readySignaled = true; break; }
                if (process.HasExited) break;
            }
            if (!readySignaled || process.HasExited)
                throw new RemoteCaptureHostUnavailableException("воркер не инициализировался за 20 секунд");
            if (control.ReadInt(CaptureHostProtocol.OffState) < (int)CaptureHostState.Ready)
                throw new RemoteCaptureHostUnavailableException("воркер не сообщил о готовности");
            int width = control.ReadInt(CaptureHostProtocol.OffWidth);
            int height = control.ReadInt(CaptureHostProtocol.OffHeight);
            long adapterLuid = control.ReadLong(CaptureHostProtocol.OffAdapterLuid);
            if (width <= 0 || height <= 0)
                throw new RemoteCaptureHostUnavailableException("воркер вернул недопустимый размер кадра");

            (device, context) = CreateDeviceForLuid(adapterLuid);
            using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
            device1 = device.QueryInterface<ID3D11Device1>();
            device5 = device.QueryInterface<ID3D11Device5>();
            context4 = context.QueryInterface<ID3D11DeviceContext4>();

            var slots = new ID3D11Texture2D[CaptureHostProtocol.SlotCount];
            for (int i = 0; i < slots.Length; i++)
            {
                long childHandle = control.ReadLong(CaptureHostProtocol.OffRingHandleBase + i * sizeof(long));
                IntPtr duplicate = DuplicateFromChild(processHandle, childHandle);
                openedHandles.Add(duplicate);
                slots[i] = device1.OpenSharedResource1<ID3D11Texture2D>(duplicate);
                if (slots[i].Description.Width != width || slots[i].Description.Height != height)
                    throw new InvalidOperationException("Размер разделяемой текстуры воркера не совпал с заявленным");
            }
            long childFenceHandle = control.ReadLong(CaptureHostProtocol.OffChildFenceHandle);
            IntPtr childFenceDuplicate = DuplicateFromChild(processHandle, childFenceHandle);
            openedHandles.Add(childFenceDuplicate);
            var childFence = device5.OpenSharedFence(childFenceDuplicate);
            // Хендлы нужны были только для открытия: сами ресурсы и fence держат
            // ссылку, поэтому дубликаты сразу закрываем и не копим их на кадр.
            foreach (var handle in openedHandles) CaptureHostNative.CloseHandle(handle);
            openedHandles.Clear();

            var parentFence = device5.CreateFence(1, FenceFlags.Shared);
            IntPtr parentFenceShared = parentFence.CreateSharedHandle(null, null);
            IntPtr parentFenceForChild = DuplicateIntoChild(processHandle, parentFenceShared);
            CaptureHostNative.CloseHandle(parentFenceShared);
            for (int i = 0; i < CaptureHostProtocol.SlotCount; i++)
                control.WriteSlotLong(i, CaptureHostProtocol.SlotOffReleasedFence, 1);
            control.WriteLong(CaptureHostProtocol.OffParentFenceChildHandle, (long)parentFenceForChild);
            control.WriteInt(CaptureHostProtocol.OffState, (int)CaptureHostState.Running);
            attach.Set();

            var result = new CaptureHostSession(mapping, control, ready, attach, frame, stop,
                process, processHandle, device, context, context4, device1, device5, adapterLuid, width, height);
            result._childFence = childFence;
            result._parentFence = parentFence;
            for (int i = 0; i < slots.Length; i++) result._slots[i] = slots[i];
            return result;
        }
        catch
        {
            foreach (var handle in openedHandles) CaptureHostNative.CloseHandle(handle);
            try { if (process is not null && !process.HasExited) process.Kill(); } catch { }
            try { stop?.Set(); } catch { }
            device5?.Dispose();
            device1?.Dispose();
            context4?.Dispose();
            context?.Dispose();
            device?.Dispose();
            stop?.Dispose();
            frame?.Dispose();
            attach?.Dispose();
            ready?.Dispose();
            process?.Dispose();
            control?.Dispose();
            view?.Dispose();
            mapping?.Dispose();
            throw;
        }
    }

    private static IntPtr DuplicateFromChild(IntPtr childProcess, long childHandle) =>
        Duplicate(childProcess, (nint)childHandle, CaptureHostNative.GetCurrentProcess());

    private static IntPtr DuplicateIntoChild(IntPtr childProcess, IntPtr parentHandle) =>
        Duplicate(CaptureHostNative.GetCurrentProcess(), parentHandle, childProcess);

    private static IntPtr Duplicate(IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess)
    {
        if (!CaptureHostNative.DuplicateHandle(sourceProcess, sourceHandle, targetProcess,
                out IntPtr duplicate, 0, false, CaptureHostNative.DuplicateSameAccess))
            throw new InvalidOperationException($"DuplicateHandle не удался: код {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        return duplicate;
    }

    private static (ID3D11Device, ID3D11DeviceContext) CreateDeviceForLuid(long luid)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                if (adapter.Description.Luid != luid) continue;
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                    out ID3D11Device device, out _, out ID3D11DeviceContext context).CheckError();
                return (device, context);
            }
        }
        throw new RemoteCaptureHostUnavailableException($"адаптер воркера LUID 0x{luid:X16} не найден в Aura");
    }

    public ID3D11Texture2D SlotTexture(int slot) => _slots[slot];
    public long LastProcessedSequence(int slot) => _lastProcessed[slot];
    public void MarkProcessed(int slot, long sequence) => _lastProcessed[slot] = sequence;
    public long ReadSlotSequence(int slot) => _control.ReadSlotLong(slot, CaptureHostProtocol.SlotOffSequence);
    public long ReadSlotTimestamp(int slot) => _control.ReadSlotLong(slot, CaptureHostProtocol.SlotOffTimestamp);
    public long ReadSlotPublishedFence(int slot) => _control.ReadSlotLong(slot, CaptureHostProtocol.SlotOffPublishedFence);

    public CaptureCursorWire ReadCursorWire(int slot) => new(
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffCursorMode),
        (CaptureCursorWireFlags)_control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffCursorFlags),
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffCursorX),
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffCursorY),
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffShapeKind),
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffShapeWidth),
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffShapeHeight),
        _control.ReadSlotInt(slot, CaptureHostProtocol.SlotOffShapeLength));

    public DdaCursorShape? ReadCursorShape(int slot, in CaptureCursorWire wire)
    {
        if (wire.ShapeLength <= 0) return null;
        if (wire.ShapeLength > CaptureHostProtocol.SlotShapeCapacity) return null;
        if (wire.ShapeKind is not ((int)DdaCursorShapeKind.Monochrome or (int)DdaCursorShapeKind.Color or (int)DdaCursorShapeKind.MaskedColor))
            return null;
        if (wire.ShapeWidth is < 1 or > DdaCursorShape.MaximumDimension ||
            wire.ShapeHeight is < 1 or > DdaCursorShape.MaximumDimension)
            return null;
        int expected = wire.ShapeWidth * wire.ShapeHeight * 4;
        if (wire.ShapeLength != expected) return null;
        var pixels = new byte[wire.ShapeLength];
        _control.ReadShape(slot, wire.ShapeLength, pixels);
        return new DdaCursorShape((DdaCursorShapeKind)wire.ShapeKind, wire.ShapeWidth, wire.ShapeHeight, pixels);
    }

    public void WaitChildFence(long published) => Context4.Wait(_childFence!, (ulong)published);

    public void ReleaseSlot(int slot)
    {
        long value = ++_releaseValue;
        Context4.Signal(_parentFence!, (ulong)value);
        _control.WriteSlotLong(slot, CaptureHostProtocol.SlotOffReleasedFence, value);
    }

    public CaptureDiagnosticsSnapshot? ReadRelayedDiagnostics(long nowQpc)
    {
        long lastProgress = _control.ReadLong(CaptureHostProtocol.OffLastLoopProgressQpc);
        if (lastProgress <= 0) return null;
        return new CaptureDiagnosticsSnapshot(
            "RemoteDesktopDuplication",
            (DdaCapturePhase)_control.ReadInt(CaptureHostProtocol.OffPhase),
            lastProgress,
            lastProgress,
            _control.ReadLong(CaptureHostProtocol.OffLoopSequence),
            _control.ReadLong(CaptureHostProtocol.OffFramesReceived),
            _control.ReadLong(CaptureHostProtocol.OffFramesAccepted),
            _control.ReadInt(CaptureHostProtocol.OffLastHResult),
            ManagedThreadId: 0,
            NativeThreadId: 0,
            _control.ReadLong(CaptureHostProtocol.OffGeneration),
            _control.ReadUtf16(CaptureHostProtocol.OffMonitorIdentity, CaptureHostProtocol.MonitorIdentityBytes),
            nowQpc);
    }

    public double HeartbeatAgeSeconds()
    {
        long heartbeat = _control.ReadLong(CaptureHostProtocol.OffHeartbeatQpc);
        return heartbeat <= 0 ? double.MaxValue :
            (Stopwatch.GetTimestamp() - heartbeat) / (double)Stopwatch.Frequency;
    }

    public CaptureFailureKind ReadFailureKind()
    {
        int value = _control.ReadInt(CaptureHostProtocol.OffFailureKind);
        return Enum.IsDefined(typeof(CaptureFailureKind), value)
            ? (CaptureFailureKind)value
            : CaptureFailureKind.BackendUnavailable;
    }

    public string StateText()
    {
        var state = (CaptureHostState)_control.ReadInt(CaptureHostProtocol.OffState);
        return state switch
        {
            CaptureHostState.Failed => "воркер захвата сообщил об отказе",
            CaptureHostState.Ended => "воркер захвата завершил работу",
            _ => "воркер захвата неожиданно завершился"
        };
    }

    public void RequestStop()
    {
        try
        {
            // Не затираем Failed: он несёт причину для родителя.
            if ((CaptureHostState)_control.ReadInt(CaptureHostProtocol.OffState) != CaptureHostState.Failed)
                _control.WriteInt(CaptureHostProtocol.OffState, (int)CaptureHostState.Stopping);
        }
        catch { }
        try { _stop.Set(); } catch { }
    }

    /// <summary>Ограниченное ожидание выхода воркера; не вышедший убивается и ожидается ещё раз.</summary>
    public bool StopHost(TimeSpan graceful, TimeSpan afterKill)
    {
        RequestStop();
        bool exited;
        try { exited = _process.WaitForExit((int)graceful.TotalMilliseconds); }
        catch { exited = true; }
        if (!exited)
        {
            Log.Warn("Capture", "Воркер захвата не вышел штатно — завершаю принудительно");
            try { _process.Kill(); } catch { }
            try { exited = _process.WaitForExit((int)afterKill.TotalMilliseconds); }
            catch { exited = true; }
        }
        return exited;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!ChildExited)
        {
            try { _process.Kill(); } catch { }
            try { _process.WaitForExit(2000); } catch { }
        }
        foreach (var slot in _slots) slot?.Dispose();
        _parentFence?.Dispose();
        _childFence?.Dispose();
        _device5.Dispose();
        _device1.Dispose();
        Context4.Dispose();
        Context.Dispose();
        Device.Dispose();
        _stop.Dispose();
        _frame.Dispose();
        _attach.Dispose();
        _ready.Dispose();
        _control.Dispose();
        _mapping.Dispose();
        _process.Dispose();
    }
}
