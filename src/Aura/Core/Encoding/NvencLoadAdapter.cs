namespace Aura.Core.Encoding;

/// <summary>
/// Задержки одной стадии за окно: p50/p95/p99/максимум в миллисекундах.
/// Потокобезопасно: пишут поток энкодера и поток выдачи, читает статистика.
/// </summary>
internal sealed class LatencyStats
{
    private readonly object _sync = new();
    private List<float> _samples = new(4096);

    public void Add(double ms)
    {
        lock (_sync) if (_samples.Count < 100_000) _samples.Add((float)ms);
    }

    public readonly record struct Summary(int Count, double P50, double P95, double P99, double Max)
    {
        public override string ToString() =>
            Count == 0 ? "нет замеров" : $"p50 {P50:F1}, p95 {P95:F1}, p99 {P99:F1}, макс {Max:F1} мс";
    }

    /// <summary>Сводка за окно; окно начинается заново.</summary>
    public Summary Take()
    {
        List<float> samples;
        lock (_sync)
        {
            samples = _samples;
            _samples = new List<float>(Math.Max(256, samples.Count));
        }
        if (samples.Count == 0) return default;
        samples.Sort();
        double At(double q) => samples[Math.Clamp((int)Math.Ceiling(q * samples.Count) - 1, 0, samples.Count - 1)];
        return new Summary(samples.Count, At(0.50), At(0.95), At(0.99), samples[^1]);
    }
}

/// <summary>
/// Гистограмма задержек за весь сеанс: перцентили без хранения каждого замера.
/// Шаг 0.1 мс до 100 мс, дальше 1 мс до 2 с; всё длиннее в последнюю корзину.
/// </summary>
internal sealed class LatencyHistogram
{
    private readonly long[] _buckets = new long[1000 + 1900 + 1];
    private long _count;
    private double _max;
    private readonly object _sync = new();

    private static int Bucket(double ms) =>
        ms < 100 ? (int)Math.Max(0, ms * 10) : ms < 2000 ? 1000 + (int)(ms - 100) : 2900;

    private static double Value(int bucket) =>
        bucket < 1000 ? (bucket + 0.5) / 10 : bucket < 2900 ? 100 + (bucket - 1000) + 0.5 : 2000;

    public void Add(double ms)
    {
        lock (_sync)
        {
            _buckets[Bucket(ms)]++;
            _count++;
            if (ms > _max) _max = ms;
        }
    }

    public LatencyStats.Summary Summary()
    {
        lock (_sync)
        {
            if (_count == 0) return default;
            double At(double q)
            {
                long need = (long)Math.Ceiling(q * _count), seen = 0;
                for (int i = 0; i < _buckets.Length; i++)
                    if ((seen += _buckets[i]) >= need) return Value(i);
                return _max;
            }
            return new LatencyStats.Summary((int)Math.Min(_count, int.MaxValue), At(0.5), At(0.95), At(0.99), _max);
        }
    }
}

/// <summary>
/// Стоп-кадры: сколько картинка в записи на самом деле стояла.
///
/// Время в файле идёт по ровной сетке 1/fps, и там, где настоящего кадра не было,
/// стоит повтор прошлого. ffprobe такой файл считает идеальным, а человек видит
/// замершую игру. Поэтому считаем промежутки между НАСТОЯЩИМИ кадрами, дошедшими
/// до энкодера, и разбираем причину:
/// • «захват» — новых кадров не было вовсе (завис захват или на экране ничего не
///   менялось: статичный рабочий стол и меню тоже сюда);
/// • «конвейер» — кадры были, но потерялись по дороге (очередь энкодера, опоздание).
/// </summary>
internal sealed class FreezeStats
{
    private readonly object _sync = new();
    private long _lastRealTicks = -1;
    private long _lastLost;

    public long Frames, RealFrames, Duplicates;
    public int Over50, Over100, Over250;
    public int PipelineFreezes;          // из них с потерей кадров по дороге
    public double LongestMs, FrozenMs;
    public string LongestCause = "";

