using System.Runtime.InteropServices;

namespace Aura.Core.Hardware;

/// <summary>
/// Загрузка движков видеокарты, как в диспетчере задач: 3D (игра) и Video Encode
/// (NVENC). Счётчики Windows «GPU Engine», средние за время между замерами.
///
/// ЗАЧЕМ. Время отправки кадра в NVENC растёт не только когда занят сам NVENC: его
/// тормозят и синхронизация Direct3D, и копии текстур, и очередь драйвера. По двум
/// цифрам сразу видно разницу: «3D 99%, Encode 35%» значит видеокарту задушила
/// игра, «3D 80%, Encode 100%» значит не вытягивает сам кодировщик.
///
/// Диспетчер задач показывает для движка сумму по всем процессам, для типа движка
/// самый загруженный из движков этого типа. Так же и здесь. Замер раз в минуту
/// стоит доли миллисекунды; если счётчиков нет, класс просто молчит.
/// </summary>
internal sealed class GpuEngineLoad : IDisposable
{
    private IntPtr _query;
    private IntPtr _counter3D, _counterEncode;
    private bool _primed;

    public GpuEngineLoad()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _counter3D) != 0)
                _counter3D = IntPtr.Zero;
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*engtype_VideoEncode)\Utilization Percentage", IntPtr.Zero, out _counterEncode) != 0)
                _counterEncode = IntPtr.Zero;
            PdhCollectQueryData(_query);   // первый замер: проценты считаются между двумя
            _primed = true;
        }
        catch { _query = IntPtr.Zero; }
    }

    /// <summary>Загрузка с прошлого вызова, проценты; null — счётчики недоступны.</summary>
    public (double Graphics, double Encode)? Sample()
    {
        if (_query == IntPtr.Zero || !_primed) return null;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return null;
            return (Busiest(_counter3D), Busiest(_counterEncode));
        }
        catch { return null; }
    }

    /// <summary>Сумма по процессам для каждого движка, затем самый загруженный движок.</summary>
    private static double Busiest(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return 0;
        uint size = 0, count = 0;
        int status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
        if (status != PdhMoreData || size == 0) return 0;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, buffer) != 0) return 0;
            var perEngine = new Dictionary<string, double>();
            int itemSize = Marshal.SizeOf<PdhFmtCounterValueItemDouble>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtCounterValueItemDouble>(buffer + i * itemSize);
                string name = Marshal.PtrToStringUni(item.Name) ?? "";
                // «pid_1234_luid_0x…_phys_0_eng_3_engtype_3D»: движок — всё после pid
                int luid = name.IndexOf("luid_", StringComparison.Ordinal);
                string engine = luid >= 0 ? name[luid..] : name;
                perEngine[engine] = perEngine.GetValueOrDefault(engine) + item.Value;
            }
            return perEngine.Count == 0 ? 0 : Math.Min(100, perEngine.Values.Max());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItemDouble
    {
        public IntPtr Name;
        public uint CStatus;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
                                                          ref uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
