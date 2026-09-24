using Aura.Core.Logging;

namespace Aura.Core.Buffering;

/// <summary>
/// Кадр из снимка буфера: байты лежат в арене по адресу <see cref="Data"/> и
/// валидны, пока жив <see cref="VideoSnapshot"/>, которому кадр принадлежит.
/// </summary>
public readonly record struct BufferedFrame(
    IntPtr Data, int Length, long PtsTicks, long DurationTicks, bool IsKeyframe, long DtsTicks)
{
    public unsafe ReadOnlySpan<byte> Span => new((void*)Data, Length);
}

/// <summary>
/// Замороженный кусок буфера видео для записи в файл. Держит арену живой: пока
/// снимок не освобождён, адреса его кадров остаются действительными, даже если
/// буфер за это время очистили или перевыделили.
/// </summary>
public sealed class VideoSnapshot : IReadOnlyList<BufferedFrame>, IDisposable
{
    public static VideoSnapshot Empty => new(null, []);

    private ArenaStorage? _storage;
    private readonly BufferedFrame[] _frames;

    internal VideoSnapshot(ArenaStorage? storage, BufferedFrame[] frames)
    {
        _storage = storage;
        _frames = frames;
        foreach (var frame in frames) TotalBytes += frame.Length;
    }

    public int Count => _frames.Length;

    public BufferedFrame this[int index] => _frames[index];

    public IReadOnlyList<BufferedFrame> Frames => _frames;

    /// <summary>Сумма байт всех кадров.</summary>
    public long TotalBytes { get; }

    public void Dispose() => Interlocked.Exchange(ref _storage, null)?.Release();

    public IEnumerator<BufferedFrame> GetEnumerator() => ((IEnumerable<BufferedFrame>)_frames).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Кольцевой буфер сжатого видео.
///
/// УСТРОЙСТВО. Кадры лежат вплотную друг за другом в одном непрерывном диапазоне
/// памяти (<see cref="ArenaStorage"/>), а кольцо записей хранит только смещения.
/// Диапазон — либо оперативная память вне кучи .NET, либо файл на диске,
/// отображённый в память (режим «буфер на диске»). Физическая память выдаётся
/// блоками по мере того, как запись до них доходит, и возвращается системе, когда
/// блок позади кольца опустел: занято ровно столько, сколько нужно под живые кадры.
///
/// Раньше арена была набором byte[] в куче больших объектов, а записи — узлами
/// LinkedList (объект на каждый кадр, до сотни тысяч живых на длинном буфере).
/// Теперь сборщик мусора в буфере не участвует вовсе.
///
/// Грабли из практики: вытеснять надо ПО ВРЕМЕНИ, а не только по объёму,
/// иначе UI показывает "в буфере 5 минут", а реально сохранится меньше.
/// Здесь: держим кадры за (MaxDuration + 1 GOP), при вытеснении режем
/// строго по ключевому кадру, чтобы буфер всегда начинался с keyframe —
/// иначе сохранённый MP4 первые секунды будет "кашей".
/// </summary>
public sealed class ReplayVideoBuffer
{
    private const int ChunkBytes = ArenaStorage.ChunkBytes;

    /// <summary>
    /// Потолок арены в оперативной памяти. Раньше это были жёсткие 4 ГБ — предел,
    /// выведенный из byte[]. Вне кучи .NET предела по массиву нет, и ограничивает
    /// только разумная доля физической памяти: не больше 40% от установленной,
    /// но и не меньше 2 ГБ, как бы мало памяти ни было.
    /// </summary>
    public static long MaximumCapacityBytes { get; } = ComputeRamCap();

    /// <summary>Потолок арены на диске: 30 минут при 150 Мбит/с помещаются с запасом.</summary>
    public const long MaximumDiskCapacityBytes = 64L << 30;

    private static long ComputeRamCap()
    {
        long physical = 0;
        try { physical = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; } catch { }
        if (physical <= 0) return 4L << 30;
        long cap = (long)(physical * 0.4);
        cap = Math.Clamp(cap, 2L << 30, 32L << 30);
        return cap / ChunkBytes * ChunkBytes;
    }

    /// <summary>
    /// Запас сверх заказанной длительности: один GOP плюс страховка вытеснения.
    /// Не приватная: тем же запасом обязано жить кольцо звука, иначе клип начнётся
    /// с тишины — снимок видео стартует с keyframe РАНЬШЕ заказанной границы.
    /// </summary>
    internal const int SlackSeconds = 15;

