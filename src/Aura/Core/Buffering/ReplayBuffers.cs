using Aura.Core.Logging;

namespace Aura.Core.Buffering;

/// <summary>
/// Один сжатый видеокадр. Данные лежат НЕ в собственном массиве, а куском чужого
/// буфера: валидны байты [Offset, Offset + Length).
///
/// Из события энкодера (<see cref="Encoding.VideoEncoder.FrameEncoded"/>) буфер
/// живёт только на время вызова, подписчик ОБЯЗАН скопировать данные себе.
///
/// <paramref name="DtsTicks"/> — время декодирования. Энкодер без B-кадров его не
/// задаёт, и тогда оно совпадает со временем показа.
/// </summary>
public readonly record struct EncodedFrame(
    byte[] Data, int Offset, int Length, long PtsTicks, long DurationTicks, bool IsKeyframe,
    long DtsTicks = long.MinValue)
{
    public long Dts => DtsTicks == long.MinValue ? PtsTicks : DtsTicks;
}

/// <summary>Что писать в дорожку файла: звук игры, микрофон или их смесь.</summary>
public enum AudioTrackKind { Game, Mic, Mixed }

/// <summary>Кадры AAC одной дорожки из снимка буфера.</summary>
public sealed record AudioTrackSnapshot(AudioTrackKind Kind, IReadOnlyList<byte[]> Frames, long FirstPtsTicks);

/// <summary>Звук клипа: готовые кадры AAC по дорожкам.</summary>
public sealed class AudioSnapshot
{
    public static readonly AudioSnapshot Empty = new([]);

    private readonly Dictionary<AudioTrackKind, AudioTrackSnapshot> _tracks;

    public AudioSnapshot(IEnumerable<AudioTrackSnapshot> tracks) =>
        _tracks = tracks.ToDictionary(t => t.Kind);

    public AudioTrackSnapshot? this[AudioTrackKind kind] => _tracks.GetValueOrDefault(kind);

    public bool IsEmpty => _tracks.Values.All(t => t.Frames.Count == 0);

    public int FrameCount => _tracks.Values.Sum(t => t.Frames.Count);
}

/// <summary>
/// Кольцевой буфер звука: готовые кадры AAC трёх дорожек (игра, микрофон, смесь).
///
/// Раньше здесь лежал несжатый звук, 16 бит: 192 КБ в секунду на дорожку, и в AAC
/// его превращал писатель файла уже при сохранении. Теперь звук кодируется по мере
/// записи (<see cref="Audio.AacEncoder"/>), и буфер хранит кадры по 21 мс: 24 КБ/с
/// на стерео и 16 КБ/с на микрофон — в восемь раз меньше памяти, и ни одного
/// объекта на кадр.
///
/// Какие дорожки положить в файл, решает сохранение: смесь, раздельно или одну.
/// </summary>
public sealed class ReplayAudioBuffer
{
    /// <summary>Запас сверх заказанной длительности — тот же, что у видео.</summary>
    private const long SlackTicks = ReplayVideoBuffer.SlackSeconds * 10_000_000L;

    /// <summary>Длительность кадра AAC (1024 сэмпла при 48 кГц), тики.</summary>
    public const long FrameTicks = 1024L * 10_000_000 / 48_000;

    /// <summary>Битрейты дорожек — те же, что у кодера (AudioMixerEngine).</summary>
    public const int StereoBitrate = 192_000;
    public const int MonoBitrate = 128_000;

    private readonly object _sync = new();
    private readonly Track?[] _tracks = new Track?[3];

    public long MaxDurationTicks { get; set; }

    /// <summary>Сколько байт занимают накопленные кадры — для диагностики памяти.</summary>
    public long TotalBytes
    {
        get { lock (_sync) return _tracks.Sum(t => t?.Used ?? 0); }
    }

    /// <summary>Сколько байт выделено под кольца звука (массивы создаются целиком сразу).</summary>
    public long CapacityBytes
    {
        get { lock (_sync) return _tracks.Sum(t => t?.Capacity ?? 0); }
    }

