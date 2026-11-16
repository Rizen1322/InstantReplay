using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Aura.Core.Diagnostics;
using Aura.Core.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Capture.Host;

/// <summary>
/// Дочерний процесс изолированного захвата: <c>Aura.exe --capture-host SESSION</c>.
///
/// Внутри — тот же <see cref="DesktopDuplicationSource"/> (устройство, дупликация,
/// курсор, поворот, локальное пересоздание), но в отдельном процессе. Наружу
/// отдаётся только кольцо из трёх собственных разделяемых BGRA-текстур и
/// метаданные. Синхронизация — ID3D11Fence: воркер сигналит «кадр готов»,
/// Aura сигналит «слот свободен»; IDXGIKeyedMutex не используется.
///
/// Текстуры, NT-хендлы и fence создаются один раз за сессию. На кадр — только
/// GPU-копия и запись метаданных в общую память.
/// </summary>
internal static class CaptureHostServer
{
    public static int Run(string sessionName)
    {
        try { Log.Init(fileSuffix: "-capture-host"); }
        catch { }
        try
        {
            return RunCore(sessionName);
        }
        catch (Exception ex)
        {
            Log.Error("CaptureHost", $"Воркер захвата упал: {ex}");
            return 3;
        }
        finally
        {
            Log.Info("CaptureHost", "Воркер захвата завершён");
            try { Log.Shutdown(); } catch { }
        }
    }