    /// <summary>
    /// Запас под запись файла. Пока сохраняется клип, его кадры остаются в арене,
    /// а запись продолжается — новым кадрам нужно куда-то ложиться. Раньше это были
    /// постоянные 64 МБ: на 80–150 Мбит/с это всего 3–6 секунд, а под нагрузкой
    /// игры сохранение на HDD идёт дольше. Теперь запас — десять секунд записи на
    /// заданном битрейте, но не меньше 64 МБ.
    /// </summary>
    public static long SaveHeadroomBytes(long bitrateBps) =>
        Math.Max(64L << 20, Math.Max(1, bitrateBps) / 8 * 10);

    private const long KeyframeSafetyTicks = 100_000_000; // 10 сек сверх лимита

    /// <summary>Кадр в арене: смещение, длина и сколько занимает вместе с довеском на стыке кольца.</summary>
    /// <summary>
    /// Время буфера (вытеснение, длительность, точка среза) идёт по времени
    /// ДЕКОДИРОВАНИЯ: оно растёт монотонно и с B-кадрами, когда время показа
    /// скачет вперёд-назад.
    /// </summary>
    private readonly record struct Entry(
        long Offset, int Length, int Total, long PtsTicks, long DtsTicks, long DurationTicks, bool IsKeyframe);

    private readonly object _sync = new();

    // Кольцо записей: массив структур, растёт удвоением. Ни одного объекта на кадр.
    private Entry[] _ring = new Entry[1024];
    private int _ringHead;
    private int _ringCount;

    private ArenaStorage? _storage;
    private long _capacity;
    private long _tail;   // позиция записи внутри арены, [0, _capacity)
    private long _used;   // занято байт, включая довески на стыке кольца

    // Кадры, отданные снимку: их уже нет в кольце, но их байты в арене трогать
    // нельзя, пока файл не записан. Токен защищает от освобождения чужого снимка.
    private long _reserved;
    private long _reservedStart;
    private long _reservedToken;
    private long _nextToken = 1;

    /// <summary>
    /// Поток кадров прерван: кадр пришлось отбросить, и следующие P-кадры ссылаются
    /// на то, чего в буфере нет. Раньше они всё равно добавлялись, и до следующего
    /// ключевого кадра клип показывал «кашу». Теперь до ключевого кадра не
    /// принимаем ничего, а энкодер просим выдать его немедленно.
    /// </summary>
    private bool _awaitKeyframe;

    /// <summary>Блок, в котором голова кольца была при прошлом пересчёте.</summary>
    private int _lastHeadChunk = -1;
    private int _lastTailChunk = -1;
    private int _preparing;

    private bool _warnedNoKeyframes, _warnedHugeFrame, _warnedNoRoom;
    private long _droppedFrames;

    /// <summary>
    /// Буфер ждёт ключевой кадр: его надо попросить у энкодера. Вызывается под
    /// замком буфера, обработчик обязан быть мгновенным.
    /// </summary>
    public event Action? KeyframeNeeded;

    public long MaxDurationTicks { get; set; }

    /// <summary>Занято байт — столько реально держит буфер.</summary>
    public long TotalBytes { get { lock (_sync) return _used; } }

    /// <summary>Ёмкость арены в байтах (сколько максимум может занять).</summary>
    public long CapacityBytes { get { lock (_sync) return _capacity; } }

    /// <summary>Сколько физической памяти сейчас занимает арена.</summary>
    public long ResidentBytes { get { lock (_sync) return (long)(_storage?.ResidentChunks ?? 0) * ChunkBytes; } }

    /// <summary>Буфер лежит в файле на диске.</summary>
    public bool OnDisk { get { lock (_sync) return _storage?.OnDisk == true; } }

    /// <summary>Сколько кадров отброшено из-за нехватки места (для диагностики).</summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    private ref Entry At(int i) => ref _ring[(_ringHead + i) & (_ring.Length - 1)];

    private int FirstKeyframeIndex()
    {
        for (int i = 0; i < _ringCount; i++)
            if (At(i).IsKeyframe) return i;
        return -1;
    }

