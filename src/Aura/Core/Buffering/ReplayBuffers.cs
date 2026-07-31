using Aura.Core.Logging;

namespace Aura.Core.Buffering;

/// <summary>
/// Один сжатый видеокадр. Данные лежат НЕ в собственном массиве, а куском чужого
/// буфера: валидны байты [Offset, Offset + Length).
///
/// Владение зависит от того, откуда кадр пришёл:
/// • из события энкодера (<see cref="Encoding.VideoEncoder.FrameEncoded"/>) — буфер
///   живёт только на время вызова, подписчик ОБЯЗАН скопировать данные себе;
/// • из снимка кольцевого буфера — данные живут, пока жив список снимка.
/// </summary>
public readonly record struct EncodedFrame(
    byte[] Data, int Offset, int Length, long PtsTicks, long DurationTicks, bool IsKeyframe);

/// <summary>Блок аудио 10 мс: две дорожки float interleaved stereo 48 кГц (по 960 сэмплов).</summary>
public readonly record struct AudioBlock(float[] Game, float[] Mic, long PtsTicks);

/// <summary>
/// Кольцевой буфер сжатого видео в оперативной памяти.
///
/// УСТРОЙСТВО: арена из блоков по 64 МБ, кадры лежат в них вплотную друг за другом,
/// а список хранит только смещения. Раньше на каждый кадр брался отдельный массив
/// из ArrayPool, и это стоило дорого: пул выдаёт куски, округлённые до степени
/// двойки (кадр 90 КБ занимал 128 КБ), а размеры кадров всё время разные, поэтому
/// куча больших объектов расползалась. В замерах на 909 МБ полезных данных
/// приходилось 1465 МБ кучи и 2359 МБ памяти процесса, и цифра ползла вверх со
/// временем. С ареной расход равен объёму данных и не растёт вовсе: блоки
/// одинаковые, освобождённый блок переиспользуется целиком.
///
/// Почему блоки, а не один массив: длина byte[] ограничена примерно 2 ГБ, а
/// настройки допускают 30 минут при 80 Мбит/с. Блоки выделяются лениво — по мере
/// того, как запись до них доходит.
///
/// Грабли из практики: вытеснять надо ПО ВРЕМЕНИ, а не только по объёму,
/// иначе UI показывает "в буфере 5 минут", а реально сохранится меньше.
/// Здесь: держим кадры за (MaxDuration + 1 GOP), при вытеснении режем
/// строго по ключевому кадру, чтобы буфер всегда начинался с keyframe —
/// иначе сохранённый MP4 первые секунды будет "кашей".
/// </summary>
public sealed class ReplayVideoBuffer
{
    /// <summary>Размер блока арены. Кадр никогда не лежит на границе двух блоков.</summary>
    private const int ChunkBytes = 64 << 20;

    /// <summary>
    /// Потолок арены. Настройки допускают 30 минут при 80 Мбит/с — это 18 ГБ,
    /// столько никто не даст. При превышении буфер будет короче заказанного,
    /// о чём пишем в лог.
    /// </summary>
    private const long MaxCapacityBytes = 4L << 30;

    /// <summary>Запас сверх заказанной длительности: один GOP плюс страховка вытеснения.</summary>
    private const int SlackSeconds = 15;

    /// <summary>
    /// Запас под запись файла. Пока сохраняется клип, его кадры остаются в арене
    /// (см. <see cref="ReleaseSnapshot"/>), а запись продолжается — новым кадрам
    /// нужно куда-то ложиться. Файл пишется около полутора секунд, то есть при
    /// 40 Мбит/с ему хватило бы 8 МБ; блока с запасом достаточно.
    /// </summary>
    private const long SaveHeadroomBytes = ChunkBytes;

    private const long KeyframeSafetyTicks = 100_000_000; // 10 сек сверх лимита

    /// <summary>Кадр в арене: где лежит, сколько занимает вместе с довеском до границы блока.</summary>
    private readonly record struct Entry(
        int Chunk, int Offset, int Length, int Total, long PtsTicks, long DurationTicks, bool IsKeyframe);

    private readonly LinkedList<Entry> _entries = new();
    private readonly object _sync = new();

    private byte[]?[] _chunks = [];
    private long _capacity;
    private long _tail;   // позиция записи внутри арены, [0, _capacity)
    private long _used;   // занято байт, включая довески до границ блоков

