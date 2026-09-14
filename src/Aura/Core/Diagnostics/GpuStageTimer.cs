using Vortice.Direct3D11;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Сколько времени стадии конвейера занимают У ВИДЕОКАРТЫ, а не у процессора.
///
/// ЗАЧЕМ. Замеры в <see cref="PipelineProbe"/> сделаны секундомером на потоке, и
/// меряют они ровно одно: сколько длилась постановка команд в очередь видеокарты.
/// Сама работа выполняется позже и асинхронно, поэтому в логе годами стояли нули
/// вроде «NV12 0,1 мс» на преобразовании кадра 2560x1440 — цифра честная, но не о
/// том. Спор «нужна ли третья копия кадра» по ней решить нельзя.
///
/// Здесь метки ставит сама видеокарта. Порядок такой:
///
/// • запрос TimestampDisjoint оборачивает кадр и говорит частоту счётчика, а также
///   признак Disjoint — за время кадра частота менялась, и разности бессмысленны;
/// • запросы Timestamp между стадиями дают сами отсчёты.
///
/// Результат нельзя читать в том же кадре: видеокарта отстаёт от процессора, и
/// ожидание ответа свело бы на нет всю асинхронность. Поэтому слоты идут кольцом,
/// а забираем мы готовое из слотов постарше.
///
/// Замер стоит недорого, но не бесплатно, поэтому меряется не каждый кадр, а один
/// из <see cref="SampleEveryNthFrame"/>.
/// </summary>
public sealed class GpuStageTimer : IDisposable
{
    /// <summary>Какой кадр из скольких попадает под замер.</summary>
    private const int SampleEveryNthFrame = 30;

    /// <summary>
    /// Сколько замеров живёт одновременно. Видеокарта отстаёт на кадр-другой,
    /// четырёх слотов хватает, чтобы ни разу не ждать её ответа.
    /// </summary>
    private const int Slots = 4;

    /// <summary>Границы стадий. Меток на одну больше, чем стадий.</summary>
    public const int StageConvert = 0;
    public const int StageCopyToPool = 1;
    private const int StageCount = 2;
    private const int MarkCount = StageCount + 1;

    private sealed class Measurement
    {
        public ID3D11Query? Disjoint;
        public readonly ID3D11Query?[] Marks = new ID3D11Query?[MarkCount];
        public bool InFlight;
        public int Filled;
    }

    private readonly Measurement[] _slots = new Measurement[Slots];
    private readonly object _sync = new();

    private readonly double[] _total = new double[StageCount];
    private readonly double[] _peak = new double[StageCount];
    private int _samples;

    private int _frame;
    private int _next;
    private Measurement? _open;
    private bool _unavailable;