    /// <summary>
    /// Сколько повтора реально СОХРАНИТСЯ, тики.
    ///
    /// Считается от первого КЛЮЧЕВОГО кадра и обрезается настройкой: буфер
    /// намеренно держит запас сверх заказанного, но сохранить эти секунды нельзя —
    /// снимок режется по keyframe.
    /// </summary>
    public long BufferedDurationTicks
    {
        get
        {
            lock (_sync)
            {
                if (_ringCount == 0) return 0;
                int first = FirstKeyframeIndex();
                if (first < 0) return 0;

                long span = At(_ringCount - 1).DtsTicks - At(first).DtsTicks;
                return MaxDurationTicks > 0 ? Math.Min(span, MaxDurationTicks) : span;
            }
        }
    }

    /// <summary>
    /// Задать арену под длительность и битрейт из настроек. Вызывается при старте
    /// конвейера; физическая память выдаётся лениво, по мере заполнения.
    ///
    /// <paramref name="diskDirectory"/> не null — буфер в файле в этой папке.
    /// </summary>
    public void Allocate(long bitrateBps, int seconds, string? diskDirectory = null)
    {
        bool onDisk = diskDirectory is not null;
        long wanted = RequiredCapacityBytes(bitrateBps, seconds);
        long capacity = AllocatedCapacityBytes(bitrateBps, seconds, onDisk);

        ArenaStorage storage = onDisk
            ? new FileArenaStorage(diskDirectory!, capacity)
            : new NativeArenaStorage(capacity);

        ArenaStorage? old;
        lock (_sync)
        {
            old = _storage;
            _storage = storage;
            _capacity = capacity;
            ResetLocked();
        }
        old?.Release();
        PrepareAhead(0);

        Log.Info("Buffer", $"Арена буфера: {capacity / (1024 * 1024)} МБ " +
                           $"{(onDisk ? "на диске" : "в памяти")} на {seconds} сек при {bitrateBps / 1_000_000} Мбит/с");
        if (capacity < wanted)
            Log.Warn("Buffer", $"Для {seconds} сек при {bitrateBps / 1_000_000} Мбит/с нужно " +
                               $"{wanted / (1024 * 1024)} МБ — ограничено {capacity / (1024 * 1024)} МБ, " +
                               "буфер будет короче заданного");
    }

    public static long RequiredCapacityBytes(long bitrateBps, int seconds) =>
        (long)(Math.Max(1, bitrateBps) / 8.0 * (Math.Max(0, seconds) + SlackSeconds) * 1.05)
        + SaveHeadroomBytes(bitrateBps);

    public static long AllocatedCapacityBytes(long bitrateBps, int seconds, bool onDisk = false)
    {
        long cap = onDisk ? MaximumDiskCapacityBytes : MaximumCapacityBytes;
        long capped = Math.Clamp(RequiredCapacityBytes(bitrateBps, seconds), ChunkBytes, cap);
        return ((capped + ChunkBytes - 1) / ChunkBytes) * ChunkBytes;
    }

    public static int MaximumDurationSeconds(long bitrateBps, bool onDisk = false)
    {
        long cap = onDisk ? MaximumDiskCapacityBytes : MaximumCapacityBytes;
        double usable = Math.Max(0, cap - SaveHeadroomBytes(bitrateBps));
        double seconds = usable * 8 / Math.Max(1, bitrateBps) / 1.05 - SlackSeconds;
        return Math.Max(5, (int)Math.Floor(seconds));
    }