    private static unsafe int RunCore(string sessionName)
    {
        using MemoryMappedFile mapping =
            MemoryMappedFile.OpenExisting(CaptureHostSessionNames.Mapping(sessionName), MemoryMappedFileRights.ReadWrite);
        using MemoryMappedViewAccessor view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
        using var control = new CaptureHostControl(view);

        if (control.ReadInt(CaptureHostProtocol.OffMagic) != CaptureHostProtocol.Magic ||
            control.ReadInt(CaptureHostProtocol.OffVersion) != CaptureHostProtocol.Version ||
            control.ReadInt(CaptureHostProtocol.OffState) != (int)CaptureHostState.Starting)
            return 2;

        string monitorIdentity = control.ReadUtf16(CaptureHostProtocol.OffMonitorIdentity,
                                                  CaptureHostProtocol.MonitorIdentityBytes);
        int targetFps = control.ReadInt(CaptureHostProtocol.OffTargetFps);
        bool captureCursor = control.ReadInt(CaptureHostProtocol.OffCaptureCursor) != 0;
        long generation = control.ReadLong(CaptureHostProtocol.OffGeneration);
        int parentPid = control.ReadInt(CaptureHostProtocol.OffParentPid);
        if (targetFps <= 0) targetFps = 60;

        using EventWaitHandle ready = EventWaitHandle.OpenExisting(CaptureHostSessionNames.Ready(sessionName));
        using EventWaitHandle attach = EventWaitHandle.OpenExisting(CaptureHostSessionNames.Attach(sessionName));
        using EventWaitHandle frameReady = EventWaitHandle.OpenExisting(CaptureHostSessionNames.Frame(sessionName));
        using EventWaitHandle stop = EventWaitHandle.OpenExisting(CaptureHostSessionNames.Stop(sessionName));

        Process parent;
        try { parent = Process.GetProcessById(parentPid); }
        catch (ArgumentException) { return 2; }
        using (parent)
        {
            int monitorIndex = MonitorLayout.Resolve(0, monitorIdentity);
            var source = new DesktopDuplicationSource(monitorIdentity);
            var ring = new CaptureHostRing();
            try
            {
                source.Prepare(monitorIndex, targetFps, captureCursor, generation);
                ring.Create(source.D3DDevice, source.D3DContext, source.Width, source.Height, generation);
                control.WriteInt(CaptureHostProtocol.OffWidth, source.Width);
                control.WriteInt(CaptureHostProtocol.OffHeight, source.Height);
                control.WriteLong(CaptureHostProtocol.OffAdapterLuid, ring.AdapterLuid);
                control.WriteInt(CaptureHostProtocol.OffChildPid, Environment.ProcessId);
                for (int i = 0; i < CaptureHostProtocol.SlotCount; i++)
                    control.WriteLong(CaptureHostProtocol.OffRingHandleBase + i * sizeof(long), ring.TextureHandles[i]);
                control.WriteLong(CaptureHostProtocol.OffChildFenceHandle, ring.ChildFenceHandle);
                control.WriteLong(CaptureHostProtocol.OffHeartbeatQpc, Stopwatch.GetTimestamp());
                control.WriteLong(CaptureHostProtocol.OffPublishedEpoch, 0);
                control.WriteInt(CaptureHostProtocol.OffState, (int)CaptureHostState.Ready);
                ready.Set();
                Log.Info("CaptureHost", $"Кольцо готово: {source.Width}x{source.Height}, монитор {monitorIdentity}, " +
                                        $"адаптер LUID 0x{ring.AdapterLuid:X16}, PID {Environment.ProcessId}");

                // Ждём, пока Aura откроет наши ресурсы и пришлёт хендл своего fence.
                var attachDeadline = Stopwatch.StartNew();
                while (control.ReadInt(CaptureHostProtocol.OffState) != (int)CaptureHostState.Running)
                {
                    if (parent.HasExited) return 0;
                    if (attach.WaitOne(100)) continue;
                    if (attachDeadline.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        Fail(control, CaptureFailureKind.BackendUnavailable, "Aura не подключилась к воркеру");
                        return 3;
                    }
                }

                long parentFenceHandle = control.ReadLong(CaptureHostProtocol.OffParentFenceChildHandle);
                if (parentFenceHandle == 0)
                {
                    Fail(control, CaptureFailureKind.BackendUnavailable, "Aura не передала хендл fence");
                    return 3;
                }
                ring.OpenParentFence((nint)parentFenceHandle);

                var loop = new CaptureHostLoop(control, ring, frameReady, generation);
                source.FrameArrived += loop.OnFrame;
                source.Failed += failure =>
                {
                    Log.Warn("CaptureHost", $"Источник захвата отказал: {failure.Kind}: {failure.Reason}");
                    Fail(control, failure.Kind, failure.Reason);
                    loop.RequestStop();
                };
                source.Start();
                Log.Info("CaptureHost", "Изолированный захват запущен");

                var watchdog = new DdaProgressWatchdog(
                    TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(2.5), requiredConfirmations: 2,
                    qpcFrequency: Stopwatch.Frequency);
                while (true)
                {
                    if (loop.Stopped) break;
                    if (stop.WaitOne(100)) break;
                    if (parent.HasExited) break;
                    if (source.TryGetDiagnostics(out CaptureDiagnosticsSnapshot snapshot))
                    {
                        control.WriteLong(CaptureHostProtocol.OffHeartbeatQpc, Stopwatch.GetTimestamp());
                        control.WriteLong(CaptureHostProtocol.OffLoopSequence, snapshot.LoopSequence);
                        control.WriteLong(CaptureHostProtocol.OffLastLoopProgressQpc, snapshot.LastLoopProgressQpc);
                        control.WriteLong(CaptureHostProtocol.OffFramesReceived, snapshot.FramesReceived);
                        control.WriteLong(CaptureHostProtocol.OffFramesAccepted, snapshot.FramesAccepted);
                        control.WriteInt(CaptureHostProtocol.OffLastHResult, snapshot.LastHResult);
                        control.WriteInt(CaptureHostProtocol.OffPhase, (int)snapshot.Phase);
                        long now = Stopwatch.GetTimestamp();
                        var verdict = watchdog.Observe(
                            new DdaProgressSample(snapshot.LoopSequence, snapshot.LastLoopProgressQpc,
                                snapshot.PublishedQpc, snapshot.Phase, snapshot.Generation), now);
                        if (verdict == DdaProgressVerdict.Confirmed)
                        {
                            string diagnostics = snapshot.Describe(now, Stopwatch.Frequency);
                            Log.Error("CaptureHost", $"Поток DDA не продвигается: {diagnostics}");
                            Fail(control, CaptureFailureKind.BackendStalled,
                                "поток Desktop Duplication не продвигается");
                            break;
                        }
                    }
                }

                // Штатная остановка: поток захвата может не выйти, тогда ограниченное ожидание.
                bool stopped = StopSourceBounded(source);
                control.WriteLong(CaptureHostProtocol.OffDroppedFrames, loop.DroppedFrames);
                if (control.ReadInt(CaptureHostProtocol.OffState) != (int)CaptureHostState.Failed)
                    control.WriteInt(CaptureHostProtocol.OffState, (int)CaptureHostState.Ended);
                Log.Info("CaptureHost", $"Воркер захвата остановлен ({(stopped ? "штатно" : "с таймаутом")}), " +
                                        $"пропущено кадров из-за занятого кольца: {loop.DroppedFrames}");
                return 0;
            }
            finally
            {
                ring.Dispose();
                source.Dispose();
            }
        }
    }

