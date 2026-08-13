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
    /// Бюджет видеопамяти под пул. На 1080p даёт ~70 кадров (≈1.2 с запаса на
    /// всплеск), на 4K упирается в нижнюю границу 24 — столько же, сколько было
    /// раньше на всех разрешениях, так что тяжёлые режимы ничего не теряют.
    /// </summary>
    private const long VramBudgetBytes = 220L << 20;

    private readonly ID3D11Device _device;
    private readonly ID3D11Texture2D?[] _slots;
    private int _next;

    /// <summary>Сколько слотов в кольце — по этому числу считается глубина очереди.</summary>
    public int Slots => _slots.Length;

    public EncoderTexturePool(ID3D11Device device, int width, int height)
    {
        _device = device;
        long frameBytes = Math.Max((long)width * height * 3 / 2, 1); // NV12
        _slots = new ID3D11Texture2D?[Math.Clamp(VramBudgetBytes / frameBytes, 24, 96)];
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