    /// <summary>
    /// Сменить размер арены (другая длина повтора, битрейт или место хранения),
    /// СОХРАНИВ накопленные кадры: они переписываются в новую арену подряд.
    ///
    /// Раньше любая правка настроек записи пересобирала конвейер с нуля, и повтор
    /// пропадал — поменял длину с двух минут на три и потерял последние две.
    /// Если новая арена меньше накопленного, отбрасываются самые старые кадры, а
    /// начало подрезается до ключевого кадра. Во время записи файла (снимок держит
    /// часть арены) размер не меняется: вызывающий обязан дождаться сохранения.
    /// </summary>
    public bool Resize(long bitrateBps, int seconds, string? diskDirectory)
    {
        bool onDisk = diskDirectory is not null;
        long capacity = AllocatedCapacityBytes(bitrateBps, seconds, onDisk);
        ArenaStorage storage;
        lock (_sync)
        {
            if (_storage is null || _reserved > 0) return false;
            if (capacity == _capacity && _storage.OnDisk == onDisk) return true;
        }

        storage = onDisk ? new FileArenaStorage(diskDirectory!, capacity) : new NativeArenaStorage(capacity);

        ArenaStorage? old;
        lock (_sync)
        {
            if (_storage is null || _reserved > 0) { storage.Release(); return false; }

            // Сколько самых свежих кадров помещается, начиная с ключевого
            long total = 0;
            int first = _ringCount;
            for (int i = _ringCount - 1; i >= 0; i--)
            {
                total += At(i).Length;
                if (total > capacity - SaveHeadroomBytes(bitrateBps)) break;
                first = i;
            }
            while (first < _ringCount && !At(first).IsKeyframe) first++;

            var entries = new Entry[Math.Max(1024, NextPow2(_ringCount - first))];
            long offset = 0;
            int count = 0;
            unsafe
            {
                byte* from = (byte*)_storage.Base;
                byte* to = (byte*)storage.Base;
                for (int i = first; i < _ringCount; i++)
                {
                    ref Entry e = ref At(i);
                    for (int c = (int)(offset / ChunkBytes); c <= (int)((offset + e.Length - 1) / ChunkBytes); c++)
                        storage.EnsureChunk(c);
                    Buffer.MemoryCopy(from + e.Offset, to + offset, e.Length, e.Length);
                    entries[count++] = e with { Offset = offset, Total = e.Length };
                    offset += e.Length;
                }
            }

            old = _storage;
            _storage = storage;
            _capacity = capacity;
            _ring = entries;
            _ringHead = 0;
            _ringCount = count;
            _tail = offset % capacity;
            _used = offset;
            _reserved = 0;
            _reservedToken = 0;
            _lastHeadChunk = -1;
            _lastTailChunk = -1;
            _awaitKeyframe = count == 0;
        }
        old.Release();
        Log.Info("Buffer", $"Арена буфера пересоздана: {capacity / (1024 * 1024)} МБ " +
                           $"{(onDisk ? "на диске" : "в памяти")}, повтор сохранён ({TotalBytes / (1024 * 1024)} МБ)");
        return true;
    }

    private static int NextPow2(int value)
    {
        int result = 1;
        while (result < value) result <<= 1;
        return result;
    }

    /// <summary>
    /// Подготовить арену к пересборке захвата. При том же формате ничего
    /// не трогаем; при смене формата старые сжатые кадры нельзя смешивать с новыми.
    /// </summary>
    public bool PrepareForCaptureRestart(bool formatCompatible, long bitrateBps, int seconds)
    {
        if (formatCompatible) return true;
        Clear();
        return false;
    }

    public void Add(EncodedFrame frame)
    {
        bool needKeyframe = false;
        lock (_sync)
        {
            if (_capacity == 0 || _storage is null) return;              // арена ещё не задана
            if ((_ringCount == 0 || _awaitKeyframe) && !frame.IsKeyframe) return; // начинаем только с keyframe
            if (frame.Length > ChunkBytes || frame.Length > _capacity / 4)
            {
                if (!_warnedHugeFrame)
                {
                    _warnedHugeFrame = true;
                    Log.Warn("Buffer", $"Кадр {frame.Length / (1024 * 1024)} МБ не влезает в арену — пропущен");
                }
                needKeyframe = DropLocked();
                goto done;
            }

            // Кадр не переходит через конец арены: если до конца не хватает, остаток
            // пропускаем и считаем довеском к этому кадру.
            int pad = _tail + frame.Length <= _capacity ? 0 : (int)(_capacity - _tail);
            int need = pad + frame.Length;

            // Пока снимок не отпущен, вытеснять НЕЛЬЗЯ: его кадры лежат в голове
            // кольца, и освобождение места за ними дало бы хвосту записи затереть
            // клип, который в этот момент пишется в файл.
            bool freedSpace = false;
            while (_reserved == 0 && _used + need > _capacity && _ringCount > 0)
            {
                EvictFirst();
                freedSpace = true;
            }
            // Голова могла остаться посреди GOP — доснимаем до ближайшего keyframe,
            // но не опустошаем буфер целиком (случай сломанного GOP).
            if (freedSpace)
                while (_ringCount > 1 && !At(0).IsKeyframe) EvictFirst();
            if (_used + need > _capacity)
            {
                // Место не освободилось: почти вся арена занята снимком, который
                // пишется в файл. Кадр теряем — и дальше ждём ключевой (см. _awaitKeyframe).
                if (!_warnedNoRoom)
                {
                    _warnedNoRoom = true;
                    Log.Warn("Buffer", "В арене нет места под кадр — не хватило запаса на время сохранения");
                }
                needKeyframe = DropLocked();
                goto done;
            }

            long start = (_tail + pad) % _capacity;
            EnsureChunks(start, frame.Length);
            unsafe
            {
                frame.Data.AsSpan(frame.Offset, frame.Length)
                     .CopyTo(new Span<byte>((byte*)_storage.Base + start, frame.Length));
            }

            _tail = (start + frame.Length) % _capacity;
            _used += need;
            Push(new Entry(start, frame.Length, need, frame.PtsTicks, frame.Dts, frame.DurationTicks, frame.IsKeyframe));
            if (frame.IsKeyframe) _awaitKeyframe = false;

            EvictByTime(frame.Dts);
            AfterMove();
        }
    done:
        if (needKeyframe) KeyframeNeeded?.Invoke();
    }