    /// <summary>
    /// Выделить кольца под заданную длительность. Зовётся при старте конвейера.
    /// Выключенная дорожка не занимает ничего.
    /// </summary>
    public void Allocate(int seconds, bool game, bool mic)
    {
        long ticks = seconds * 10_000_000L + SlackTicks;
        long bytes;
        lock (_sync)
        {
            _tracks[(int)AudioTrackKind.Game] = game ? new Track(StereoBitrate, ticks) : null;
            _tracks[(int)AudioTrackKind.Mic] = mic ? new Track(MonoBitrate, ticks) : null;
            _tracks[(int)AudioTrackKind.Mixed] = game && mic ? new Track(StereoBitrate, ticks) : null;
            bytes = _tracks.Sum(t => t?.Capacity ?? 0);
        }
        Log.Info("Buffer", $"Арена звука (AAC): {bytes / 1024} КБ на {seconds} сек, дорожки: " +
                           $"{(game ? "игра " : "")}{(mic ? "микрофон " : "")}{(game && mic ? "смесь" : "")}");
    }

    /// <summary>
    /// Сменить ёмкость колец под новую длину, сохранив накопленные кадры. Дорожки,
    /// которых больше нет, отпускаются; новые заводятся пустыми.
    /// </summary>
    public void Resize(int seconds, bool game, bool mic)
    {
        long ticks = seconds * 10_000_000L + SlackTicks;
        lock (_sync)
        {
            _tracks[(int)AudioTrackKind.Game] = Rebuild(_tracks[(int)AudioTrackKind.Game], game, StereoBitrate, ticks);
            _tracks[(int)AudioTrackKind.Mic] = Rebuild(_tracks[(int)AudioTrackKind.Mic], mic, MonoBitrate, ticks);
            _tracks[(int)AudioTrackKind.Mixed] = Rebuild(_tracks[(int)AudioTrackKind.Mixed], game && mic, StereoBitrate, ticks);
        }

        static Track? Rebuild(Track? old, bool wanted, int bitrate, long ticks)
        {
            if (!wanted) return null;
            var track = new Track(bitrate, ticks);
            old?.CopyTo(track);
            return track;
        }
    }

    public void Add(AudioTrackKind kind, ReadOnlySpan<byte> frame, long ptsTicks)
    {
        lock (_sync)
        {
            var track = _tracks[(int)kind];
            if (track is null) return;
            track.Add(frame, ptsTicks);
            track.EvictBefore(ptsTicks - MaxDurationTicks - SlackTicks);
        }
    }

    /// <summary>
    /// Кадры в интервале [fromTicks, toTicks] отдельной копией по каждой дорожке.
    /// Первый кадр — ближайший к <paramref name="fromTicks"/>: погрешность не больше
    /// половины кадра (10 мс), на слух не отличима.
    /// </summary>
    public AudioSnapshot Snapshot(long fromTicks, long toTicks, Func<AudioTrackKind, byte[]?>? silence = null)
    {
        var result = new List<AudioTrackSnapshot>();
        lock (_sync)
        {
            for (int k = 0; k < _tracks.Length; k++)
            {
                var track = _tracks[k];
                if (track is null) continue;
                var (frames, first) = track.Copy(fromTicks, toTicks, silence?.Invoke((AudioTrackKind)k));
                result.Add(new AudioTrackSnapshot((AudioTrackKind)k, frames, first));
            }
        }
        return new AudioSnapshot(result);
    }

    /// <summary>Самый поздний кадр дорожки (для ожидания хвоста при сохранении); null — пусто.</summary>
    public long? NewestPts(AudioTrackKind kind)
    {
        lock (_sync) return _tracks[(int)kind]?.NewestPts;
    }

    /// <summary>Забыть накопленное, сохранив кольца.</summary>
    public void Clear()
    {
        lock (_sync) foreach (var t in _tracks) t?.Reset();
    }

    /// <summary>Отпустить кольца целиком: конвейер остановлен.</summary>
    public void Release()
    {
        lock (_sync) Array.Clear(_tracks);
    }

    /// <summary>Кольцо одной дорожки: байты кадров вплотную и кольцо записей о них.</summary>
    private sealed class Track
    {
        private readonly record struct Entry(long Pts, int Offset, int Length, int Total);

