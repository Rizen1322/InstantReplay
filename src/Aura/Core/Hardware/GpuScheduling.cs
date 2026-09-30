using System.Runtime.InteropServices;

namespace Aura.Core.Hardware;

/// <summary>
/// Включено ли в Windows аппаратное планирование GPU (HAGS) хоть на одной видеокарте.
///
/// От этого зависит, можно ли Aura встать в очередь видеокарты впереди игры. OBS
/// ставит себе класс приоритета REALTIME, и под тяжёлой игрой это и держит запись
/// плавной. Но при включённом HAGS у NVIDIA описаны зависания NVENC с REALTIME,
/// когда видеопамять почти полна, поэтому тогда OBS остаётся на HIGH. Aura так же.
///
/// Спрашиваем сам драйвер (D3DKMTQueryAdapterInfo, KMTQAITYPE_WDDM_2_7_CAPS), а
/// не реестр: ключа HwSchMode может не быть вовсе, и тогда решает умолчание.
/// </summary>
internal static class GpuScheduling
{
    public static bool? HagsEnabled()
    {
        IntPtr adapters = IntPtr.Zero;
        try
        {
            var e = new Enum2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return null;
            int size = Marshal.SizeOf<AdapterInfo>();
            adapters = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            e.Adapters = adapters;
            if (D3DKMTEnumAdapters2(ref e) != 0) return null;

            bool any = false;
            IntPtr caps = Marshal.AllocHGlobal(4);
            try
            {
                for (int i = 0; i < e.NumAdapters; i++)
                {
                    var adapter = Marshal.PtrToStructure<AdapterInfo>(adapters + i * size);
                    Marshal.WriteInt32(caps, 0);
                    var query = new Query { Adapter = adapter.Handle, Type = Wddm27Caps, Data = caps, Size = 4 };
                    if (D3DKMTQueryAdapterInfo(ref query) != 0) continue;
                    if ((Marshal.ReadInt32(caps) & 0b10) != 0) any = true;   // HwSchEnabled
                }
            }
            finally { Marshal.FreeHGlobal(caps); }
            return any;
        }
        catch { return null; }
        finally { if (adapters != IntPtr.Zero) Marshal.FreeHGlobal(adapters); }
    }

    private const int Wddm27Caps = 70;   // KMTQAITYPE_WDDM_2_7_CAPS

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo { public uint Handle; public Luid Luid; public uint NumOfSources; public int PrecisePresentRegions; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Enum2 { public uint NumAdapters; public IntPtr Adapters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Query { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref Enum2 e);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref Query q);
}