    /// <summary>Кадр потерян: дальше до ключевого не принимаем. true — надо попросить ключевой.</summary>
    private bool DropLocked()
    {
        Interlocked.Increment(ref _droppedFrames);
        if (_ringCount == 0) return true;
        bool first = !_awaitKeyframe;
        _awaitKeyframe = true;
        return first;
    }

    private void Push(Entry entry)
    {
        if (_ringCount == _ring.Length)
        {
            var bigger = new Entry[_ring.Length * 2];
            for (int i = 0; i < _ringCount; i++) bigger[i] = At(i);
            _ring = bigger;
            _ringHead = 0;
        }
        _ring[(_ringHead + _ringCount) & (_ring.Length - 1)] = entry;
        _ringCount++;
    }

    private void EvictFirst()
    {
        _used -= At(0).Total;
        _ringHead = (_ringHead + 1) & (_ring.Length - 1);
        _ringCount--;
    }

    /// <summary>Вытеснение по времени: держим (MaxDuration + 1 GOP), режем по keyframe.</summary>
    private void EvictByTime(long newest)
    {
        // Пока снимок держит голову кольца, освобождать место нельзя — см. Add.
        if (_reserved > 0 || _ringCount == 0) return;

        // Ищем самый поздний keyframe, который всё ещё покрывает MaxDuration,
        // и удаляем всё до него.
        int cutTo = -1;
        for (int i = 0; i < _ringCount; i++)
        {
            ref Entry e = ref At(i);
            if (e.IsKeyframe && newest - e.DtsTicks >= MaxDurationTicks)
                cutTo = i;
            else if (newest - e.DtsTicks < MaxDurationTicks)
                break;
        }

        // cutTo == 0 означает «резать нечего»: подходящий keyframe и так в голове.
        // Такой случай обязан проваливаться в страховку ниже — иначе при
        // единственном keyframe в начале (сломанный GOP) буфер рос бесконечно.
        if (cutTo > 0)
        {
            for (int i = 0; i < cutTo; i++) EvictFirst();
        }
        else if (newest - At(0).DtsTicks > MaxDurationTicks + KeyframeSafetyTicks)
        {
            // Страховка: энкодер не выдаёт регулярные keyframe (сломанный GOP).
            if (!_warnedNoKeyframes)
            {
                _warnedNoKeyframes = true;
                Log.Warn("Buffer", "Энкодер не выдаёт регулярные keyframe — вытеснение по времени");
            }
            while (_ringCount > 0 && newest - At(0).DtsTicks > MaxDurationTicks)
                EvictFirst();
        }
    }

    // ---------------- Физическая память ----------------

    private void EnsureChunks(long start, int length)
    {
        int first = (int)(start / ChunkBytes);
        int last = (int)((start + length - 1) / ChunkBytes);
        for (int i = first; i <= last; i++) _storage!.EnsureChunk(i);
    }

    /// <summary>Голова или хвост перешли в другой блок: отпустить пустые, запечатать дописанные, подготовить следующий.</summary>
    private void AfterMove()
    {
        int tailChunk = (int)(_tail / ChunkBytes);
        if (tailChunk != _lastTailChunk)
        {
            // Хвост ушёл вперёд: блок позади дописан. На диске его пора отпустить из памяти.
            if (_lastTailChunk >= 0) _storage!.Seal(_lastTailChunk);
            _lastTailChunk = tailChunk;
            PrepareAhead((tailChunk + 1) % _storage!.ChunkCount);
        }

        int headChunk = HeadChunk();
        if (headChunk != _lastHeadChunk)
        {
            _lastHeadChunk = headChunk;
            ReleaseUnusedChunks();
        }
    }