    // Кадры, отданные снимку: они уже не в списке буфера, но их байты в арене
    // трогать нельзя, пока файл не записан. Токен защищает от освобождения чужого
    // снимка — например, если между снимком и записью конвейер перезапустили.
    private long _reserved;
    private long _reservedToken;
    private long _nextToken = 1;

    /// <summary>В каком блоке была голова буфера — чтобы не пересчитывать живые блоки на каждый кадр.</summary>
    private int _lastHeadChunk = -1;

    /// <summary>
    /// Отложенные блоки: кольцо отпускает их сзади и через некоторое время снова
    /// доходит до них спереди, поэтому блок переиспользуется, а не выбрасывается.
    ///
    /// Без этого получалась мясорубка: освобождённый блок становился мусором, на его
    /// месте выделялся новый на 64 МБ, и в режиме низких задержек сборщик доходил до
    /// них редко — в замерах буфер держал ровные 331 МБ, а куча пухла с 543 до 822 МБ.
    /// Двух запасных хватает: кольцо потребляет их не быстрее, чем отпускает.
    /// </summary>
    private readonly Stack<byte[]> _spare = new();
    private const int MaxSpareChunks = 2;
    private int _prefetching;

    private bool _warnedNoKeyframes, _warnedHugeFrame, _warnedNoRoom;

    public long MaxDurationTicks { get; set; }

    /// <summary>Занято байт — столько реально держит буфер.</summary>
    public long TotalBytes { get { lock (_sync) return _used; } }

    /// <summary>Ёмкость арены в байтах (сколько максимум может занять).</summary>
    public long CapacityBytes { get { lock (_sync) return _capacity; } }

    /// <summary>Реальная длительность буфера от первого keyframe до последнего кадра, тики.</summary>
    public long BufferedDurationTicks
    {
        get
        {
            lock (_sync)
                return _entries.Count == 0 ? 0
                     : _entries.Last!.Value.PtsTicks - _entries.First!.Value.PtsTicks;
        }
    }

    /// <summary>
    /// Задать размер арены под длительность и битрейт из настроек. Вызывается при
    /// старте конвейера; сами блоки выделяются лениво, по мере заполнения.
    /// </summary>
    public void Allocate(long bitrateBps, int seconds)
    {
        long wanted = (long)(bitrateBps / 8.0 * (seconds + SlackSeconds) * 1.05) + SaveHeadroomBytes;
        long capped = Math.Clamp(wanted, ChunkBytes, MaxCapacityBytes);
        int chunks = (int)((capped + ChunkBytes - 1) / ChunkBytes);

        lock (_sync)
        {
            _entries.Clear();
            _used = 0;
            _tail = 0;
            _reserved = 0;
            _reservedToken = 0;
            _lastHeadChunk = -1;
            _spare.Clear();
            _chunks = new byte[]?[chunks];
            _capacity = (long)chunks * ChunkBytes;
            // Первый блок готовим прямо здесь: Allocate зовут при старте конвейера,
            // это не горячий путь. Второй подготовит фон.
            _spare.Push(GC.AllocateUninitializedArray<byte>(ChunkBytes));
            EnsureSpare();
        }

        Log.Info("Buffer", $"Арена буфера: {chunks} × 64 МБ = {_capacity / (1024 * 1024)} МБ " +
                           $"на {seconds} сек при {bitrateBps / 1_000_000} Мбит/с");
        if (capped < wanted)
            Log.Warn("Buffer", $"Для {seconds} сек при {bitrateBps / 1_000_000} Мбит/с нужно " +
                               $"{wanted / (1024 * 1024)} МБ — ограничено {MaxCapacityBytes / (1024 * 1024)} МБ, " +
                               "буфер будет короче заданного");
    }

