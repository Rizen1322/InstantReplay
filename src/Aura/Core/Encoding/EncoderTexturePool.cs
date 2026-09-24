using Vortice.Direct3D11;

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
            // Сразу отправить копию видеокарте. Иначе она остаётся в буфере команд
            // контекста, а NVENC (просмотр вперёд) ждёт этот кадр внутри своего
            // вызова, ДЕРЖА замок устройства: отправить буфер уже некому. Пока идут
            // настоящие кадры, буфер попутно отправляет захват, а на статичном
            // экране WGC молчит — и конвейер вставал намертво. Снято нативными
            // стеками: поток выдачи NVENC крутится в драйвере с замком устройства,
            // пейсер ждёт этот замок.
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

    /// <summary>Отпустить ссылку; слот без ссылок снова свободен для копий.</summary>
    public void Release(ID3D11Texture2D? texture)
    {
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
                _slots[i]?.Dispose();
                _slots[i] = null;
            }
            _slotOf.Clear();
            _ledger.Reset();
            _latest?.Dispose();
            _latest = null;
        }
    }
}
