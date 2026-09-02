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

/// <summary>Что писать в дорожку файла: звук игры, микрофон или их смесь.</summary>
public enum AudioTrackKind { Game, Mic, Mixed }

/// <summary>
/// Блок аудио 10 мс: две дорожки interleaved stereo 48 кГц по 960 сэмплов.
///
/// 16 бит, а не float32. Микшер считает в float (шумодав, пики, сведение), но в
/// буфере хранить вчетверо более широкий формат незачем: дальше звук всё равно
/// уходит в AAC, а 16 бит — это то, в чём поставляется любая музыка и кино.
/// Динамический диапазон 96 дБ, потолок AAC при 192 кбит/с намного ниже, так что
/// на слух разницы нет. Память буфера при этом падает вдвое: 132 МБ на три минуты
/// двух дорожек превращаются в 66 МБ.
///
/// ВЛАДЕНИЕ. Массивы принадлежат микшеру и переиспользуются от блока к блоку:
/// они валидны ТОЛЬКО на время вызова <see cref="Audio.AudioMixerEngine.BlockReady"/>,
/// подписчик обязан скопировать данные себе. Ровно тот же контракт, что у
/// <see cref="EncodedFrame"/> из энкодера, и по той же причине: раньше микшер
/// выделял два свежих массива каждые 10 мс — двести массивов в секунду, и
/// трёхминутный буфер держал их все живыми, около сорока тысяч объектов.
/// </summary>
public readonly record struct AudioBlock(short[] Game, short[] Mic, long PtsTicks)
{
    /// <summary>Записать нужную дорожку в готовый буфер, ничего не выделяя.</summary>
    public void CopyTo(AudioTrackKind kind, Span<short> dest) => Mix(Game, Mic, kind, dest);

    /// <summary>
    /// Свести дорожки прямо в приёмник.
    ///
    /// Сложение идёт через int с зажимом по границам short: сумма двух дорожек
    /// легко выходит за диапазон, а переполнение слышно как треск. Раньше сведение
    /// жило в селекторе, который выделял под результат новый массив НА КАЖДЫЙ БЛОК —
    /// в обычной записи это происходило на потоке микшера с приоритетом Highest.
    /// </summary>
    public static void Mix(ReadOnlySpan<short> game, ReadOnlySpan<short> mic,
                           AudioTrackKind kind, Span<short> dest)
    {
        switch (kind)
        {
            case AudioTrackKind.Game:
                game[..dest.Length].CopyTo(dest);
                break;
            case AudioTrackKind.Mic:
                mic[..dest.Length].CopyTo(dest);
                break;
            default:
                for (int i = 0; i < dest.Length; i++)
                    dest[i] = (short)Math.Clamp(game[i] + mic[i], short.MinValue, short.MaxValue);
                break;
        }
    }
}

/// <summary>
/// Замороженный кусок аудиобуфера: своя память, живёт столько, сколько нужно тому,
/// кто пишет файл. Дорожки лежат раздельно — как их свести, решает вызывающий.
/// </summary>
public sealed class AudioSnapshot(short[] game, short[] mic, long[] pts, int blockSamples)
{
    public static readonly AudioSnapshot Empty = new([], [], [], 0);

    /// <summary>Сэмплов в одном блоке на дорожку.</summary>
    public int BlockSamples { get; } = blockSamples;

    public int Count => pts.Length;

    public long PtsAt(int index) => pts[index];

