using System.Buffers.Binary;
using TextEncoding = System.Text.Encoding;

namespace Aura.Core.Saving.Mp4;

/// <summary>
/// Время ключевых кадров видео в готовом MP4: по таблицам moov, без декодирования.
///
/// ЗАЧЕМ. Быстрый экспорт фрагмента копирует видео без перекодирования, и
/// начаться оно может только с ключевого кадра. ffmpeg с <c>-ss</c> перед входом
/// берёт видео с ключевого кадра ДО точки старта, а звук режет ровно по точке:
/// в начале фрагмента до двух секунд картинки без звука. Зная ключевые кадры,
/// старт ставим точно на кадр, и звук идёт с того же места.
///
/// Время считается так же, как его видит ffmpeg: DTS + CTS, минус начало медиа
/// в списке правок (elst) плюс пустой отрезок перед ним.
/// </summary>
public static class Mp4Keyframes
{
    /// <summary>Времена ключевых кадров первой видеодорожки по возрастанию; пусто, если прочитать не вышло.</summary>
    public static IReadOnlyList<double> Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            byte[]? moov = ReadTopLevel(file, "moov");
            return moov is null ? [] : FromMoov(moov);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Ключевой кадр, с которого начать фрагмент: последний не позже
    /// <paramref name="start"/>. Видео и раньше начиналось с него, только без
    /// звука; брать следующий значило бы отрезать часть выбранного момента.
    /// Если такого нет, первый кадр раньше <paramref name="end"/>; null — кадров нет.
    /// </summary>
    public static double? StartFor(IReadOnlyList<double> keyframes, double start, double end)
    {
        double? before = null;
        foreach (double k in keyframes)
        {
            if (k <= start + 1e-6) before = k;
            else return before ?? (k < end - 0.05 ? k : null);
        }
        return before;
    }

    internal static IReadOnlyList<double> FromMoov(byte[] moov)
    {
        long movieTimescale = 1000;
        if (Find(moov, 8, moov.Length, "mvhd") is { } mvhd)
            movieTimescale = moov[mvhd.Body] == 1
                ? BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Body + 4 + 16))
                : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Body + 4 + 8));

        foreach (var trak in Children(moov, 8, moov.Length).Where(b => b.Type == "trak"))
        {
            var mdia = Find(moov, trak.Body, trak.End, "mdia");
            if (mdia is not { } m) continue;
            var hdlr = Find(moov, m.Body, m.End, "hdlr");
            if (hdlr is not { } h || TextEncoding.ASCII.GetString(moov, h.Body + 8, 4) != "vide") continue;

            var mdhd = Find(moov, m.Body, m.End, "mdhd");
            if (mdhd is not { } md) continue;
            long timescale = moov[md.Body] == 1
                ? BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(md.Body + 4 + 16))
                : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(md.Body + 4 + 8));
            if (timescale <= 0 || movieTimescale <= 0) return [];

            var (emptyMovie, mediaStart) = EditList(moov, trak, movieTimescale);
            var stbl = FindPath(moov, m, "minf", "stbl");
            if (stbl is not { } st) return [];

            var deltas = Runs(moov, Find(moov, st.Body, st.End, "stts"), signed: false);
            var offsets = Runs(moov, Find(moov, st.Body, st.End, "ctts"), signed: true);
            HashSet<int>? sync = null;
            if (Find(moov, st.Body, st.End, "stss") is { } stss)
            {
                int n = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stss.Body + 4));
                sync = new HashSet<int>(n);
                for (int i = 0; i < n; i++)
                    sync.Add((int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(stss.Body + 8 + i * 4)));
            }

            var result = new List<double>();
            long dts = 0;
            int sample = 0, offsetRun = 0, offsetLeft = offsets.Count > 0 ? (int)offsets[0].Count : 0;
            foreach (var (count, delta) in deltas)
            {
                for (uint i = 0; i < count; i++)
                {
                    sample++;
                    long cts = 0;
                    if (offsets.Count > 0 && offsetRun < offsets.Count)
                    {
                        cts = offsets[offsetRun].Value;
                        if (--offsetLeft == 0 && ++offsetRun < offsets.Count)
                            offsetLeft = (int)offsets[offsetRun].Count;
                    }
                    if (sync is null || sync.Contains(sample))
                        result.Add((double)emptyMovie / movieTimescale + (double)(dts + cts - mediaStart) / timescale);
                    dts += delta;
                }
            }
            result.Sort();
            return result;
        }
        return [];
    }

    /// <summary>Пустой отрезок перед медиа (в единицах фильма) и начало медиа (в единицах дорожки).</summary>
    private static (long EmptyMovie, long MediaStart) EditList(byte[] moov, BoxRef trak, long movieTimescale)
    {
        var edts = Find(moov, trak.Body, trak.End, "edts");
        if (edts is not { } e || Find(moov, e.Body, e.End, "elst") is not { } elst) return (0, 0);
        int version = moov[elst.Body];
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(elst.Body + 4));
        int pos = elst.Body + 8;
        long empty = 0;
        for (int i = 0; i < count; i++)
        {
            long duration, mediaTime;
            if (version == 1)
            {
                duration = (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(pos));
                mediaTime = BinaryPrimitives.ReadInt64BigEndian(moov.AsSpan(pos + 8));
                pos += 20;
            }
            else
            {
                duration = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(pos));
                mediaTime = BinaryPrimitives.ReadInt32BigEndian(moov.AsSpan(pos + 4));
                pos += 12;
            }
            if (mediaTime == -1) { empty += duration; continue; }
            return (empty, mediaTime);
        }
        return (empty, 0);
    }

    private static List<(uint Count, long Value)> Runs(byte[] moov, BoxRef? box, bool signed)
    {
        var runs = new List<(uint, long)>();
        if (box is not { } b) return runs;
        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(b.Body + 4));
        for (int i = 0; i < n; i++)
        {
            int at = b.Body + 8 + i * 8;
            uint count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at));
            long value = signed
                ? BinaryPrimitives.ReadInt32BigEndian(moov.AsSpan(at + 4))
                : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at + 4));
            runs.Add((count, value));
        }
        return runs;
    }

    private readonly record struct BoxRef(string Type, int Start, int Body, int End);

    private static IEnumerable<BoxRef> Children(byte[] data, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                header = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < header || pos + size > end) yield break;
            yield return new BoxRef(TextEncoding.ASCII.GetString(data, pos + 4, 4), pos, pos + header, pos + (int)size);
            pos += (int)size;
        }
    }

    private static BoxRef? Find(byte[] data, int start, int end, string type)
    {
        foreach (var b in Children(data, start, end))
            if (b.Type == type) return b;
        return null;
    }

    private static BoxRef? FindPath(byte[] data, BoxRef parent, params string[] path)
    {
        BoxRef current = parent;
        foreach (string part in path)
        {
            if (Find(data, current.Body, current.End, part) is not { } next) return null;
            current = next;
        }
        return current;
    }

    private static byte[]? ReadTopLevel(Stream file, string type)
    {
        var header = new byte[16];
        long pos = 0;
        while (pos + 8 <= file.Length)
        {
            file.Position = pos;
            if (file.Read(header, 0, 8) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            int headerSize = 8;
            if (size == 1)
            {
                if (file.Read(header, 8, 8) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0) size = file.Length - pos;
            if (size < headerSize) return null;
            if (TextEncoding.ASCII.GetString(header, 4, 4) == type)
            {
                if (size > 256L * 1024 * 1024) return null;
                var box = new byte[size];
                file.Position = pos;
                file.ReadExactly(box);
                return box;
            }
            pos += size;
        }
        return null;
    }
}
