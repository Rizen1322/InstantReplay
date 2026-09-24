using Vortice.Direct3D11;
using Vortice.DXGI;
using Aura.Core.Logging;

namespace Aura.Core.Encoding;

/// <summary>
/// Кольцевой пул текстур-копий на входе энкодера.
///
/// CreateTexture2D на каждый кадр (60/с) — это аллокации видеопамяти в горячем пути,
/// в играх они дают фризы записи. Поэтому копии складываются в заранее посчитанное
/// кольцо слотов и переиспользуются по кругу.
///
/// Каждый слот знает, занят ли он: слот держат очередь, энкодер (до выдачи кадра)
/// и последний поданный кадр, с которого пейсер делает дубликаты. Копия идёт только
/// в свободный слот. Раньше кольцо шло по кругу вслепую, и при всплеске (очередь
/// переполнена, кадры вытесняются, а копии всё равно идут) переписывало текстуру,
/// которую NVENC ещё держал отображённой для просмотра вперёд. Копия на видеокарте
/// ждала NVENC, NVENC ждал новых кадров, а замок устройства D3D держала копия —
/// вставали захват, кодирование и вместе с ними снос конвейера.
/// Нет свободного слота — кадр теряется, конвейер идёт дальше.
///
/// ОБЩИЙ РЕЖИМ (прямой NVENC). Слоты создаются на отдельном устройстве энкодера
/// с keyed mutex и открываются на устройстве захвата — так же устроен NVENC в OBS.
/// Захват копирует кадр в слот под ключом 0 и отдаёт его ключом 1; поток NVENC
/// берёт слот ключом 1, копирует кадр в свою текстуру на своём устройстве и сразу
/// возвращает слот ключом 0. Устройство захвата NVENC не трогает никогда: прежде
/// драйвер внутри вызова NVENC держал замок общего устройства, пока наши потоки
/// захвата и пейсера ждали его же, и конвейер вставал намертво.
/// </summary>
internal sealed class EncoderTexturePool : IDisposable
{
    /// <summary>
    /// Потолок, который пул берёт себе при свободной видеопамяти. На 1080p это
    /// ~70 кадров (≈1.2 с запаса на всплеск), на 4K упирается в нижнюю границу.
    /// </summary>
    private const long MaxBudgetBytes = 220L << 20;

    /// <summary>
    /// Какую долю ВЫДЕЛЕННОГО системой бюджета видеопамяти пул вправе занять.
    ///
    /// ЗАЧЕМ ДОЛЯ, А НЕ КОНСТАНТА. Windows назначает бюджет процессу и ужимает его,
    /// когда видеопамять нужна другим. Замер на тяжёлой игре: в простое бюджет
    /// 7249 МБ, под игрой — 846 МБ, то есть в девять раз меньше. Пул при этом
    /// продолжал требовать свои 220 МБ, драйвер начинал вытеснять поверхности,
    /// и захват с кодированием проваливались одновременно: 8–20 кадров в секунду
    /// вместо 60. Теперь пул ужимается вместе с бюджетом.
    /// </summary>
    private const double BudgetShare = 0.15;

    private readonly ID3D11Device _device;
    private readonly ID3D11Texture2D?[] _slots;
    private readonly SharedSlot?[]? _shared;

    /// <summary>Слот в общем режиме: одна текстура, два устройства, keyed mutex.</summary>
    private sealed class SharedSlot(ID3D11Texture2D encoder, IDXGIKeyedMutex encoderLock,
                                    ID3D11Texture2D capture, IDXGIKeyedMutex captureLock)
    {
        public readonly ID3D11Texture2D Encoder = encoder;
        public readonly IDXGIKeyedMutex EncoderLock = encoderLock;
        public readonly ID3D11Texture2D Capture = capture;
        public readonly IDXGIKeyedMutex CaptureLock = captureLock;
        /// <summary>Захват записал кадр (ключ 1), а энкодер его ещё не забрал.</summary>
        public volatile bool Written;

        public void Dispose()
        {
            CaptureLock.Dispose(); Capture.Dispose();
            EncoderLock.Dispose(); Encoder.Dispose();
        }
    }

    /// <summary>Общий режим: слоты живут на устройстве энкодера (см. описание класса).</summary>
    public bool Shared => _shared is not null;
    private readonly PoolSlotLedger _ledger;
    private readonly Dictionary<nint, int> _slotOf = [];
    private readonly object _sync = new();

    /// <summary>Сколько слотов в кольце — по этому числу считается глубина очереди.</summary>
    public int Slots => _slots.Length;

    /// <summary>Нужна ли текстурам привязка RenderTarget (так регистрирует вход NVENC).</summary>
    private readonly bool _renderTarget;

