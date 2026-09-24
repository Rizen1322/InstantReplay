using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Учёт устройств D3D11 и текстур для диагностики памяти.
///
/// Реестр держит слабые ссылки и ничего не освобождает сам. Объект пропадает из
/// отчёта, когда его освободили (Dispose обнуляет NativePointer) или собрал
/// сборщик. Сверка идёт только при печати отчёта, раз в минуту; создание
/// текстуры добавляет одну запись под замком, горячий путь кадра не трогается.
/// Объём — оценка по размеру и формату: сколько на самом деле отвёл драйвер
/// (выравнивание, сжатие), отсюда не видно.
/// </summary>
public static class GpuResourceLedger
{
    private sealed record Item(WeakReference<ComObject> Target, string Owner, bool IsDevice, long Bytes, string Shape);

    private static readonly object s_sync = new();
    private static readonly List<Item> s_items = [];

    /// <summary>Записать текстуру за владельцем и вернуть её же.</summary>
    public static ID3D11Texture2D Track(ID3D11Texture2D texture, string owner)
    {
        try
        {
            var d = texture.Description;
            long bytes = EstimateBytes(d.Width, d.Height, d.Format) * Math.Max(1, d.ArraySize);
            Add(new Item(new WeakReference<ComObject>(texture), owner, false, bytes,
                         $"{d.Width}x{d.Height} {d.Format}"));
        }
        catch { }
        return texture;
    }

    /// <summary>Записать устройство D3D11 за владельцем.</summary>
    public static ID3D11Device Track(ID3D11Device device, string owner)
    {
        Add(new Item(new WeakReference<ComObject>(device), owner, true, 0, ""));
        return device;
    }

    private static void Add(Item item)
    {
        lock (s_sync) s_items.Add(item);
    }

    /// <summary>Приблизительный объём текстуры одного слоя без мип-уровней.</summary>
    public static long EstimateBytes(uint width, uint height, Format format)
    {
        long pixels = (long)width * height;
        return format switch
        {
            Format.NV12 => pixels * 3 / 2,
            Format.P010 or Format.P016 => pixels * 3,
            Format.R8_UNorm or Format.R8_UInt or Format.A8_UNorm => pixels,
            Format.R8G8_UNorm or Format.R16_UNorm or Format.R16_Float => pixels * 2,
            Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm => pixels * 8,
            Format.R32G32B32A32_Float => pixels * 16,
            _ => pixels * 4,
        };
    }

    /// <summary>
    /// «Устройств 3; текстур 41 ≈ 612 МБ: пул энкодера 24×2560x1440 P010 = 265 МБ, …».
    /// Живыми считаются объекты, у которых ещё есть нативный указатель.
    /// </summary>
    public static string Summary()
    {
        var alive = new List<Item>();
        lock (s_sync)
        {
            s_items.RemoveAll(i => !i.Target.TryGetTarget(out var obj) || obj.NativePointer == IntPtr.Zero);
            alive.AddRange(s_items);
        }
        int devices = alive.Count(i => i.IsDevice);
        string deviceOwners = string.Join(", ", alive.Where(i => i.IsDevice)
            .GroupBy(i => i.Owner).Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key));
        var textures = alive.Where(i => !i.IsDevice).ToList();
        long total = textures.Sum(i => i.Bytes);
        string groups = string.Join("; ", textures
            .GroupBy(i => (i.Owner, i.Shape))
            .OrderByDescending(g => g.Sum(i => i.Bytes))
            .Select(g => $"{g.Key.Owner} {g.Count()}×{g.Key.Shape} = {Mb(g.Sum(i => i.Bytes))}"));
        return $"устройств D3D11 {devices} ({deviceOwners}); текстур {textures.Count} ≈ {Mb(total)}" +
               (groups.Length > 0 ? $": {groups}" : "");
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024):F0} МБ";
}
