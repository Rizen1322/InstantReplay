using Aura.Core.Interop;
using Aura.Core.Logging;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Карта адресного пространства процесса: сколько памяти закоммичено и КАКОГО ТИПА.
///
/// Зачем: после сохранения клипа частная память процесса вырастает на объём клипа
/// и не возвращается, при том что управляемая куча отдаёт всё до единиц мегабайт.
/// Спор «это фрагментация кучи Windows или внутренние очереди Media Foundation»
/// цифрами Private Bytes не решается — они не различают источник.
///
/// Обход VirtualQuery различает три типа областей:
/// • образы — загруженные библиотеки (код и данные DLL);
/// • отображения — файлы и разделяемая память;
/// • частные — кучи, VirtualAlloc, стеки; сюда же попадает управляемая куча,
///   поэтому из частных вычитается коммит сборщика.
///
/// Плюс крупнейшие области с их адресами: по ним видно, одна ли это гигантская
/// выделенная область (так ведёт себя драйвер или собственный аллокатор) или
/// множество сегментов кучи (тогда это действительно фрагментация).
/// </summary>
public static class MemoryMap
{
    /// <summary>
    /// Сумма закоммиченных ЧАСТНЫХ областей — то же число, что в «Карте памяти»,
    /// но без разбора и без записи в лог.
    ///
    /// Нужна для поэтапного замера внутри сохранения: PrivateMemorySize64 сюда не
    /// годится, он включает управляемую кучу, а её коммит меняется сам по себе.
    /// Обход занимает единицы миллисекунд, а зовут его считаные разы за сохранение.
    /// </summary>
    public static long PrivateCommittedBytes()
    {
        try
        {
            long priv = 0;
            IntPtr address = IntPtr.Zero;
            int guard = 0;
            while (guard++ < 200_000)
            {
                nuint written = NativeMethods.VirtualQuery(
                    address, out var info, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());
                if (written == 0) break;

                long size = (long)info.RegionSize;
                if (size <= 0) break;
                if (info.State == NativeMethods.MemCommit && info.Type == NativeMethods.MemPrivate) priv += size;

                long next = (long)address + size;
                if (next <= (long)address) break;
                address = (IntPtr)next;
            }
            return priv;
        }
        catch { return 0; }
    }

    /// <summary>Закоммиченная память по типам: образы DLL, отображения, частные и число частных областей.</summary>
    public static (long Image, long Mapped, long Private, int PrivateRegions) Breakdown()
    {
        long image = 0, mapped = 0, priv = 0;
        int count = 0;
        try
        {
            IntPtr address = IntPtr.Zero;
            int guard = 0;
            while (guard++ < 200_000)
            {
                nuint written = NativeMethods.VirtualQuery(
                    address, out var info, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());
                if (written == 0) break;
                long size = (long)info.RegionSize;
                if (size <= 0) break;
                if (info.State == NativeMethods.MemCommit)
                {
                    if (info.Type == NativeMethods.MemImage) image += size;
                    else if (info.Type == NativeMethods.MemMapped) mapped += size;
                    else if (info.Type == NativeMethods.MemPrivate) { priv += size; count++; }
                }
                long next = (long)address + size;
                if (next <= (long)address) break;
                address = (IntPtr)next;
            }
        }
        catch { }
        return (image, mapped, priv, count);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    /// <summary>Загружен ли в процесс модуль (например, libvlc.dll).</summary>
    public static bool IsModuleLoaded(string name)
    {
        try { return GetModuleHandleW(name) != IntPtr.Zero; }
        catch { return false; }
    }

    public static void Log(string when)
    {
        try
        {
            long image = 0, mapped = 0, priv = 0;
            long privSmall = 0, privMedium = 0, privLarge = 0;
            int privCount = 0;
            var regions = new List<(long Size, IntPtr Base, uint Type)>();

            IntPtr address = IntPtr.Zero;
            int guard = 0;
            while (guard++ < 200_000)
            {
                nuint written = NativeMethods.VirtualQuery(
                    address, out var info, (nuint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MemoryBasicInformation>());
                if (written == 0) break;

                long size = (long)info.RegionSize;
                if (size <= 0) break;

                if (info.State == NativeMethods.MemCommit)
                {
                    switch (info.Type)
                    {
                        case NativeMethods.MemImage: image += size; break;
                        case NativeMethods.MemMapped: mapped += size; break;
                        case NativeMethods.MemPrivate:
                            priv += size;
                            privCount++;
                            // Раскладка по размеру: по ней видно, ЧТО именно растёт —
                            // одна большая область (собственный аллокатор, драйвер)
                            // или россыпь мелких (обычная куча, то есть живые выделения).
                            if (size < 1L << 20) privSmall += size;
                            else if (size < 16L << 20) privMedium += size;
                            else privLarge += size;
                            if (size >= 16L * 1024 * 1024) regions.Add((size, info.AllocationBase, info.Type));
                            break;
                    }
                }

                long next = (long)address + size;
                if (next <= (long)address) break;
                address = (IntPtr)next;
            }

            long gc = GC.GetGCMemoryInfo().TotalCommittedBytes;
            static string Mb(long b) => $"{b / (1024 * 1024)} МБ";

            // Крупные частные области объединяем по базе выделения: одна база — это
            // один вызов VirtualAlloc, то есть один сегмент кучи или один буфер.
            string top = string.Join(", ", regions
                .GroupBy(r => r.Base)
                .Select(g => g.Sum(x => x.Size))
                .OrderByDescending(x => x)
                .Take(6)
                .Select(Mb));

            Log2($"Карта памяти ({when}): образы {Mb(image)}, отображения {Mb(mapped)}, " +
                 $"частные {Mb(priv)} (из них сборщик {Mb(gc)}, нативные ~{Mb(Math.Max(0, priv - gc))}); " +
                 $"крупные частные области: {(top.Length > 0 ? top : "нет")}");

            // Число областей и их раскладка по размеру. Если между двумя замерами
            // растёт ЧИСЛО областей — это живые выделения, то есть настоящая утечка.
            // Если число стоит, а объём растёт — куча просто удерживает достигнутый
            // пик и новой памяти у системы не просит.
            Log2($"Частные области ({when}): всего {privCount} шт; " +
                 $"до 1 МБ — {Mb(privSmall)}, 1–16 МБ — {Mb(privMedium)}, от 16 МБ — {Mb(privLarge)}");
        }
        catch (Exception ex)
        {
            Log2($"Карта памяти недоступна: {ex.Message}");
        }
    }

    private static void Log2(string message) => Logging.Log.Info("Memory", message);
}