    public void Add(EncodedFrame frame)
    {
        lock (_sync)
        {
            if (_capacity == 0) return;                                  // арена ещё не задана
            if (_entries.Count == 0 && !frame.IsKeyframe) return;        // начинаем только с keyframe
            if (frame.Length > ChunkBytes)
            {
                if (!_warnedHugeFrame)
                {
                    _warnedHugeFrame = true;
                    Log.Warn("Buffer", $"Кадр {frame.Length / (1024 * 1024)} МБ не влезает в блок арены — пропущен");
                }
                return;
            }

            // Кадр не должен лежать на границе блоков: если до конца текущего не
            // хватает, остаток блока пропускаем и считаем довеском к этому кадру.
            int offsetInChunk = (int)(_tail % ChunkBytes);
            int pad = offsetInChunk + frame.Length <= ChunkBytes ? 0 : ChunkBytes - offsetInChunk;
            int need = pad + frame.Length;

            // Пока снимок не отпущен, вытеснять НЕЛЬЗЯ. Его кадры лежат в голове
            // кольца, и освобождение места за ними образовало бы дыру: хвост записи
            // пошёл бы дальше по кругу и в итоге затёр бы клип, который в этот момент
            // пишется в файл. Место на время сохранения даёт SaveHeadroomBytes,
            // а если и его не хватит — лучше потерять кадр, чем испортить клип.
            bool freedSpace = false;
            while (_reserved == 0 && _used + need > _capacity && _entries.Count > 0)
            {
                EvictFirst();
                freedSpace = true;
            }
            // Освобождали место — голова могла остаться посреди GOP. Доснимаем до
            // ближайшего keyframe, но не опустошаем буфер целиком (случай сломанного
            // GOP, когда keyframe'ов нет вовсе).
            if (freedSpace)
                while (_entries.Count > 1 && !_entries.First!.Value.IsKeyframe) EvictFirst();
            if (_used + need > _capacity)
            {
                // Место всё ещё не освободилось — такое возможно только пока идёт
                // сохранение и почти вся арена занята его кадрами. На этот случай
                // в арене есть запас (SaveHeadroomBytes), так что сюда не попадаем.
                if (!_warnedNoRoom)
                {
                    _warnedNoRoom = true;
                    Log.Warn("Buffer", "В арене нет места под кадр — не хватило запаса на время сохранения");
                }
                return;
            }

            long start = (_tail + pad) % _capacity;
            int chunkIndex = (int)(start / ChunkBytes);
            int offset = (int)(start % ChunkBytes);
            var chunk = _chunks[chunkIndex] ??= TakeChunk();
            Buffer.BlockCopy(frame.Data, frame.Offset, chunk, offset, frame.Length);

            _tail = (start + frame.Length) % _capacity;
            _used += need;
            _entries.AddLast(new Entry(chunkIndex, offset, frame.Length, need,
                                       frame.PtsTicks, frame.DurationTicks, frame.IsKeyframe));

            EvictByTime(frame.PtsTicks);

            // Голова сдвинулась в другой блок — значит блоки позади освободились.
            int headChunk = _entries.Count > 0 ? _entries.First!.Value.Chunk : chunkIndex;
            if (headChunk != _lastHeadChunk)
            {
                _lastHeadChunk = headChunk;
                ReleaseUnusedChunks();
            }

            EnsureSpare(); // следующий блок готовится в фоне, а не здесь
        }
    }

    /// <summary>Вытеснение по времени: держим (MaxDuration + 1 GOP), режем по keyframe.</summary>
    private void EvictByTime(long newest)
    {
        // Пока снимок держит голову кольца, освобождать место нельзя — см. Add.
        if (_reserved > 0) return;

        // Ищем самый поздний keyframe, который всё ещё покрывает MaxDuration,
        // и удаляем всё до него.
        var node = _entries.First;
        LinkedListNode<Entry>? cutTo = null;
        while (node is not null)
        {
            if (node.Value.IsKeyframe && newest - node.Value.PtsTicks >= MaxDurationTicks)
                cutTo = node; // кандидат: keyframe, дальше которого ещё есть полная длительность
            else if (newest - node.Value.PtsTicks < MaxDurationTicks)
                break;
            node = node.Next;
        }

        // cutTo == первый кадр означает «резать нечего»: подходящий keyframe и так
        // в голове буфера. Такой случай обязан проваливаться в страховку ниже —
        // иначе при единственном keyframe в начале (сломанный GOP) буфер рос
        // бесконечно: вытеснять нечего, а страховка не срабатывала.
        if (cutTo is not null && !ReferenceEquals(cutTo, _entries.First))
        {
            while (_entries.First is not null && !ReferenceEquals(_entries.First, cutTo))
                EvictFirst();
        }
        else if (newest - _entries.First!.Value.PtsTicks > MaxDurationTicks + KeyframeSafetyTicks)
        {
            // Страховка: энкодер не выдаёт регулярные keyframe (сломанный GOP) —
            // без неё буфер держал бы лишние секунды. Режем по времени, начало клипа
            // станет валидным с ближайшего keyframe при сохранении.
            if (!_warnedNoKeyframes)
            {
                _warnedNoKeyframes = true;
                Log.Warn("Buffer", "Энкодер не выдаёт регулярные keyframe — вытеснение по времени");
            }
            while (_entries.First is not null &&
                   newest - _entries.First.Value.PtsTicks > MaxDurationTicks)
                EvictFirst();
        }
    }

