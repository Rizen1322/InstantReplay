using System.Buffers;

namespace InstantReplay.Core.Buffering;

/// <summary>
/// Один сжатый видеокадр в RAM. ptsTicks — QPC-время, 100-нс единицы.
/// Data берётся из ArrayPool и может быть ДЛИННЕЕ полезных данных — валидны
/// первые Length байт. Пул устраняет 60 аллокаций ~94 КБ/с (порог LOH!),
/// GC-паузы которых давали фризы записи в играх.
/// </summary>
public readonly record struct EncodedFrame(byte[] Data, int Length, long PtsTicks, long DurationTicks, bool IsKeyframe);

/// <summary>Блок аудио 10 мс: две дорожки float interleaved stereo 48 кГц (по 960 сэмплов).</summary>
public readonly record struct AudioBlock(float[] Game, float[] Mic, long PtsTicks);

/// <summary>
/// Кольцевой буфер сжатого видео в оперативной памяти.
///
/// Грабли из практики: вытеснять надо ПО ВРЕМЕНИ, а не только по объёму,
/// иначе UI показывает "в буфере 5 минут", а реально сохранится меньше.
/// Здесь: держим кадры за (MaxDuration + 1 GOP), при вытеснении режем
/// строго по ключевому кадру, чтобы буфер всегда начинался с keyframe —
/// иначе сохранённый MP4 первые секунды будет "кашей".
/// </summary>
public sealed class ReplayVideoBuffer
{
    private readonly LinkedList<EncodedFrame> _frames = new();
    private readonly object _sync = new();

    public long MaxDurationTicks { get; set; }
    public long TotalBytes { get; private set; }

    /// <summary>Реальная длительность буфера от первого keyframe до последнего кадра, тики.</summary>
    public long BufferedDurationTicks
    {
        get { lock (_sync) return _frames.Count == 0 ? 0 : _frames.Last!.Value.PtsTicks - _frames.First!.Value.PtsTicks; }
    }

    public void Add(EncodedFrame frame)
    {
        lock (_sync)
        {
            // Первый кадр буфера обязан быть keyframe (кадр не возвращаем в пул —
            // его может ещё писать ManualRecorder; редкий случай, соберёт GC)
            if (_frames.Count == 0 && !frame.IsKeyframe) return;

            _frames.AddLast(frame);
            TotalBytes += frame.Length;

            // Вытеснение по времени: ищем самый поздний keyframe, который всё ещё
            // покрывает MaxDuration, и удаляем всё до него.
            long newest = frame.PtsTicks;
            var node = _frames.First;
            LinkedListNode<EncodedFrame>? cutTo = null;
            while (node is not null)
            {
                if (node.Value.IsKeyframe && newest - node.Value.PtsTicks >= MaxDurationTicks)
                    cutTo = node; // кандидат: keyframe, дальше которого ещё есть полная длительность
                else if (newest - node.Value.PtsTicks < MaxDurationTicks)
                    break;
                node = node.Next;
            }
            if (cutTo is not null)
            {
                // Оставляем начиная с ПОСЛЕДНЕГО keyframe, дающего >= MaxDuration
                while (_frames.First is not null && !ReferenceEquals(_frames.First, cutTo))
                    EvictFirst();
            }
            else if (newest - _frames.First!.Value.PtsTicks > MaxDurationTicks + KeyframeSafetyTicks)
            {
                // Страховка: энкодер не выдаёт регулярные keyframe (сломанный GOP) —
                // без неё буфер рос бы бесконечно и съедал всю RAM. Режем по времени,
                // начало клипа станет валидным с ближайшего keyframe при сохранении.
                if (!_warnedNoKeyframes)
                {
                    _warnedNoKeyframes = true;
                    Logging.Log.Warn("Buffer", "Энкодер не выдаёт регулярные keyframe — вытеснение по времени");
                }
                while (_frames.First is not null &&
                       newest - _frames.First.Value.PtsTicks > MaxDurationTicks)
                    EvictFirst();
            }
        }
    }

    private void EvictFirst()
    {
        var f = _frames.First!.Value;
        TotalBytes -= f.Length;
        _frames.RemoveFirst();
        ArrayPool<byte>.Shared.Return(f.Data);
    }

    private const long KeyframeSafetyTicks = 100_000_000; // 10 сек сверх лимита
    private bool _warnedNoKeyframes;

    /// <summary>
    /// Снимок последних wantedTicks (с ближайшего keyframe не позже newest - wantedTicks)
    /// С ОЧИСТКОЙ буфера: владение массивами кадров снимка переходит вызывающему —
    /// после записи файла он ОБЯЗАН вернуть их через ReturnToPool. Кадры до точки
    /// снимка возвращаются в пул здесь же.
    /// </summary>
    public List<EncodedFrame> SnapshotAndClear(long wantedTicks)
    {
        lock (_sync)
        {
            var result = new List<EncodedFrame>(_frames.Count);
            if (_frames.Count == 0) return result;

            long newest = _frames.Last!.Value.PtsTicks;
            long from = newest - wantedTicks;

            // Ищем последний keyframe с pts <= from (или самый первый кадр)
            LinkedListNode<EncodedFrame>? start = _frames.First;
            for (var n = _frames.First; n is not null && n.Value.PtsTicks <= from; n = n.Next)
                if (n.Value.IsKeyframe) start = n;

            for (var n = _frames.First; n is not null; n = n.Next)
            {
                if (ReferenceEquals(n, start)) break;
                ArrayPool<byte>.Shared.Return(n.Value.Data); // не попали в снимок
            }
            for (var n = start; n is not null; n = n.Next)
                result.Add(n.Value);

            _frames.Clear();
            TotalBytes = 0;
            return result;
        }
    }

    /// <summary>Вернуть массивы снимка в пул (после завершения записи файла).</summary>
    public static void ReturnToPool(List<EncodedFrame> frames)
    {
        foreach (var f in frames) ArrayPool<byte>.Shared.Return(f.Data);
        frames.Clear();
    }

    public void Clear()
    {
        lock (_sync)
        {
            for (var n = _frames.First; n is not null; n = n.Next)
                ArrayPool<byte>.Shared.Return(n.Value.Data);
            _frames.Clear();
            TotalBytes = 0;
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