    /// <param name="minSlots">
    /// Сколько слотов нужно как минимум. NVENC с просмотром вперёд держит у себя
    /// входные кадры до выдачи результата, и кольцо не имеет права переписать их
    /// раньше: слотов должно хватать и на очередь, и на всё, что внутри энкодера.
    /// </param>
    public EncoderTexturePool(ID3D11Device device, int width, int height, bool tenBit = false,
                              int minSlots = 0, bool renderTarget = false)
    {
        _renderTarget = renderTarget;
        _device = device;
        // NV12 это полтора байта на пиксель, P010 — три: та же раскладка, но каждый
        // отсчёт занимает два байта вместо одного. На десяти битах в тот же бюджет
        // видеопамяти помещается вдвое меньше кадров, и считать это надо честно,
        // иначе пул выйдет за отведённую долю бюджета.
        long pixels = (long)width * height;
        long frameBytes = Math.Max(tenBit ? pixels * 3 : pixels * 3 / 2, 1);
        long bySlots = Math.Clamp(BudgetBytes(device) / frameBytes, 24, 96);
        _slots = new ID3D11Texture2D?[Math.Max(bySlots, minSlots)];
        _ledger = new PoolSlotLedger(_slots.Length);
    }

    /// <summary>
    /// Общий режим для прямого NVENC. Все слоты создаются сразу: текстуры на двух
    /// устройствах в горячем пути не заводят.
    /// </summary>
    public EncoderTexturePool(ID3D11Device captureDevice, ID3D11Device encoderDevice, int width, int height,
                              bool tenBit, int slots)
    {
        _device = captureDevice;
        _renderTarget = false;
        _slots = new ID3D11Texture2D?[slots];
        _shared = new SharedSlot?[slots];
        _ledger = new PoolSlotLedger(slots);
        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = tenBit ? Format.P010 : Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
        };
        try
        {
            for (int i = 0; i < slots; i++)
            {
                var encoder = encoderDevice.CreateTexture2D(desc);
                var encoderLock = encoder.QueryInterface<IDXGIKeyedMutex>();
                using var resource = encoder.QueryInterface<IDXGIResource>();
                var capture = captureDevice.OpenSharedResource<ID3D11Texture2D>(resource.SharedHandle);
                var captureLock = capture.QueryInterface<IDXGIKeyedMutex>();
                _shared[i] = new SharedSlot(encoder, encoderLock, capture, captureLock);
                _slots[i] = capture;
                _slotOf[capture.NativePointer] = i;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Сколько слотов сейчас занято — для диагностики.</summary>
    public int BusySlots => _ledger.Busy;

    /// <summary>Сколько байт видеопамяти пул готов занять прямо сейчас.</summary>
    private static long BudgetBytes(ID3D11Device device)
    {
        if (Capture.GpuInfo.Usage(device) is not { } vram || vram.BudgetMb <= 0)
            return MaxBudgetBytes;   // бюджет не читается — ведём себя как раньше

        long share = (long)(vram.BudgetMb * BudgetShare) << 20;
        return Math.Min(MaxBudgetBytes, Math.Max(32L << 20, share));
    }

    /// <summary>
    /// GPU-копия источника в следующий свободный слот. Возвращённая текстура занята
    /// одной ссылкой вызывающего: её отдают очереди или отпускают через
    /// <see cref="Release"/>. null — свободных слотов нет.
    ///
    /// Лок на устройстве: контекст D3D один на конвейер, а копии делают и поток
    /// захвата, и пейсер.
    /// </summary>
    public ID3D11Texture2D? TryCopy(ID3D11Texture2D source, ID3D11DeviceContext context)
    {
        int slot = _ledger.TryTake();
        if (slot < 0) return null;

        if (_shared is not null)
        {
            var shared = _shared[slot]!;
            // Свободный слот энкодер уже вернул ключом 0, так что ждать тут нечего;
            // таймаут — на случай сбоя, чтобы не повесить поток захвата.
            if (AcquireSync(shared.CaptureLock, 0, 20) != 0)
            {
                // Слот свободен по учёту, но стоит на ключе 1 (вытеснение из очереди
                // не смогло вернуть ключ). Возвращаем ключ сами, иначе слот потерян.
                bool repaired = AcquireSync(shared.CaptureLock, 1, 0) == 0;
                if (repaired) shared.CaptureLock.ReleaseSync(0);
                if (!repaired || AcquireSync(shared.CaptureLock, 0, 0) != 0)
                {
                    LogOnce("слот общего пула не отдан энкодером — кадр пропущен");
                    _ledger.Release(slot);
                    return null;
                }
            }
            lock (_device) context.CopyResource(shared.Capture, source);
            shared.CaptureLock.ReleaseSync(1);      // отпускает и отправляет копию видеокарте
            shared.Written = true;
            return shared.Capture;
        }

        var destination = _slots[slot];
        var desc = source.Description;
        if (destination is null || destination.Description.Width != desc.Width
                                || destination.Description.Height != desc.Height)
        {
            desc.BindFlags = _renderTarget ? BindFlags.RenderTarget : BindFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            var created = _device.CreateTexture2D(desc);
            lock (_sync)
            {
                if (destination is not null)
                {
                    _slotOf.Remove(destination.NativePointer);
                    destination.Dispose();
                }
                destination = _slots[slot] = created;
                _slotOf[created.NativePointer] = slot;
            }
        }
        lock (_device)
        {
            context.CopyResource(destination, source);
            // Сразу отправить копию видеокарте: энкодер берёт кадр из другого потока,
            // и копия не должна ждать в буфере команд, пока его отправит кто-то ещё.
            context.Flush();
        }
        return destination;
    }

    private ID3D11Texture2D? _latest;

    /// <summary>
    /// Копия последнего настоящего кадра вне кольца — источник дубликатов.
    ///
    /// ПОЧЕМУ ОТДЕЛЬНО. Раньше дубликат копировался с текстуры, которую только что
    /// отдали в NVENC. NVENC держит вход отображённым до выдачи кадра, а с
    /// просмотром вперёд выдаёт его только через 16+ кадров. Копия С отображённой
    /// текстуры ждала NVENC прямо в очереди видеокарты, NVENC ждал новых кадров, а
    /// новые кадры стояли в той же очереди за этой копией. На статичном экране
    /// (дубликатов много) конвейер вставал намертво через пару минут; это
    /// воспроизведено и снято стеками потоков. Эта текстура в энкодер не уходит
    /// никогда, поэтому копировать с неё можно всегда.
    /// </summary>
    public ID3D11Texture2D? Latest => _latest;

    /// <summary>Запомнить настоящий кадр как источник будущих дубликатов.</summary>
    public void KeepLatest(ID3D11Texture2D source, ID3D11DeviceContext context)
    {
        var desc = source.Description;
        if (_latest is null || _latest.Description.Width != desc.Width
                            || _latest.Description.Height != desc.Height)
        {
            desc.BindFlags = BindFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            _latest?.Dispose();
            _latest = _device.CreateTexture2D(desc);
        }
        lock (_device) context.CopyResource(_latest, source);
    }

    /// <summary>Ещё одна ссылка на занятую текстуру пула.</summary>
    public void AddRef(ID3D11Texture2D texture)
    {
        int slot;
        lock (_sync) if (!_slotOf.TryGetValue(texture.NativePointer, out slot)) return;
        _ledger.AddRef(slot);
    }

    /// <summary>
    /// Общий режим, поток энкодера: взять кадр слота (ключ 1). Возвращает текстуру
    /// слота на устройстве энкодера; null — захват так и не отдал слот.
    /// </summary>
    public ID3D11Texture2D? AcquireForEncoder(ID3D11Texture2D capture, uint timeoutMs)
    {
        if (_shared is null || !TrySlotOf(capture, out int slot)) return null;
        var shared = _shared[slot]!;
        return AcquireSync(shared.EncoderLock, 1, timeoutMs) == 0 ? shared.Encoder : null;
    }

    /// <summary>Общий режим, поток энкодера: кадр скопирован — слот снова свободен (ключ 0).</summary>
    public void ReleaseFromEncoder(ID3D11Texture2D capture)
    {
        if (_shared is null || !TrySlotOf(capture, out int slot)) return;
        var shared = _shared[slot]!;
        shared.Written = false;
        shared.EncoderLock.ReleaseSync(0);
        _ledger.Release(slot);
    }

    /// <summary>
    /// IDXGIKeyedMutex::AcquireSync с настоящим кодом возврата. Обёртка Vortice его
    /// не отдаёт, а WAIT_TIMEOUT (0x102) — код успеха, то есть таймаут прошёл бы
    /// молча, как будто ключ получен. 0 — ключ наш.
    /// </summary>
    private static unsafe int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
    {
        // IUnknown (3) + IDXGIObject (4) + IDXGIDeviceSubObject (1): AcquireSync — восьмой
        void** vtable = *(void***)mutex.NativePointer;
        var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)vtable[8];
        return acquire(mutex.NativePointer, key, milliseconds);
    }

    private bool TrySlotOf(ID3D11Texture2D texture, out int slot)
    {
        lock (_sync) return _slotOf.TryGetValue(texture.NativePointer, out slot);
    }

    private int _loggedOnce;
    private void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _loggedOnce, 1) == 0) Log.Warn("Encoder", message);
    }

    /// <summary>Отпустить ссылку; слот без ссылок снова свободен для копий.</summary>
    public void Release(ID3D11Texture2D? texture)
    {
        if (texture is not null && _shared is not null && TrySlotOf(texture, out int sharedSlot))
        {
            // Кадр вытеснен из очереди, не дойдя до энкодера: слот стоит на ключе 1.
            // Возвращаем ключ 0, иначе следующая копия захвата его не получит.
            var shared = _shared[sharedSlot]!;
            if (shared.Written && AcquireSync(shared.CaptureLock, 1, 20) == 0)
            {
                shared.Written = false;
                shared.CaptureLock.ReleaseSync(0);
            }
            _ledger.Release(sharedSlot);
            return;
        }
        if (texture is null) return;
        int slot;
        lock (_sync) if (!_slotOf.TryGetValue(texture.NativePointer, out slot)) return;
        _ledger.Release(slot);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_shared is not null) _shared[i]?.Dispose();
                else _slots[i]?.Dispose();
                _slots[i] = null;
            }
            _slotOf.Clear();
            _ledger.Reset();
            _latest?.Dispose();
            _latest = null;
        }
    }
}