    private void EvictFirst()
    {
        _used -= _entries.First!.Value.Total;
        _entries.RemoveFirst();
    }

    /// <summary>
    /// Отпустить блоки, в которых не осталось живых кадров.
    ///
    /// Арена рассчитана на худший случай — заданный битрейт целиком. На простой
    /// картинке энкодер тратит куда меньше (на неподвижном рабочем столе — 5 Мбит/с
    /// вместо 40), и без этого блоки всё равно занимались бы один за другим по мере
    /// обхода кольца: 110 МБ данных и гигабайт занятой памяти. Теперь занято ровно
    /// столько блоков, сколько нужно под живые кадры; когда запись доходит до
    /// отпущенного блока, он выделяется заново.
    /// </summary>
    private void ReleaseUnusedChunks()
    {
        if (_reserved > 0) return; // идёт сохранение — арену не трогаем

        int tailChunk = (int)(_tail / ChunkBytes);
        int headChunk = _entries.Count > 0 ? _entries.First!.Value.Chunk : tailChunk;
        for (int i = 0; i < _chunks.Length; i++)
        {
            if (_chunks[i] is null) continue;
            bool live = headChunk <= tailChunk
                ? i >= headChunk && i <= tailChunk
                : i >= headChunk || i <= tailChunk;   // живой участок идёт через край кольца
            if (live) continue;

            // Блок пойдёт в запас и достанется следующему обороту кольца; лишние
            // отдаём сборщику — держать всю арену впустую незачем.
            if (_spare.Count < MaxSpareChunks) _spare.Push(_chunks[i]!);
            _chunks[i] = null;
        }
    }

    /// <summary>
    /// Блок под запись — из запаса. Блоки НЕ закрепляем: закреплённая куча не
    /// уплотняется, и освобождённые сегменты остаются за процессом даже после сжатия.
    /// </summary>
    private byte[] TakeChunk() =>
        _spare.Count > 0 ? _spare.Pop() : GC.AllocateUninitializedArray<byte>(ChunkBytes);