    private static bool StopSourceBounded(DesktopDuplicationSource source)
    {
        try
        {
            var stop = Task.Run(source.Stop);
            bool returned = stop.Wait(TimeSpan.FromSeconds(6));
            // Wait вернулся, даже если Stop вышел по таймауту Join: поток захвата
            // мог остаться в драйвере. «Штатно» — только если он действительно вышел.
            return returned && source.StopCompleted;
        }
        catch
        {
            return false;
        }
    }

    private static void Fail(CaptureHostControl control, CaptureFailureKind kind, string reason)
    {
        try
        {
            control.WriteInt(CaptureHostProtocol.OffFailureKind, (int)kind);
            control.WriteInt(CaptureHostProtocol.OffState, (int)CaptureHostState.Failed);
            Log.Warn("CaptureHost", $"Воркер сообщает об отказе: {kind}: {reason}");
        }
        catch { }
    }
}

/// <summary>Кольцо воркера: три разделяемые текстуры и два общих fence.</summary>
internal sealed unsafe class CaptureHostRing : IDisposable
{
    public ID3D11Texture2D[] Textures { get; } = new ID3D11Texture2D[CaptureHostProtocol.SlotCount];
    public long[] TextureHandles { get; } = new long[CaptureHostProtocol.SlotCount];
    public ID3D11Fence ChildFence { get; private set; } = null!;
    public long ChildFenceHandle { get; private set; }
    public ID3D11Fence ParentFence { get; private set; } = null!;
    public long AdapterLuid { get; private set; }
    public ID3D11DeviceContext4 Context4 { get; private set; } = null!;

    public void Create(ID3D11Device device, ID3D11DeviceContext context, int width, int height, long generation)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"Воркер получил кадр недопустимого размера {width}x{height}");
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        using (IDXGIAdapter adapter = dxgi.GetAdapter())
            AdapterLuid = adapter.Description.Luid;
        using (var device5 = device.QueryInterface<ID3D11Device5>())
        {
            // Начальное значение 0: первое же Signal(1) действительно означает
            // «копия кадра завершена», а не совпадает со стартовым состоянием.
            ChildFence = device5.CreateFence(0, FenceFlags.Shared);
            ChildFenceHandle = ChildFence.CreateSharedHandle(null, null);
        }
        Context4 = context.QueryInterface<ID3D11DeviceContext4>();
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            // NT-хендлы требуют базового флага шаринга в маске, иначе CreateTexture2D
            // отвечает E_INVALIDARG. Комбинация Shared | SharedNTHandle — штатный
            // путь шаринга под ID3D11Fence; keyed mutex не задействуется вовсе.
            MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle
        };
        for (int i = 0; i < CaptureHostProtocol.SlotCount; i++)
        {
            Textures[i] = CreateSharedTexture(device, desc, out long handle);
            TextureHandles[i] = handle;
        }
        Log.Info("CaptureHost", $"Кольцо {width}x{height} создано один раз " +
                                "(NT-shared Shared|SharedNTHandle, без keyed mutex), " +
                                $"генерация {generation}");
    }

    public void OpenParentFence(nint handle)
    {
        using var device5 = ChildFence.Device.QueryInterface<ID3D11Device5>();
        ParentFence = device5.OpenSharedFence(handle);
        // Дубликат принадлежал нашему процессу; fence держит ссылку сам.
        CaptureHostNative.CloseHandle(handle);
    }

    private static ID3D11Texture2D CreateSharedTexture(ID3D11Device device, Texture2DDescription desc, out long handle)
    {
        ID3D11Texture2D texture = device.CreateTexture2D(desc);
        using var resource = texture.QueryInterface<IDXGIResource1>();
        handle = (long)resource.CreateSharedHandle(null,
            Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
        return texture;
    }

    public void Dispose()
    {
        Context4?.Dispose();
        ParentFence?.Dispose();
        ChildFence?.Dispose();
        foreach (var texture in Textures) texture?.Dispose();
        // Хендлы созданы нами; закрытие здесь не обязательно (процесс завершается),
        // но нужно, чтобы сессия не копила объекты ядра при перезапусках внутри процесса.
        foreach (long handle in TextureHandles)
            if (handle != 0) CaptureHostNative.CloseHandle((nint)handle);
        if (ChildFenceHandle != 0) CaptureHostNative.CloseHandle((nint)ChildFenceHandle);
    }
}