    public GpuStageTimer(ID3D11Device device)
    {
        try
        {
            for (int i = 0; i < Slots; i++)
            {
                var measurement = new Measurement
                {
                    Disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint))
                };
                for (int mark = 0; mark < MarkCount; mark++)
                    measurement.Marks[mark] = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
                _slots[i] = measurement;
            }
        }
        catch (Exception ex)
        {
            // Запросы времени не обязаны поддерживаться. Без них конвейер работает
            // как работал, просто в логе не будет времён видеокарты.
            Logging.Log.Info("Probe", $"Таймеры видеокарты недоступны: {ex.Message}");
            _unavailable = true;
        }
    }

    /// <summary>
    /// Открыть замер на этот кадр. Возвращает false, если кадр не под замером или
    /// свободных слотов нет — тогда метки ставить не нужно.
    /// </summary>
    public bool BeginFrame(ID3D11DeviceContext context)
    {
        if (_unavailable) return false;
        if (++_frame % SampleEveryNthFrame != 0) return false;

        lock (_sync)
        {
            // Прошлый замер не закрыли: между метками бросило исключение, и кадр
            // ушёл в обработчик ошибок мимо EndFrame. Слот освобождаем и начинаем
            // заново — иначе один сбой выключил бы замеры до конца сеанса.
            if (_open is not null)
            {
                _open.InFlight = false;
                _open = null;
            }

            Measurement slot = _slots[_next];
            if (slot.InFlight) return false;       // ещё не забрали прошлый ответ
            _next = (_next + 1) % Slots;

            try
            {
                context.Begin(slot.Disjoint!);
                context.End(slot.Marks[0]!);
                slot.Filled = 1;
                _open = slot;
                return true;
            }
            catch (Exception ex)
            {
                Logging.Log.Info("Probe", $"Таймер видеокарты не открылся: {ex.Message}");
                _unavailable = true;
                return false;
            }
        }
    }

    /// <summary>Отметить конец стадии. Звать в том же порядке, что и стадии.</summary>
    public void Mark(ID3D11DeviceContext context)
    {
        lock (_sync)
        {
            if (_unavailable || _open is null || _open.Filled >= MarkCount) return;
            try
            {
                context.End(_open.Marks[_open.Filled]!);
                _open.Filled++;
            }
            catch { _open = null; }
        }
    }

    /// <summary>Закрыть замер кадра и забрать всё, что видеокарта успела посчитать.</summary>
    public void EndFrame(ID3D11DeviceContext context)
    {
        lock (_sync)
        {
            if (_unavailable) return;
            if (_open is not null)
            {
                try
                {
                    context.End(_open.Disjoint!);
                    _open.InFlight = _open.Filled == MarkCount;
                }
                catch { }
                _open = null;
            }

            CollectLocked(context);
        }
    }

    private unsafe void CollectLocked(ID3D11DeviceContext context)
    {
        // Буфер под метки один на весь обход: stackalloc внутри цикла растил бы
        // стек на каждой итерации до выхода из метода.
        Span<ulong> marks = stackalloc ulong[MarkCount];
        foreach (Measurement slot in _slots)
        {
            if (!slot.InFlight) continue;

            QueryDataTimestampDisjoint disjoint;
            // DoNotFlush: ждать видеокарту нельзя, мы на горячем пути. Ответ ещё не
            // готов — это S_FALSE, а не ошибка; заберём в следующий раз.
            if (context.GetData(slot.Disjoint!, new IntPtr(&disjoint),
                                (uint)sizeof(QueryDataTimestampDisjoint),
                                AsyncGetDataFlags.DoNotFlush) != SharpGen.Runtime.Result.Ok)
                continue;

            slot.InFlight = false;
            if (disjoint.Disjoint || disjoint.Frequency == 0) continue;  // частота плыла

            marks.Clear();
            bool complete = true;
            for (int i = 0; i < MarkCount && complete; i++)
            {
                ulong value;
                complete = context.GetData(slot.Marks[i]!, new IntPtr(&value), sizeof(ulong),
                                           AsyncGetDataFlags.DoNotFlush) == SharpGen.Runtime.Result.Ok;
                marks[i] = value;
            }
            if (!complete) continue;

            for (int stage = 0; stage < StageCount; stage++)
            {
                if (marks[stage + 1] < marks[stage]) continue;
                double ms = (marks[stage + 1] - marks[stage]) * 1000.0 / disjoint.Frequency;
                _total[stage] += ms;
                if (ms > _peak[stage]) _peak[stage] = ms;
            }
            _samples++;
        }
    }

    /// <summary>Отчёт за окно и сброс накопленного. Пустая строка — замеров не было.</summary>
    public string TakeReport()
    {
        lock (_sync)
        {
            if (_samples == 0) return "";

            string report =
                $"Время видеокарты (среднее/пик мс, {_samples} замеров): " +
                $"NV12 {_total[StageConvert] / _samples:F2}/{_peak[StageConvert]:F2}, " +
                $"копия в пул {_total[StageCopyToPool] / _samples:F2}/{_peak[StageCopyToPool]:F2}";

            Array.Clear(_total);
            Array.Clear(_peak);
            _samples = 0;
            return report;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            // Сначала закрываем открытый замер: без этого Mark на уже освобождённом
            // запросе ушёл бы в мёртвый объект.
            _unavailable = true;
            _open = null;

            foreach (Measurement slot in _slots)
            {
                if (slot is null) continue;
                slot.InFlight = false;
                slot.Disjoint?.Dispose();
                for (int i = 0; i < MarkCount; i++) slot.Marks[i]?.Dispose();
            }
        }
    }
}