    /// <summary>Кадр пошёл в энкодер. lostSoFar — счётчик потерянных настоящих кадров.</summary>
    public void Add(long ticks, bool duplicate, long lostSoFar, double frameMs)
    {
        lock (_sync)
        {
            Frames++;
            if (duplicate) { Duplicates++; return; }
            RealFrames++;
            if (_lastRealTicks >= 0)
            {
                double gap = (ticks - _lastRealTicks) / 10_000.0;
                if (gap > 50)
                {
                    bool pipeline = lostSoFar > _lastLost;
                    Over50++;
                    if (gap > 100) Over100++;
                    if (gap > 250) Over250++;
                    if (pipeline) PipelineFreezes++;
                    FrozenMs += gap - frameMs;
                    if (gap > LongestMs) { LongestMs = gap; LongestCause = pipeline ? "конвейер" : "захват"; }
                }
            }
            _lastRealTicks = ticks;
            _lastLost = lostSoFar;
        }
    }

    public FreezeStats TakeAndReset()
    {
        lock (_sync)
        {
            var copy = (FreezeStats)MemberwiseClone();
            Frames = RealFrames = Duplicates = 0;
            Over50 = Over100 = Over250 = PipelineFreezes = 0;
            LongestMs = FrozenMs = 0;
            LongestCause = "";
            return copy;
        }
    }

    public void MergeInto(FreezeStats total)
    {
        lock (total._sync)
        {
            total.Frames += Frames; total.RealFrames += RealFrames; total.Duplicates += Duplicates;
            total.Over50 += Over50; total.Over100 += Over100; total.Over250 += Over250;
            total.PipelineFreezes += PipelineFreezes; total.FrozenMs += FrozenMs;
            if (LongestMs > total.LongestMs) { total.LongestMs = LongestMs; total.LongestCause = LongestCause; }
        }
    }

    public override string ToString() =>
        $"стоп-кадров >50 мс {Over50} (>100: {Over100}, >250: {Over250}, из них по вине конвейера {PipelineFreezes}), " +
        $"самый долгий {LongestMs:F0} мс{(LongestCause.Length > 0 ? $" ({LongestCause})" : "")}, " +
        $"всего стояло {FrozenMs / 1000:F1} с, настоящих кадров {RealFrames} из {Frames} " +
        $"({(Frames == 0 ? 0 : RealFrames * 100.0 / Frames):F0}%)";
}

/// <summary>
/// Снижение нагрузки на NVENC на ходу, когда игра забирает видеокарту.
///
/// ЗАЧЕМ. На 2560×1440@60 рядом с CS2 NVENC с B-кадрами и вторым проходом не
/// успевал: отправка кадра в среднем 5–11 мс, пики до 350 мс, и за полчаса 51
/// провал записи. Настройки под разрешение выставляются при старте, но нагрузка
/// игры заранее не известна. Здесь окно в 5 секунд: если энкодер не успевает,
/// снимаем по ступени — сначала второй проход, потом пространственный AQ. Оба
/// меняются через nvEncReconfigureEncoder без новой сессии и без ключевого кадра,
/// поэтому повтор и запись не рвутся. B-кадры так не выключить: структуру GOP
/// API менять на ходу запрещает.
///
/// Лучше чуть больше битрейта на кадр и чуть грубее сцены с мелкими деталями,
/// чем провал в 30 кадров в момент, который человек хотел сохранить.
///
/// Возврат осторожный: после минуты спокойствия на одну ступень вверх; если та
/// же нагрузка сразу вернулась, следующая попытка ждёт вдвое дольше (до 16 минут).
/// </summary>
internal sealed class NvencLoadAdapter
{
    public const int WindowMs = 5000;
    private const int RecoveryWindows = 12;        // минута
    private const int MaxRecoveryWindows = 192;    // 16 минут
    private const int RelapseWindows = 3;          // вернулась нагрузка за 15 с — возврат не удался

    private readonly int _fps;
    private readonly List<(string Name, int Multipass, bool Aq)> _levels = [];
    private long _nextCheckMs = WindowMs;
    private long _lastCheckMs;
    private long _lastEncoded, _lastSubmitted, _lastDropped;
    private int _calm;
    private int _recoveryNeeded = RecoveryWindows;
    private int _sinceRecovery = int.MaxValue;
    private bool _softPrevious;

    public int Level { get; private set; }
    public int MaxLevel => _levels.Count - 1;
    public string LevelName => _levels[Level].Name;
    public (int Multipass, bool Aq) Current => (_levels[Level].Multipass, _levels[Level].Aq);

    public NvencLoadAdapter(int fps, int baseMultipass, bool baseAq)
    {
        _fps = Math.Max(1, fps);
        _levels.Add(("полное качество", baseMultipass, baseAq));
        if (baseMultipass > 0) _levels.Add(("без второго прохода", 0, baseAq));
        if (baseAq) _levels.Add(("без второго прохода и AQ", 0, false));
    }