        private readonly byte[] _data;
        private Entry[] _entries = new Entry[4096];
        private int _head, _count;
        private int _tail;

        public long Used { get; private set; }
        public long Capacity => _data.Length;
        public long? NewestPts => _count > 0 ? At(_count - 1).Pts : null;

        public Track(int bitrate, long ticks)
        {
            // Средний битрейт плюс треть на всплески кодера и запас в 64 КБ
            long bytes = (long)(bitrate / 8.0 * ticks / 10_000_000 * 1.33) + (64 << 10);
            _data = new byte[bytes];
        }

        private ref Entry At(int i) => ref _entries[(_head + i) % _entries.Length];

        public void Add(ReadOnlySpan<byte> frame, long pts)
        {
            if (frame.Length == 0 || frame.Length > _data.Length / 4) return;
            int pad = _tail + frame.Length <= _data.Length ? 0 : _data.Length - _tail;
            int need = pad + frame.Length;
            while (_count > 0 && Used + need > _data.Length) RemoveFirst();

            int start = (_tail + pad) % _data.Length;
            frame.CopyTo(_data.AsSpan(start));
            _tail = (start + frame.Length) % _data.Length;
            Used += need;

            if (_count == _entries.Length)
            {
                var bigger = new Entry[_entries.Length * 2];
                for (int i = 0; i < _count; i++) bigger[i] = At(i);
                _entries = bigger;
                _head = 0;
            }
            _entries[(_head + _count) % _entries.Length] = new Entry(pts, start, frame.Length, need);
            _count++;
        }

        public void EvictBefore(long pts)
        {
            while (_count > 1 && At(0).Pts < pts) RemoveFirst();
        }

        private void RemoveFirst()
        {
            Used -= At(0).Total;
            _head = (_head + 1) % _entries.Length;
            _count--;
            if (_count == 0) { _tail = 0; Used = 0; }
        }

        public void Reset()
        {
            _head = _count = _tail = 0;
            Used = 0;
        }

        /// <summary>Переписать все кадры в другое кольцо (оно само вытеснит лишнее).</summary>
        public void CopyTo(Track target)
        {
            for (int i = 0; i < _count; i++)
            {
                ref Entry e = ref At(i);
                target.Add(_data.AsSpan(e.Offset, e.Length), e.Pts);
            }
        }

        /// <summary>
        /// Кадры интервала подряд. Кадры в MP4 идут без меток времени, поэтому
        /// разрыв в звуке (перезапуск звука при смене устройства, отставание
        /// микшера) заполняется кадрами тишины <paramref name="silence"/> — иначе
        /// весь звук после разрыва съехал бы назад. Без кадра тишины (или при
        /// разрыве длиннее минуты) остаётся только последний сплошной отрезок.
        /// Кадры, налезающие на уже взятые (новый отсчёт кодера чуть раньше конца
        /// старого), пропускаются.
        /// </summary>
        public (List<byte[]> Frames, long FirstPts) Copy(long from, long to, byte[]? silence = null)
        {
            const long MaxFillTicks = 60 * 10_000_000L;
            var frames = new List<byte[]>();
            long first = 0;
            for (int i = 0; i < _count; i++)
            {
                ref Entry e = ref At(i);
                if (e.Pts + FrameTicks / 2 < from) continue;
                if (e.Pts > to) break;

                if (frames.Count > 0)
                {
                    long expected = first + ExactTicks(frames.Count);
                    long gap = e.Pts - expected;
                    if (gap < -FrameTicks / 2) continue;               // налезает на взятое
                    if (gap > FrameTicks / 2)
                    {
                        if (silence is not null && gap <= MaxFillTicks)
                        {
                            long missing = (long)Math.Round(gap / (double)ExactTicks(1));
                            for (long m = 0; m < missing; m++) frames.Add(silence);
                        }
                        else frames.Clear();
                    }
                }
                if (frames.Count == 0) first = e.Pts;
                frames.Add(_data.AsSpan(e.Offset, e.Length).ToArray());
            }
            return (frames, first);
        }

        /// <summary>Длительность n кадров без накопления округления, тики.</summary>
        private static long ExactTicks(long frames) => frames * 1024 * 10_000_000 / 48_000;
    }
}
