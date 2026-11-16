using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// Запись о выходе монитора, который удерживает незавершившаяся сессия DDA.
/// </summary>
internal sealed record HeldCaptureOutput(
    string MonitorDeviceId,
    long Generation,
    string Reason,
    DateTimeOffset SinceUtc,
    string? DiagnosticContext)
{
    public string Describe() =>
        $"выход {MonitorDeviceId} удерживается сессией генерации {Generation} " +
        $"с {SinceUtc.ToLocalTime():HH:mm:ss} ({Reason})";
}

/// <summary>
/// Попытка открыть Desktop Duplication выхода, который в этом процессе уже
/// удерживает незавершившаяся сессия. Отдельное исключение, а не E_INVALIDARG:
/// цикл восстановления обязан отличить доказанную блокировку от прочих причин
/// отказа и прекратить слепые старты, сохранив накопленный повтор.
/// </summary>
internal sealed class CaptureOutputHeldException : InvalidOperationException
{
    public HeldCaptureOutput Output { get; }

    public CaptureOutputHeldException(in HeldCaptureOutput output) : base(BuildMessage(output)) =>
        Output = output;

    internal static string BuildMessage(in HeldCaptureOutput output) =>
        $"Захват экрана не восстановился после зависания: {output.Describe()}. " +
        "Накопленный повтор можно сохранить. Для продолжения перезапустите Aura.";
}

/// <summary>
/// Реестр выходов, которые в ЭТОМ процессе удерживает незавершившийся поток
/// Desktop Duplication.
///
/// ЗАЧЕМ. Вторую дупликацию одного выхода в одном процессе открыть нельзя,
/// поэтому после зависания каждая новая попытка тратит нативный вызов и падает
/// с E_INVALIDARG — в логе друга так набралось 131 штук. Пометка ставится только
/// при незавершившейся остановке DDA (или подтверждённом зависании её потока),
/// а не при любом WAIT_TIMEOUT или отсутствии картинки.
///
/// Пометка НЕ снимается «на всякий случай»: дупликацию мог освободить только
/// сам зависший поток, и без доказанного освобождения снимать её нельзя. Живёт
/// до выхода процесса — владелец Aura честно просит перезапуск.
/// </summary>
internal static class HeldCaptureOutputs
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, HeldCaptureOutput> Held =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Пометить выход занятым. false — уже помечен (первая, настоящая причина
    /// важнее повторной) или у выхода нет устойчивой идентичности.
    /// </summary>
    public static bool TryHold(
        string? monitorDeviceId,
        long generation,
        string reason,
        string? diagnosticContext = null)
    {
        if (string.IsNullOrWhiteSpace(monitorDeviceId)) return false;

        lock (Sync)
        {
            if (Held.TryGetValue(monitorDeviceId, out HeldCaptureOutput? current))
            {
                Log.Warn("Capture",
                    $"Выход {monitorDeviceId} уже помечен как удерживаемый (генерация {current.Generation}), " +
                    $"повторная пометка генерации {generation} не перетирает причину: {reason}");
                return false;
            }

            var held = new HeldCaptureOutput(
                monitorDeviceId,
                generation,
                reason,
                DateTimeOffset.UtcNow,
                diagnosticContext);
            Held[monitorDeviceId] = held;
            Log.Error("Capture",
                $"Выход монитора помечен как удерживаемый незавершившейся сессией DDA: {held.Describe()}. " +
                "Новые нативные DuplicateOutput для него запрещены до выхода из процесса.");
            return true;
        }
    }

    public static bool IsHeld(string? monitorDeviceId, out HeldCaptureOutput output)
    {
        if (!string.IsNullOrWhiteSpace(monitorDeviceId))
        {
            lock (Sync)
            {
                if (Held.TryGetValue(monitorDeviceId, out HeldCaptureOutput? held))
                {
                    output = held;
                    return true;
                }
            }
        }

        output = null!;
        return false;
    }

    public static bool IsHeld(string? monitorDeviceId) => IsHeld(monitorDeviceId, out _);

    public static IReadOnlyList<HeldCaptureOutput> Snapshot()
    {
        lock (Sync) return [.. Held.Values];
    }

    /// <summary>Только для тестов: боевой код пометку не снимает.</summary>
    internal static void ResetForTests()
    {
        lock (Sync) Held.Clear();
    }
}