    /// <summary>Есть ли что снимать: при старте без второго прохода и AQ адаптации нет.</summary>
    public bool CanAdapt => _levels.Count > 1;

    /// <summary>
    /// Раз в окно решает, менять ли ступень. Возвращает новую ступень или null.
    ///
    /// Судим по пропускной способности, а не по задержке выхода: с B-кадрами кадр
    /// законно выходит через 30–50 мс, это буферизация, а не перегруз. Перегруз —
    /// когда поданное не успевает кодироваться: закодировано меньше поданного,
    /// кадры теряются, очередь растёт, самый старый ждущий кадр старше 100 мс или
    /// сам вызов отправки подвисает (p99 дольше 25 мс).
    /// </summary>
    public int? Tick(long nowMs, long encoded, long submitted, long dropped, double submitP99Ms,
                     double maxPendingAgeMs, int queueGrowth, out string reason)
    {
        reason = "";
        if (nowMs < _nextCheckMs) return null;
        double elapsedSeconds = Math.Max(1, nowMs - _lastCheckMs) / 1000.0;
        _lastCheckMs = nowMs;
        _nextCheckMs = nowMs + WindowMs;

        long dEncoded = encoded - _lastEncoded, dSubmitted = submitted - _lastSubmitted, dDropped = dropped - _lastDropped;
        _lastEncoded = encoded; _lastSubmitted = submitted; _lastDropped = dropped;
        if (!CanAdapt) return null;

        double target = _fps * elapsedSeconds;
        // Низкая подача без очереди может быть статичным экраном. Но старая
        // очередь/дропы — backpressure, который сам уже мог ограничить подачу.
        bool fed = dSubmitted >= target * 0.9;
        bool behind = dEncoded < Math.Min(dSubmitted, target) * 0.95 || dDropped > 0;
        // Жёсткие признаки: кадры теряются, очередь растёт, энкодер отстаёт,
        // кадр ждёт полсекунды или отправка стоит сотни миллисекунд в каждом
        // тридцатом кадре. Мягкие — разовый пик: один старый кадр в очереди
        // (100–500 мс) или p99 отправки выше 25 мс. В записи CS2 под хостом NVENC
        // такой пик в одном окне при 60 из 60 кадров и нулевых потерях снимал AQ
        // на минуту трижды за десять минут. Мягкий признак считается, только если
        // повторился два окна подряд.
        bool strong = dDropped > 0 || queueGrowth >= 5 || maxPendingAgeMs > 500 ||
                      (fed && (behind || submitP99Ms > 150));
        bool soft = maxPendingAgeMs > 100 || (fed && submitP99Ms > 25);
        bool overloaded = strong || (soft && _softPrevious);
        _softPrevious = soft;
        bool calm = fed && dEncoded >= target * 0.95 && !behind &&
                    submitP99Ms < 8 && maxPendingAgeMs < 50 && queueGrowth <= 1;
        if (_sinceRecovery < int.MaxValue) _sinceRecovery++;

        if (overloaded)
        {
            _calm = 0;
            if (_sinceRecovery <= RelapseWindows)
                _recoveryNeeded = Math.Min(_recoveryNeeded * 2, MaxRecoveryWindows);
            _sinceRecovery = int.MaxValue;
            if (Level >= MaxLevel) return null;
            Level++;
            reason = $"энкодер не успевает: закодировано {dEncoded / elapsedSeconds:F0} из {_fps} кадр/с, " +
                     $"потеряно {dDropped}, отправка p99 {submitP99Ms:F1} мс, самый старый кадр в очереди " +
                     $"{maxPendingAgeMs:F0} мс, рост очереди {queueGrowth}";
            return Level;
        }

        if (!calm) { _calm = 0; return null; }
        if (Level == 0)
        {
            // Долгое спокойствие на полном качестве: порог возврата снова короткий
            if (++_calm >= MaxRecoveryWindows) { _recoveryNeeded = RecoveryWindows; _calm = 0; }
            return null;
        }
        if (++_calm < _recoveryNeeded) return null;
        _calm = 0;
        Level--;
        _sinceRecovery = 0;
        reason = $"нагрузка спала {_recoveryNeeded * WindowMs / 1000} с";
        return Level;
    }
}