    /// <summary>
    /// Держать готовый блок про запас, выделяя его В ФОНЕ.
    ///
    /// Add вызывается из потока, который забирает сжатые кадры у энкодера, и обязан
    /// возвращаться мгновенно. Выделение блока на 64 МБ прямо в нём коммитит страницы
    /// и может дёрнуть сборку мусора — это десятки миллисекунд, за которые энкодер
    /// остаётся необслуженным. В замерах так и выглядело: все стадии по нулям, а
    /// очередь энкодера забита под потолок, пейсер молчит сотни раз, кадры теряются
    /// и в игре видны микрофризы.
    /// </summary>
    private void EnsureSpare()
    {
        if (_spare.Count > 0) return;
        if (Interlocked.Exchange(ref _prefetching, 1) == 1) return; // уже готовим

        Task.Run(() =>
        {
            try
            {
                var chunk = GC.AllocateUninitializedArray<byte>(ChunkBytes);
                lock (_sync)
                    if (_spare.Count < MaxSpareChunks) _spare.Push(chunk);
            }
            catch (Exception ex) { Log.Warn("Buffer", $"Блок арены не выделился: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _prefetching, 0); }
        });
    }

    /// <summary>
    /// Снимок последних wantedTicks (с ближайшего keyframe не позже newest - wantedTicks)
    /// С ОЧИСТКОЙ буфера: список кадров обнуляется, дальше буфер копит заново.
    ///
    /// Данные НЕ копируются и арена НЕ перевыделяется — кадры снимка остаются лежать
    /// на своих местах и помечаются занятыми, пока вызывающий не вернёт их через
    /// <see cref="ReleaseSnapshot"/>. Запись тем временем идёт в свободную часть
    /// арены (под это заложен SaveHeadroomBytes).
    ///
    /// Раньше снимок забирал блоки себе, а буфер выделял новые: на время записи
    /// файла процесс держал ДВЕ арены, и в замерах память подскакивала с 1.3 до
    /// 2.1 ГБ и оставалась там.
    ///
    /// token нужен, чтобы вернуть именно этот снимок: если между снимком и концом
    /// записи конвейер перезапустили, арена уже другая и освобождать в ней нечего.
    /// </summary>
    public List<EncodedFrame> SnapshotAndClear(long wantedTicks, out long token)
    {
        lock (_sync)
        {
            token = 0;
            var result = new List<EncodedFrame>(_entries.Count);
            if (_entries.Count == 0) return result;

            long newest = _entries.Last!.Value.PtsTicks;
            long from = newest - wantedTicks;

            // Ищем последний keyframe с pts <= from (или самый первый кадр)
            LinkedListNode<Entry>? start = _entries.First;
            for (var n = _entries.First; n is not null && n.Value.PtsTicks <= from; n = n.Next)
                if (n.Value.IsKeyframe) start = n;

            long reserved = 0;
            for (var n = start; n is not null; n = n.Next)
            {
                var e = n.Value;
                reserved += e.Total;
                result.Add(new EncodedFrame(_chunks[e.Chunk]!, e.Offset, e.Length,
                                            e.PtsTicks, e.DurationTicks, e.IsKeyframe));
            }

            // Кадры до точки снимка освобождаются сразу, кадры снимка — держатся
            // занятыми до ReleaseSnapshot. Позиция записи (_tail) не трогается:
            // запись продолжится дальше по кольцу, за хвостом снимка.
            _entries.Clear();
            _used = reserved;
            _reserved = reserved;
            _reservedToken = token = _nextToken++;
            return result;
        }
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
            _used -= _reserved;
            _reserved = 0;
            _reservedToken = 0;
        }
    }

    /// <summary>Очистить буфер и отпустить блоки арены (память вернётся сборщику).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _used = 0;
            _tail = 0;
            // Незавершённый снимок остаётся жив у того, кто его держит: его кадры
            // ссылаются на блоки напрямую, поэтому данные не пропадут.
            _reserved = 0;
            _reservedToken = 0;
            _lastHeadChunk = -1;
            _spare.Clear();
            Array.Clear(_chunks);
        }
    }
}

/// <summary>
/// Кольцевой буфер аудио: 10-мс блоки, ВСЕГДА обе дорожки (игра и микрофон)
/// раздельно — как именно свести (одна дорожка / две / только игра / только мик)
/// решается в момент сохранения, а не записи.
/// </summary>
public sealed class ReplayAudioBuffer
{
    private readonly Queue<AudioBlock> _blocks = new();
    private readonly object _sync = new();

    public long MaxDurationTicks { get; set; }

    /// <summary>Сколько байт занимают накопленные блоки — для диагностики памяти.</summary>
    public long TotalBytes
    {
        get { lock (_sync) return _blocks.Count * (long)BlockBytes; }
    }

    /// <summary>
    /// Две дорожки float32 по 10 мс: 480 фреймов × 2 канала × 4 байта × 2 дорожки.
    /// Размер блока задаёт микшер (AudioMixerEngine.BlockFrames); здесь он записан
    /// числом намеренно — файл не должен тянуть за собой аудиоподсистему с NAudio,
    /// иначе его не подключить к тестам.
    /// </summary>
    private const int BlockBytes = 480 * 2 * sizeof(float) * 2;

    public void Add(AudioBlock block)
    {
        lock (_sync)
        {
            _blocks.Enqueue(block);
            while (_blocks.Count > 0 && block.PtsTicks - _blocks.Peek().PtsTicks > MaxDurationTicks + 10_000_000)
                _blocks.Dequeue();
        }
    }

    /// <summary>Блоки в интервале [fromTicks, toTicks].</summary>
    public List<AudioBlock> Snapshot(long fromTicks, long toTicks)
    {
        lock (_sync)
            return _blocks.Where(b => b.PtsTicks >= fromTicks && b.PtsTicks <= toTicks).ToList();
    }

    public void Clear() { lock (_sync) _blocks.Clear(); }
}
