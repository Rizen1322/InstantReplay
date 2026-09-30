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
    private long _lastEncoded, _lastSubmitted, _lastDropped;
    private int _calm;
    private int _recoveryNeeded = RecoveryWindows;
    private int _sinceRecovery = int.MaxValue;

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
    /// submitP95Ms — 95-й перцентиль времени отправки кадра в NVENC за окно.
    /// </summary>
    public int? Tick(long nowMs, long encoded, long submitted, long dropped, double submitP95Ms, out string reason)
    {
        reason = "";
        if (nowMs < _nextCheckMs) return null;
        _nextCheckMs = nowMs + WindowMs;

        long dEncoded = encoded - _lastEncoded, dSubmitted = submitted - _lastSubmitted, dDropped = dropped - _lastDropped;
        _lastEncoded = encoded; _lastSubmitted = submitted; _lastDropped = dropped;
        if (!CanAdapt) return null;

        double target = _fps * (WindowMs / 1000.0);
        double frameMs = 1000.0 / _fps;
        // Кадры до энкодера должны были дойти: если голодает захват, NVENC не виноват
        bool fed = dSubmitted >= target * 0.9;
        bool behind = dEncoded < target * 0.95 || dDropped > 0;
        bool slowSubmit = submitP95Ms > frameMs;         // отправка дольше целого кадра
        bool overloaded = fed && (behind || slowSubmit);
        bool calm = !behind && submitP95Ms < frameMs * 0.4;
        if (_sinceRecovery < int.MaxValue) _sinceRecovery++;

        if (overloaded)
        {
            _calm = 0;
            if (_sinceRecovery <= RelapseWindows)
                _recoveryNeeded = Math.Min(_recoveryNeeded * 2, MaxRecoveryWindows);
            _sinceRecovery = int.MaxValue;
            if (Level >= MaxLevel) return null;
            Level++;
            reason = $"энкодер не успевает: закодировано {dEncoded / (WindowMs / 1000.0):F0} из {_fps} кадр/с, " +
                     $"отправка p95 {submitP95Ms:F1} мс, потеряно {dDropped}";
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