/// <summary>Обработчик кадров воркера: выбрать слот, скопировать на GPU, опубликовать.</summary>
internal sealed unsafe class CaptureHostLoop
{
    private const int SlotOffSequence = CaptureHostProtocol.SlotOffSequence;
    private const int SlotOffTimestamp = CaptureHostProtocol.SlotOffTimestamp;
    private const int SlotOffPublishedFence = CaptureHostProtocol.SlotOffPublishedFence;
    private const int SlotOffReleasedFence = CaptureHostProtocol.SlotOffReleasedFence;

    private readonly CaptureHostControl _control;
    private readonly CaptureHostRing _ring;
    private readonly EventWaitHandle _frameReady;
    private readonly long _generation;
    private readonly long[] _lastReleasedSeen = new long[CaptureHostProtocol.SlotCount];
    private long _fenceValue;
    private long _sequence;
    private long _droppedFrames;
    private volatile bool _stopped;

    public CaptureHostLoop(CaptureHostControl control, CaptureHostRing ring,
        EventWaitHandle frameReady, long generation)
    {
        _control = control;
        _ring = ring;
        _frameReady = frameReady;
        _generation = generation;
    }

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public bool Stopped => _stopped;
    public void RequestStop() => _stopped = true;

    public void OnFrame(CapturedSurface surface)
    {
        if (_stopped) return;
        int slot = -1;
        long released = 0;
        for (int i = 0; i < CaptureHostProtocol.SlotCount; i++)
        {
            long value = _control.ReadSlotLong(i, SlotOffReleasedFence);
            if (value > _lastReleasedSeen[i])
            {
                slot = i;
                released = value;
                break;
            }
        }
        if (slot < 0)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }
        _lastReleasedSeen[slot] = released;

        // Метаданные и форма курсора — до публикации fence/события: потребитель
        // читает их только после того, как увидит «кадр готов».
        CaptureCursorUpdate cursor = surface.Cursor;
        DdaCursorShape? shape = cursor.Shape;
        int shapeLength = shape is null ? 0 : Math.Min(shape.Pixels.Length, CaptureHostProtocol.SlotShapeCapacity);
        if (shape is not null && shapeLength == shape.Pixels.Length)
            _control.WriteShape(slot, shape.Pixels);
        else if (shape is not null)
            shapeLength = 0;

        CaptureCursorWire wire = CaptureCursorWire.From(cursor, shapeLength);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffCursorMode, wire.Mode);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffCursorFlags, (int)wire.Flags);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffCursorX, wire.X);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffCursorY, wire.Y);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffShapeKind, wire.ShapeKind);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffShapeWidth, wire.ShapeWidth);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffShapeHeight, wire.ShapeHeight);
        _control.WriteSlotInt(slot, CaptureHostProtocol.SlotOffShapeLength, wire.ShapeLength);
        _control.WriteSlotLong(slot, SlotOffTimestamp, surface.Timestamp);

        // Потребитель не должен читать слот, пока мы в него пишем: ждём на GPU
        // освобождения слота его fence-значением.
        _ring.Context4.Wait(_ring.ParentFence, (ulong)released);
        _ring.Context4.CopyResource(_ring.Textures[slot], surface.Texture);
        long published = Interlocked.Increment(ref _fenceValue);
        _ring.Context4.Signal(_ring.ChildFence, (ulong)published);

        _control.WriteSlotLong(slot, SlotOffPublishedFence, published);
        _control.WriteSlotLong(slot, SlotOffSequence, Interlocked.Increment(ref _sequence));
        Interlocked.MemoryBarrier();
        _frameReady.Set();
    }
}
