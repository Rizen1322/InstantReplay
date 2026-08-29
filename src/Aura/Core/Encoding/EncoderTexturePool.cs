using Vortice.Direct3D11;

namespace Aura.Core.Encoding;

/// <summary>
/// Кольцевой пул текстур-копий на входе энкодера.
///
/// CreateTexture2D на каждый кадр (60/с) — это аллокации видеопамяти в горячем пути,
/// в играх они дают фризы записи. Поэтому копии складываются в заранее посчитанное
/// кольцо слотов и переиспользуются по кругу.
///
/// Слотов заметно больше, чем глубина входной очереди плюс кадры в работе у MFT,
/// иначе кольцо пойдёт по второму кругу и перезапишет текстуры, которые ещё лежат
/// в очереди неотправленными.
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
    private int _next;

    /// <summary>Сколько слотов в кольце — по этому числу считается глубина очереди.</summary>
    public int Slots => _slots.Length;

    public EncoderTexturePool(ID3D11Device device, int width, int height)
    {
        _device = device;
        long frameBytes = Math.Max((long)width * height * 3 / 2, 1); // NV12
        _slots = new ID3D11Texture2D?[Math.Clamp(BudgetBytes(device) / frameBytes, 8, 96)];
    }

    /// <summary>Сколько байт видеопамяти пул готов занять прямо сейчас.</summary>
    private static long BudgetBytes(ID3D11Device device)
    {
        if (Capture.GpuInfo.Usage(device) is not { } vram || vram.BudgetMb <= 0)
            return MaxBudgetBytes;   // бюджет не читается — ведём себя как раньше

        long share = (long)(vram.BudgetMb * BudgetShare) << 20;
        return Math.Min(MaxBudgetBytes, Math.Max(32L << 20, share));
    }

    /// <summary>
    /// GPU-копия источника в следующий слот кольца.
    ///
    /// Лок на устройстве: контекст D3D один на конвейер, а копии делают и поток
    /// захвата, и пейсер.
    /// </summary>
    public ID3D11Texture2D Copy(ID3D11Texture2D source, ID3D11DeviceContext context)
    {
        int slot = _next;
        _next = (_next + 1) % _slots.Length;

        var destination = _slots[slot];
        var desc = source.Description;
        if (destination is null || destination.Description.Width != desc.Width
                                || destination.Description.Height != desc.Height)
        {
            destination?.Dispose();
            desc.BindFlags = BindFlags.None;
            desc.MiscFlags = ResourceOptionFlags.None;
            destination = _slots[slot] = _device.CreateTexture2D(desc);
        }
        lock (_device) context.CopyResource(destination, source);
        return destination;
    }

    public void Dispose()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i]?.Dispose();
            _slots[i] = null;
        }
    }
}
