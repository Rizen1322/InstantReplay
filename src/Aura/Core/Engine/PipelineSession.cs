using Aura.Core.Capture;
using Aura.Core.Encoding;
using Aura.Core.Logging;

namespace Aura.Core.Engine;

/// <summary>
/// Один видеоконвейер целиком: захват, перевод цвета, масштабирование, брокер
/// кадров, энкодер и замеры видеокарты. У всего этого один владелец и одно
/// поколение (<see cref="Id"/>, оно же generation захвата).
///
/// ЗАЧЕМ. Раньше части конвейера лежали шестью отдельными полями движка. Разбирать
/// их приходилось в правильном порядке по месту, бросать при зависшей видеокарте
/// по списку, а поздние события старого конвейера отсекать каждое по-своему.
/// Теперь пересборка создаёт новый объект сессии: старый либо разбирается целиком
/// (<see cref="DisposeOrdered"/>), либо бросается целиком, и всё, что держит ссылку
/// на старую сессию, видит, что она уже не текущая.
/// </summary>
internal sealed class PipelineSession(long id)
{
    public long Id { get; } = id;
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public IScreenCapture? Capture;
    public VideoProcessorNv12? Processor;
    /// <summary>Уменьшение кадра Ланцошем до видеопроцессора; null — размер не меняется.</summary>
    public LanczosScaler? Scaler;
    /// <summary>Время стадий по часам видеокарты. Диагностика, на запись не влияет.</summary>
    public Diagnostics.GpuStageTimer? GpuTimer;
    public VideoEncoder? Encoder;
    public GpuCaptureFrameBroker? FrameBroker;

    /// <summary>
    /// Разобрать в единственно верном порядке: энкодер (его пейсер держит контекст
    /// захвата) → замеры → масштабирование → видеопроцессор → брокер → и лишь затем
    /// устройство захвата. Подробности — у вызова в ReplayEngine.StopLocked.
    /// </summary>
    public void DisposeOrdered()
    {
        Encoder?.Dispose(); Encoder = null;
        GpuTimer?.Dispose(); GpuTimer = null;
        Scaler?.Dispose(); Scaler = null;
        Processor?.Dispose(); Processor = null;
        FrameBroker?.Dispose(); FrameBroker = null;
        Capture?.Dispose(); Capture = null;
    }

    /// <summary>Есть ли что разбирать или бросать.</summary>
    public bool IsEmpty => Capture is null && Processor is null && Scaler is null &&
                           GpuTimer is null && Encoder is null && FrameBroker is null;

    public override string ToString() =>
        $"конвейер #{Id} ({(DateTime.UtcNow - StartedUtc).TotalMinutes:F0} мин)";
}

/// <summary>
/// Брошенные конвейеры: видеокарта не отпустила их объекты, освобождать их под
/// висящим потоком нельзя. Держим до конца процесса (финализатор на зависшем
/// устройстве повесил бы сборку мусора), но не без счёта: каждый держит
/// видеопамять, и после <see cref="Limit"/> штук повтор останавливается.
/// С хостом NVENC сюда попадают только зависшие захваты: энкодер в хосте
/// просто убивается вместе с процессом.
/// </summary>
internal static class AbandonedPipelines
{
    public const int Limit = 3;
    private static readonly List<PipelineSession> s_sessions = [];

    /// <summary>Бросить сессию. Возвращает, сколько их теперь брошено.</summary>
    public static int Add(PipelineSession session)
    {
        lock (s_sessions)
        {
            s_sessions.Add(session);
            Log.Error("Engine", $"{session} брошен целиком: его объекты держит зависшая видеокарта " +
                                $"(брошено за запуск: {s_sessions.Count} из {Limit})");
            return s_sessions.Count;
        }
    }

    public static int Count
    {
        get { lock (s_sessions) return s_sessions.Count; }
    }
}
