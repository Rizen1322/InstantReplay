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
    public static void Log(string when)
    {
        try
        {
            long image = 0, mapped = 0, priv = 0;
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
        }
        catch (Exception ex)
        {
            Log2($"Карта памяти недоступна: {ex.Message}");
        }
    }

    private static void Log2(string message) => Logging.Log.Info("Memory", message);
}
