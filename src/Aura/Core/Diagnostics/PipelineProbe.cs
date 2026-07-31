using System.Diagnostics;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Замер времени по стадиям конвейера записи.
///
/// Зачем: по счётчикам кадров видно ЧТО сломалось (переполнилась входная очередь
/// энкодера), но не видно ГДЕ уходит бюджет кадра. При 60 fps на стадию есть 16.7 мс
/// на всё: копия кадра захвата → конвертация в NV12 → копия в пул → ProcessInput
/// в MFT → выемка сжатого кадра. Под 100% загрузкой GPU игрой любая из стадий может
/// начать ждать очередь GPU, и тогда очередь энкодера переполняется, а кадры летят
/// в мусор.
///
/// Стоимость самого замера: два Stopwatch.GetTimestamp (~20 нс) и атомарные
/// сложения на кадр — на фоне 16.7 мс это неизмеримо.
/// </summary>
public static class PipelineProbe
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    public sealed class Stage(string name)
    {
        public string Name { get; } = name;
        private long _ticks, _count, _max;

        public void Add(long startTs, long endTs)
        {
            long d = endTs - startTs;
            if (d < 0) return;
            Interlocked.Add(ref _ticks, d);
            Interlocked.Increment(ref _count);
            // Обновление максимума без блокировки
            long cur;
            while (d > (cur = Interlocked.Read(ref _max)))
                if (Interlocked.CompareExchange(ref _max, d, cur) == cur) break;
        }

        /// <summary>Снимок «среднее/максимум, мс» и сброс для следующего окна.</summary>
        public (double Avg, double Max, long Count) TakeSnapshot()
        {
            long ticks = Interlocked.Exchange(ref _ticks, 0);
            long count = Interlocked.Exchange(ref _count, 0);
            long max = Interlocked.Exchange(ref _max, 0);
            return (count == 0 ? 0 : ticks * TicksToMs / count, max * TicksToMs, count);
        }
    }

    /// <summary>Копия кадра захвата (DDA: CopyResource в свою текстуру).</summary>
    public static readonly Stage CaptureCopy = new("захват-копия");
    /// <summary>Конвертация BGRA → NV12 + масштаб через ID3D11VideoProcessor.</summary>
    public static readonly Stage Convert = new("NV12");
    /// <summary>Копия NV12 в кольцевой пул текстур энкодера + постановка в очередь.</summary>
    public static readonly Stage Submit = new("копия+очередь");
    /// <summary>Подача кадра в MFT (ProcessInput).</summary>
    public static readonly Stage ProcessInput = new("MFT-вход");
    /// <summary>Выемка сжатого кадра из MFT + копия битстрима в RAM.</summary>
    public static readonly Stage DrainOutput = new("MFT-выход");

    private static long _queuePeak;

    /// <summary>Пиковая глубина входной очереди энкодера за окно.</summary>
    public static void ReportQueueDepth(int depth)
    {
        long cur;
        while (depth > (cur = Interlocked.Read(ref _queuePeak)))
            if (Interlocked.CompareExchange(ref _queuePeak, depth, cur) == cur) break;
    }

    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Строка для минутного лога. Сбрасывает счётчики.</summary>
    public static string TakeReport()
    {
        var stages = new[] { CaptureCopy, Convert, Submit, ProcessInput, DrainOutput };
        long peak = Interlocked.Exchange(ref _queuePeak, 0);

        var parts = new List<string>(stages.Length + 1);
        foreach (var s in stages)
        {
            var (avg, max, count) = s.TakeSnapshot();
            if (count == 0) continue;
            parts.Add($"{s.Name} {avg:F1}/{max:F1}");
        }
        if (parts.Count == 0) return "";
        return $"Время стадий (среднее/пик мс): {string.Join(", ", parts)}; пик очереди {peak}";
    }
}