    /// <summary>Записать блок нужной дорожкой в готовый буфер, ничего не выделяя.</summary>
    public void CopyTo(int index, AudioTrackKind kind, Span<short> dest) =>
        AudioBlock.Mix(game.AsSpan(index * BlockSamples, BlockSamples),
                       mic.AsSpan(index * BlockSamples, BlockSamples), kind, dest);
}

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
    /// <summary>
    /// Размер блока арены. Кадр никогда не лежит на границе двух блоков.
    ///
    /// 16 МБ, а не 64: арена рассчитывается на ЗАДАННЫЙ битрейт, а реальный поток
    /// часто заметно меньше (простая картинка, низкая частота захвата). Занято при
    /// этом столько блоков, сколько нужно под живые кадры, — и чем мельче блок, тем
    /// точнее это «сколько нужно». В замере на чужой машине буфер держал 146 МБ
    /// данных при арене на 832 МБ; с блоками по 64 МБ округление съедало заметную
    /// часть разницы.
    /// </summary>
    private const int ChunkBytes = 16 << 20;

    /// <summary>
    /// Потолок арены. Настройки допускают 30 минут при 80 Мбит/с — это 18 ГБ,
    /// столько никто не даст. При превышении буфер будет короче заказанного,
    /// о чём пишем в лог.
    /// </summary>
    private const long MaxCapacityBytes = 4L << 30;

    /// <summary>
    /// Запас сверх заказанной длительности: один GOP плюс страховка вытеснения.
    /// Не приватная: тем же запасом обязано жить кольцо звука, иначе клип начнётся
    /// с тишины — снимок видео стартует с keyframe РАНЬШЕ заказанной границы.
    /// </summary>
    internal const int SlackSeconds = 15;

    /// <summary>
    /// Запас под запись файла. Пока сохраняется клип, его кадры остаются в арене
    /// (см. <see cref="ReleaseSnapshot"/>), а запись продолжается — новым кадрам
    /// нужно куда-то ложиться. Файл пишется около полутора секунд, то есть при
    /// 40 Мбит/с ему хватило бы 8 МБ; 64 МБ — запас с большим избытком.
    /// </summary>
    private const long SaveHeadroomBytes = 64L << 20;

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
    private const int MaxSpareChunks = 4;
    private int _prefetching;

    private bool _warnedNoKeyframes, _warnedHugeFrame, _warnedNoRoom;

    public long MaxDurationTicks { get; set; }

    /// <summary>Занято байт — столько реально держит буфер.</summary>
    public long TotalBytes { get { lock (_sync) return _used; } }

    /// <summary>Ёмкость арены в байтах (сколько максимум может занять).</summary>
    public long CapacityBytes { get { lock (_sync) return _capacity; } }

    /// <summary>Первый ключевой кадр в буфере; null — их нет вовсе (сломанный GOP).</summary>
    private LinkedListNode<Entry>? FirstKeyframe()
    {
        for (var n = _entries.First; n is not null; n = n.Next)
            if (n.Value.IsKeyframe) return n;
        return null;
    }

    /// <summary>
    /// Сколько повтора реально СОХРАНИТСЯ, тики.
    ///
    /// Считается от первого КЛЮЧЕВОГО кадра и обрезается настройкой. Раньше здесь
    /// было «от первого кадра вообще, без ограничения» — и число получалось честным
    /// только внешне: буфер намеренно держит запас сверх заказанного (GOP плюс
    /// страховка), поэтому на экране «Обзора» при настройке 2:00 показывалось 2:05
    /// и 2:10, а после вытеснения очередной группы разом падало до 1:40. Сохранить
    /// эти лишние секунды всё равно было нельзя — снимок режется по keyframe.
    /// </summary>
    public long BufferedDurationTicks
    {
        get
        {
            lock (_sync)
            {
                if (_entries.Count == 0) return 0;
                var first = FirstKeyframe();
                if (first is null) return 0;

                long span = _entries.Last!.Value.PtsTicks - first.Value.PtsTicks;
                return MaxDurationTicks > 0 ? Math.Min(span, MaxDurationTicks) : span;
            }
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

        Log.Info("Buffer", $"Арена буфера: {chunks} × {ChunkBytes / (1024 * 1024)} МБ = {_capacity / (1024 * 1024)} МБ " +
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
    public List<EncodedFrame> TakeSnapshot(long wantedTicks, out long token)
    {
        lock (_sync)
        {
            token = 0;
            var result = new List<EncodedFrame>(_entries.Count);
            if (_entries.Count == 0) return result;

            long newest = _entries.Last!.Value.PtsTicks;
            long from = newest - wantedTicks;

            // Ищем последний keyframe с pts <= from
            LinkedListNode<Entry>? start = null;
            for (var n = _entries.First; n is not null && n.Value.PtsTicks <= from; n = n.Next)
                if (n.Value.IsKeyframe) start = n;

            // Такого keyframe нет — берём САМЫЙ РАННИЙ из имеющихся, даже если он
            // позже точки среза и клип выйдет короче заказанного.
            //
            // Здесь раньше стояло «иначе первый кадр буфера», и это был тот самый
            // баг: при сломанном GOP (вытеснение по времени срезало голову посреди
            // группы) клип начинался с P-кадра, который ссылается на уже выброшенный
            // ключевой. Файл получался длиннее настройки, а плеер первые секунды
            // показывал пустоту или отказывался играть, пока не перемотаешь вручную.
            start ??= FirstKeyframe();
            if (start is null)
            {
                Log.Warn("Buffer", "В буфере нет ни одного ключевого кадра — сохранять нечего");
                return result;
            }

            long reserved = 0;
            for (var n = start; n is not null; n = n.Next)
            {
                var e = n.Value;
                reserved += e.Total;
                result.Add(new EncodedFrame(_chunks[e.Chunk]!, e.Offset, e.Length,
                                            e.PtsTicks, e.DurationTicks, e.IsKeyframe));
            }

            // Кадры ДО точки снимка освобождаются сразу; сами кадры снимка остаются
            // и в арене, и в списке.
            //
            // ЗАЧЕМ ОСТАВЛЯТЬ. Раньше здесь стоял _entries.Clear(), то есть буфер
            // начинался с нуля и ждал следующего keyframe — до двух секунд. Два
            // повтора подряд («момент был чуть раньше, сохраню ещё раз») давали
            // второй клип длиной в зазор между нажатиями. ShadowPlay так себя не
            // ведёт, и люди справедливо считали это потерей записи.
            //
            // Блоки арены при этом держатся занятыми до ReleaseSnapshot, а позиция
            // записи (_tail) не трогается: запись идёт дальше по кольцу за хвостом
            // снимка, и вытеснение обойдёт занятое.
            while (_entries.First is not null && !ReferenceEquals(_entries.First, start))
                _entries.RemoveFirst();

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

            // _used НЕ трогаем. Раньше снимок вычищал кадры из _entries, и их байты
            // держала только резервация — снять её значило и освободить учёт. Теперь
            // кадры снимка ОСТАЮТСЯ в списке (чтобы второй повтор подряд не был
            // пустым), их байты уже посчитаны там, и повторное вычитание уводило
            // учёт в минус: «буфер видео -88 МБ» и всё дальше с каждым сохранением.
            // Эти байты спишет обычное вытеснение, когда дойдёт до этих кадров.
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
///
/// УСТРОЙСТВО: две плоские арены (по одной на дорожку) и кольцо индексов поверх них.
/// Раньше здесь лежала очередь блоков, каждый со своей парой массивов: микшер
/// выделял их двести штук в секунду, и трёхминутный буфер держал живыми под сорок
/// тысяч объектов — при том, что видеотракт ушёл на арену как раз затем, чтобы не
/// давить на сборщик во время записи. Теперь память берётся один раз при старте
/// конвейера и дальше не растёт вовсе.
/// </summary>
public sealed class ReplayAudioBuffer
{
    private readonly object _sync = new();

    private short[] _game = [];
    private short[] _mic = [];
    private long[] _pts = [];

    private int _blockSamples;
    private int _capacity;   // блоков в кольце
    private int _head;       // индекс самого старого блока
    private int _count;

    /// <summary>Блок — 10 мс, значит в секунде их сто.</summary>
    private const int BlocksPerSecond = 100;

    /// <summary>
    /// Запас сверх заказанной длительности — ТОТ ЖЕ, что у видео (SlackSeconds).
    ///
    /// ЗАЧЕМ СТОЛЬКО. Снимок видео начинается с последнего keyframe ПЕРЕД точкой
    /// среза, то есть раньше заказанной границы — на длину группы кадров, а при
    /// сорванном GOP и на все десять секунд. Здесь раньше стояла одна секунда с
    /// комментарием «как у видео», хотя у видео пятнадцать: этих первых секунд
    /// в звуке просто не было, и каждый клип начинался с тишины.
    /// </summary>
    private const long SlackTicks = ReplayVideoBuffer.SlackSeconds * 10_000_000L;

    public long MaxDurationTicks { get; set; }

    /// <summary>Сколько байт занимают накопленные блоки — для диагностики памяти.</summary>
    public long TotalBytes
    {
        get { lock (_sync) return (long)_count * _blockSamples * 2 * sizeof(short); }
    }

    /// <summary>
    /// Выделить арену под заданную длительность. Зовётся при старте конвейера.
    ///
    /// <paramref name="blockSamples"/> задаёт микшер (AudioMixerEngine.BlockSamples);
    /// параметром — намеренно: файл не должен тянуть за собой аудиоподсистему с
    /// NAudio, иначе его не подключить к тестам.
    /// </summary>
    public void Allocate(int blockSamples, int seconds)
    {
        // Арена обязана вмещать заказанное ПЛЮС тот же запас, что держит вытеснение
        // по времени, иначе кольцо выбросит начало клипа раньше, чем его попросят
        int capacity = Math.Max(BlocksPerSecond, (seconds + ReplayVideoBuffer.SlackSeconds) * BlocksPerSecond);

        lock (_sync)
        {
            _blockSamples = blockSamples;
            _capacity = capacity;
            _head = 0;
            _count = 0;
            _game = new short[(long)capacity * blockSamples];
            _mic = new short[(long)capacity * blockSamples];
            _pts = new long[capacity];
        }

        long bytes = 2L * capacity * blockSamples * sizeof(short);
        Log.Info("Buffer", $"Арена звука: {capacity} блоков × 2 дорожки = " +
                           $"{bytes / (1024 * 1024)} МБ на {seconds} сек");
    }

    public void Add(AudioBlock block)
    {
        lock (_sync)
        {
            if (_capacity == 0) return;   // арена ещё не задана

            int slot = (_head + _count) % _capacity;
            // Кольцо заполнено — новый блок ложится на место самого старого
            if (_count == _capacity) _head = (_head + 1) % _capacity;
            else _count++;

            int offset = slot * _blockSamples;
            block.Game.AsSpan(0, _blockSamples).CopyTo(_game.AsSpan(offset, _blockSamples));
            block.Mic.AsSpan(0, _blockSamples).CopyTo(_mic.AsSpan(offset, _blockSamples));
            _pts[slot] = block.PtsTicks;

            // Вытеснение по времени: держим заказанную длительность плюс запас
            while (_count > 1 && block.PtsTicks - _pts[_head] > MaxDurationTicks + SlackTicks)
            {
                _head = (_head + 1) % _capacity;
                _count--;
            }
        }
    }

    /// <summary>
    /// Блоки в интервале [fromTicks, toTicks] отдельной копией.
    ///
    /// Копия здесь обязательна: кольцо продолжает заполняться, пока файл пишется в
    /// фоне, и отдать наружу ссылку на живую арену значит дать перезаписать её прямо
    /// под писателем. Зато копия ОДНА на сохранение, а не сорок тысяч мелких
    /// объектов, живущих всё время работы буфера.
    /// </summary>
    public AudioSnapshot Snapshot(long fromTicks, long toTicks)
    {
        lock (_sync)
        {
            if (_count == 0 || _blockSamples == 0) return AudioSnapshot.Empty;

            int first = -1, last = -2;
            for (int i = 0; i < _count; i++)
            {
                long stamp = _pts[(_head + i) % _capacity];
                if (stamp < fromTicks) continue;
                if (stamp > toTicks) break;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return AudioSnapshot.Empty;

            int count = last - first + 1;
            var game = new short[count * _blockSamples];
            var mic = new short[count * _blockSamples];
            var pts = new long[count];

            for (int i = 0; i < count; i++)
            {
                int slot = (_head + first + i) % _capacity;
                _game.AsSpan(slot * _blockSamples, _blockSamples).CopyTo(game.AsSpan(i * _blockSamples));
                _mic.AsSpan(slot * _blockSamples, _blockSamples).CopyTo(mic.AsSpan(i * _blockSamples));
                pts[i] = _pts[slot];
            }
            return new AudioSnapshot(game, mic, pts, _blockSamples);
        }
    }

    /// <summary>
    /// Забыть накопленное, СОХРАНИВ арену: буфер копит заново с чистого листа.
    /// Так делается после каждого сохранения повтора — конвейер при этом работает,
    /// и отпускать под ним память нельзя.
    /// </summary>
    public void Clear() { lock (_sync) { _head = 0; _count = 0; } }

    /// <summary>Отпустить арену целиком: конвейер остановлен, память возвращается системе.</summary>
    public void Release()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
            _capacity = 0;
            _game = [];
            _mic = [];
            _pts = [];
        }
    }
}