    private int HeadChunk() => (int)(HeadOffset() / ChunkBytes);

    /// <summary>Смещение самого старого живого байта: начало снимка или первого кадра.</summary>
    private long HeadOffset()
    {
        if (_reserved > 0) return _reservedStart;
        return _ringCount > 0 ? At(0).Offset : _tail;
    }

    /// <summary>
    /// Вернуть системе блоки, в которых не осталось живых кадров.
    ///
    /// Арена рассчитана на худший случай — заданный битрейт целиком. На простой
    /// картинке энкодер тратит куда меньше, и без этого блоки занимались бы один за
    /// другим по мере обхода кольца: 110 МБ данных и гигабайт занятой памяти.
    /// Живой участок — от головы до хвоста плюс следующий блок, который готовится
    /// заранее.
    /// </summary>
    private void ReleaseUnusedChunks()
    {
        if (_storage is null || _storage.OnDisk) return;

        int count = _storage.ChunkCount;
        int tailChunk = (int)(_tail / ChunkBytes);
        int nextChunk = (tailChunk + 1) % count;
        long headOffset = HeadOffset();
        int headChunk = (int)(headOffset / ChunkBytes);
        bool empty = _used == 0;
        // Сравниваем СМЕЩЕНИЯ, а не номера блоков: голова и хвост могут стоять в
        // одном блоке и при почти пустом кольце, и при полностью заполненном (хвост
        // обошёл арену и подошёл к голове сзади). Во втором случае живо всё, и
        // вернуть системе блок с живыми кадрами — это чтение освобождённой памяти
        // при сохранении, то есть падение процесса.
        bool wraps = !empty && headOffset >= _tail;
        for (int i = 0; i < count; i++)
        {
            if (i == tailChunk || i == nextChunk) continue;
            bool live = !empty && (wraps
                ? i >= headChunk || i <= tailChunk     // живой участок идёт через край кольца
                : i >= headChunk && i <= tailChunk);
            if (!live) _storage.ReleaseChunk(i);
        }
    }

    /// <summary>
    /// Выдать память под блок В ФОНЕ.
    ///
    /// Add вызывается из потока, который забирает сжатые кадры у энкодера, и обязан
    /// возвращаться мгновенно. Выдача 16 МБ и первое касание их страниц — это
    /// тысячи страничных исключений; на горячем пути они давали бы паузу, за
    /// которую энкодер остаётся необслуженным.
    /// </summary>
    private void PrepareAhead(int index)
    {
        ArenaStorage? storage;
        lock (_sync) storage = _storage;
        if (storage is null || storage.OnDisk) return;
        if (Interlocked.Exchange(ref _preparing, 1) == 1) return;

        try { storage.AddRef(); }
        catch (ObjectDisposedException) { Interlocked.Exchange(ref _preparing, 0); return; }

        Task.Run(() =>
        {
            try { storage.Prepare(index); }
            catch (Exception ex) { Log.Warn("Buffer", $"Блок арены не подготовлен: {ex.Message}"); }
            finally
            {
                storage.Release();
                Interlocked.Exchange(ref _preparing, 0);
            }
        });
    }

    // ---------------- Снимок ----------------

