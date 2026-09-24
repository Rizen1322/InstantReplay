namespace Aura.Core.Encoding;

/// <summary>
/// Учёт занятости слотов пула текстур энкодера (<see cref="EncoderTexturePool"/>).
///
/// Слот занят, пока у него есть хоть одна ссылка: кадр лежит в очереди, энкодер
/// ещё не выдал его результат или пейсер держит его как последний кадр для
/// дубликатов. Выдаётся только свободный слот, по кругу от последней выдачи, —
/// так только что освободившийся слот берётся последним.
///
/// Отдельно от пула, чтобы проверять без видеокарты.
/// </summary>
internal sealed class PoolSlotLedger
{
    private readonly int[] _refs;
    private readonly object _sync = new();
    private int _next;

    public PoolSlotLedger(int slots)
    {
        _refs = new int[Math.Max(1, slots)];
    }

    public int Count => _refs.Length;

    /// <summary>Сколько слотов сейчас занято.</summary>
    public int Busy
    {
        get { lock (_sync) return _refs.Count(r => r > 0); }
    }

    /// <summary>Занять свободный слот одной ссылкой; -1 — свободных нет.</summary>
    public int TryTake()
    {
        lock (_sync)
        {
            for (int i = 0; i < _refs.Length; i++)
            {
                int slot = (_next + i) % _refs.Length;
                if (_refs[slot] != 0) continue;
                _refs[slot] = 1;
                _next = (slot + 1) % _refs.Length;
                return slot;
            }
            return -1;
        }
    }

    /// <summary>Ещё одна ссылка на уже занятый слот. На свободный не действует.</summary>
    public void AddRef(int slot)
    {
        lock (_sync)
            if ((uint)slot < (uint)_refs.Length && _refs[slot] > 0) _refs[slot]++;
    }

    /// <summary>Отпустить одну ссылку; слот без ссылок снова свободен.</summary>
    public void Release(int slot)
    {
        lock (_sync)
            if ((uint)slot < (uint)_refs.Length && _refs[slot] > 0) _refs[slot]--;
    }

    /// <summary>Все слоты свободны (пул уничтожен).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_refs);
            _next = 0;
        }
    }
}