    /// <summary>
    /// Снимок последних wantedTicks (с ближайшего keyframe не позже newest - wantedTicks)
    /// С ОЧИСТКОЙ буфера: кольцо обнуляется, дальше буфер копит заново.
    ///
    /// Данные НЕ копируются — кадры снимка остаются лежать на своих местах и
    /// помечаются занятыми, пока вызывающий не вернёт их через
    /// <see cref="ReleaseSnapshot"/>. Запись тем временем идёт в свободную часть
    /// арены (под это заложен SaveHeadroomBytes). Сам снимок держит арену живой до
    /// своего Dispose.
    /// </summary>
    public VideoSnapshot TakeSnapshot(long wantedTicks, out long token)
    {
        bool askKeyframe = false;
        VideoSnapshot result;
        lock (_sync)
        {
            token = 0;
            if (_ringCount == 0 || _storage is null) return VideoSnapshot.Empty;

            long newest = At(_ringCount - 1).DtsTicks;
            long from = newest - wantedTicks;

            // Последний keyframe с dts <= from
            int start = -1;
            for (int i = 0; i < _ringCount && At(i).DtsTicks <= from; i++)
                if (At(i).IsKeyframe) start = i;

            // Такого нет — берём САМЫЙ РАННИЙ keyframe, даже если клип выйдет короче.
            // Начинать с P-кадра нельзя: он ссылается на уже выброшенный ключевой.
            if (start < 0) start = FirstKeyframeIndex();
            if (start < 0)
            {
                Log.Warn("Buffer", "В буфере нет ни одного ключевого кадра — сохранять нечего");
                return VideoSnapshot.Empty;
            }

            var frames = new BufferedFrame[_ringCount - start];
            long reserved = 0;
            IntPtr baseAddress = _storage.Base;
            for (int i = start; i < _ringCount; i++)
            {
                ref Entry e = ref At(i);
                reserved += e.Total;
                frames[i - start] = new BufferedFrame(baseAddress + (nint)e.Offset, e.Length,
                                                      e.PtsTicks, e.DurationTicks, e.IsKeyframe, e.DtsTicks);
            }
            _storage.AddRef();
            result = new VideoSnapshot(_storage, frames);

            // Сохранение завершает текущий replay: шкала сразу начинается с нуля.
            // Байты снимка остаются зарезервированы в той же арене.
            _reservedStart = At(start).Offset;
            _ringHead = 0;
            _ringCount = 0;
            _used = reserved;
            _reserved = reserved;
            _reservedToken = token = _nextToken++;
            _lastHeadChunk = -1;
            // Новый replay начнётся только с ключевого кадра, а до штатного может
            // быть две секунды. Просим его сразу, чтобы не терять начало.
            askKeyframe = true;
        }
        if (askKeyframe) KeyframeNeeded?.Invoke();
        return result;
    }

    /// <summary>
    /// Вернуть буферу место, занятое снимком: файл записан, кадры больше не нужны.
    /// Чужой или повторный токен игнорируется.
    /// </summary>
    public void ReleaseSnapshot(long token)
    {
        lock (_sync)
        {
            if (token == 0 || token != _reservedToken) return;

            _used = Math.Max(0, _used - _reserved);
            _reserved = 0;
            _reservedStart = 0;
            _reservedToken = 0;
            _lastHeadChunk = -1;
            ReleaseUnusedChunks();
        }
    }

    /// <summary>
    /// Очистить буфер, сохранив ёмкость. Под новые кадры берётся свежая арена того
    /// же размера, а старая освобождается, когда её отпустит последний снимок:
    /// идущее сохранение читает её прямо сейчас.
    /// </summary>
    public void Clear()
    {
        ArenaStorage? old;
        lock (_sync)
        {
            old = _storage;
            if (old is null) { ResetLocked(); return; }
            _storage = old.OnDisk
                ? CreateSameDisk(old)
                : new NativeArenaStorage(_capacity);
            ResetLocked();
        }
        old.Release();
    }

    private ArenaStorage CreateSameDisk(ArenaStorage old)
    {
        try { return new FileArenaStorage(DiskDirectory, _capacity); }
        catch (Exception ex)
        {
            Log.Warn("Buffer", $"Новый файл буфера не создан ({ex.Message}) — беру оперативную память");
            return new NativeArenaStorage(Math.Min(_capacity, MaximumCapacityBytes));
        }
    }

    /// <summary>Папка файла буфера на диске.</summary>
    public static string DiskDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura", "ReplayBuffer");

    /// <summary>Отпустить арену целиком: конвейер выключен, память возвращается системе.</summary>
    public void Release()
    {
        ArenaStorage? old;
        lock (_sync)
        {
            old = _storage;
            _storage = null;
            _capacity = 0;
            ResetLocked();
        }
        old?.Release();
    }

    private void ResetLocked()
    {
        _ringHead = 0;
        _ringCount = 0;
        _used = 0;
        _tail = 0;
        _reserved = 0;
        _reservedStart = 0;
        _reservedToken = 0;
        _lastHeadChunk = -1;
        _lastTailChunk = -1;
        _awaitKeyframe = false;
        if (_capacity > 0 && _storage is not null && _capacity != _storage.Capacity)
            _capacity = _storage.Capacity;
    }
}
