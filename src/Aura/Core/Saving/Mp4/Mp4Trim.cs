using System.Buffers.Binary;
using TextEncoding = System.Text.Encoding;

namespace Aura.Core.Saving.Mp4;

/// <summary>
/// Вырезка фрагмента из обычного MP4 без перекодирования и без кодеков.
///
/// ЗАЧЕМ. Точный экспорт через Windows Media декодирует видео, а HEVC и AV1 Windows
/// без платного расширения из Store не декодирует вовсе: редактор не мог сохранить
/// ни одного фрагмента клипа Aura (HEVC по умолчанию), если рядом нет ffmpeg.
///
/// КАК — так же, как ffmpeg с -ss перед -i и -c copy (на нём построен LosslessCut).
/// Кадры копируются байт в байт, начиная с ключевого кадра не позже точки старта:
/// раньше декодер начать не может. Лишнее начало и хвост прячет список правок
/// (elst): одна правка «с такого-то места медиа, столько-то» — тот базовый случай,
/// который обязаны понимать и браузеры (MSE), и VLC, и Media Foundation, и QuickTime.
///
/// Звук берётся с того же ключевого кадра, что и видео, а не ровно с точки старта.
/// Плеер, который elst честно выполняет, покажет фрагмент кадр в кадр; плеер, который
/// его игнорирует, покажет на секунду-другую больше, но звук не разъедется с картинкой.
///
/// moov пишется перед данными (как -movflags +faststart), дорожки чередуются
/// кусками по полсекунды: фрагмент сразу начинает играть в браузере и Discord.
/// </summary>
public static class Mp4Trim
{
    private const double InterleaveSeconds = 0.5;

    /// <summary>
    /// Звуковая дорожка AAC для сведения: кадры подряд и время первого из них на
    /// шкале исходного файла, секунды.
    /// </summary>
    public sealed record AacClip(int SampleRate, int Channels, byte[] Config, int Bitrate,
                                 IReadOnlyList<byte[]> Frames, double StartSeconds);

    /// <summary>Свести несколько дорожек в одну (декодирование, сумма, кодирование).</summary>
    public delegate AacClip AudioMixer(IReadOnlyList<AacClip> tracks);

    internal sealed class SourceTrack
    {
        public int Id;
        public bool Audio;
        public int Timescale;
        public int Width, Height;
        public string Name = "";
        public byte[] Stsd = [];
        public double EmptySeconds;    // пустой отрезок перед дорожкой в исходном файле
        public long MediaStart;        // откуда дорожка начинает показ, в её единицах
        public long[] Offsets = [];
        public int[] Sizes = [];
        public long[] Dts = [];
        public int[] Cts = [];
        public bool[] Key = [];
        public uint LastDuration;
        public byte[][]? Memory;       // сэмплы не из файла, а готовые (сведённый звук)

        /// <summary>Время на шкале исходного фильма, секунды.</summary>
        public double Time(long mediaTicks) => EmptySeconds + (double)(mediaTicks - MediaStart) / Timescale;
        public double Pts(int i) => Time(Dts[i] + Cts[i]);
        public double DtsTime(int i) => Time(Dts[i]);
        public uint Duration(int i) => i + 1 < Dts.Length ? (uint)Math.Max(1, Dts[i + 1] - Dts[i]) : Math.Max(1, LastDuration);
    }

    /// <summary>
    /// Вырезать [<paramref name="start"/>, <paramref name="end"/>) секунд в <paramref name="output"/>.
    /// <paramref name="audioTrack"/> — номер звуковой дорожки по порядку в файле, -1 — все:
    /// при <paramref name="mix"/> они сводятся в одну, без него остаются отдельными.
    /// Файлы без видео не поддерживаются (NotSupportedException).
    /// </summary>
    public static void Cut(string input, string output, double start, double end, int audioTrack,
                           Action<double>? progress = null, CancellationToken ct = default,
                           AudioMixer? mix = null)
    {
        using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                          1, FileOptions.RandomAccess);
        byte[] moov = Mp4Keyframes.ReadTopLevel(source, "moov") ?? throw new NotSupportedException("в файле нет moov");
        byte[]? ftyp = Mp4Keyframes.ReadTopLevel(source, "ftyp");
        bool fragmented = Mp4Keyframes.Find(moov, 8, moov.Length, "mvex") is not null;
        var tracks = fragmented
            ? FromFragments(Mp4Defragment.ReadFragmented(source) ?? throw new NotSupportedException("фрагменты не разобрать"))
            : ReadTracks(moov);
        // Марки «iso5/iso6» обещают фрагменты, а фрагмент выходит обычным MP4
        if (fragmented) ftyp = null;

        var video = tracks.FirstOrDefault(t => !t.Audio && t.Sizes.Length > 0)
                    ?? throw new NotSupportedException("в файле нет видеодорожки");
        var audios = tracks.Where(t => t.Audio && t.Sizes.Length > 0).ToList();
        if (audioTrack >= 0) audios = audioTrack < audios.Count ? [audios[audioTrack]] : audios.Take(1).ToList();

        // Ключевой кадр, с которого начнётся копия: последний не позже старта.
        int key = -1;
        for (int i = 0; i < video.Sizes.Length; i++)
            if (video.Key[i] && video.Pts(i) <= start + 1e-6 && (key < 0 || video.Pts(i) >= video.Pts(key))) key = i;
        if (key < 0)
        {
            key = Array.IndexOf(video.Key, true);
            if (key < 0) throw new NotSupportedException("в видео нет ключевых кадров");
            start = Math.Max(start, video.Pts(key));
        }
        if (end - start < 0.05) throw new ArgumentOutOfRangeException(nameof(end), "фрагмент пуст");
        double anchor = video.Pts(key);

        var picked = new List<(SourceTrack Track, int First, int Last)>();
        picked.Add((video, key, LastBefore(video, key, end)));
        foreach (var a in audios)
        {
            int first = 0;
            for (int j = 0; j < a.Sizes.Length && a.DtsTime(j) <= anchor + 1e-6; j++) first = j;
            if (a.DtsTime(first) >= end) continue;
            picked.Add((a, first, LastBefore(a, first, end)));
        }

        if (mix is not null && picked.Count > 2)
        {
            var clips = picked.Skip(1).Select(p => Clip(source, p.Track, p.First, p.Last)).ToList();
            ct.ThrowIfCancellationRequested();
            var mixed = mix(clips);
            picked.RemoveRange(1, picked.Count - 1);
            var memory = InMemory(mixed);
            picked.Add((memory, 0, memory.Sizes.Length - 1));
        }

        // Порядок данных в mdat: куски по полсекунды от каждой дорожки по очереди
        var order = new List<(int Track, int First, int Count)>();
        var cursor = picked.Select(p => p.First).ToArray();
        for (double window = anchor + InterleaveSeconds; cursor.Where((c, n) => c <= picked[n].Last).Any(); window += InterleaveSeconds)
            for (int n = 0; n < picked.Count; n++)
            {
                var (t, _, last) = picked[n];
                int from = cursor[n];
                while (cursor[n] <= last && t.DtsTime(cursor[n]) < window) cursor[n]++;
                if (cursor[n] > from) order.Add((n, from, cursor[n] - from));
            }

        // Индексы дорожек; смещения кусков пока от начала данных mdat
        var indexes = picked.Select(_ => new Mp4Defragment.TrackIndex()).ToArray();
        for (int n = 0; n < picked.Count; n++)
        {
            var (t, first, last) = picked[n];
            for (int i = first; i <= last; i++) indexes[n].Add(t.Sizes[i], t.Duration(i), t.Cts[i], t.Key[i] || t.Audio);
        }
        long dataLength = 0;
        foreach (var (n, first, count) in order)
        {
            indexes[n].Chunks.Add((dataLength, count));
            for (int i = first; i < first + count; i++) dataLength += picked[n].Track.Sizes[i];
        }

        var outTracks = new List<Mp4Defragment.Track>();
        long fragmentMovie = (long)Math.Round((end - start) * Mp4Boxes.MovieTimescale);
        for (int n = 0; n < picked.Count; n++)
        {
            var (t, first, _) = picked[n];
            var index = indexes[n];
            // С какого места новой шкалы дорожки показывать, чтобы ноль фрагмента был ровно start
            long media = (long)Math.Round((start - t.EmptySeconds) * t.Timescale) + t.MediaStart - t.Dts[first];
            long empty = 0;
            if (media < 0)
            {
                empty = (long)Math.Round(-media * (double)Mp4Boxes.MovieTimescale / t.Timescale);
                media = 0;
            }
            long available = (long)Mp4Boxes.ToMovie(index.MediaDuration - media, t.Timescale);
            long edit = Math.Min(fragmentMovie - empty, available);
            if (edit <= 0) continue;
            outTracks.Add(new Mp4Defragment.Track(outTracks.Count + 1, t.Audio, t.Timescale, t.Width, t.Height, t.Name,
                                                  t.Stsd, media, index, empty, edit));
        }

        ftyp ??= DefaultFtyp();
        bool largeMdat = dataLength + 8 > uint.MaxValue;
        int mdatHeader = largeMdat ? 16 : 8;
        bool co64 = ftyp.Length + dataLength + mdatHeader + moov.Length * 2L > uint.MaxValue;
        long dataStart = ftyp.Length + Mp4Defragment.BuildMoov(outTracks, co64).Length + mdatHeader;
        foreach (var index in indexes)
            for (int c = 0; c < index.Chunks.Count; c++)
                index.Chunks[c] = (index.Chunks[c].Offset + dataStart, index.Chunks[c].Count);
        byte[] newMoov = Mp4Defragment.BuildMoov(outTracks, co64);

        using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                          1 << 20, FileOptions.SequentialScan);
        target.Write(ftyp);
        target.Write(newMoov);
        var header = new byte[mdatHeader];
        if (largeMdat)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)(dataLength + 16));
        }
        else BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(dataLength + 8));
        "mdat"u8.CopyTo(header.AsSpan(4));
        target.Write(header);
        if (target.Position != dataStart) throw new InvalidOperationException("раскладка фрагмента разошлась с оглавлением");

        // Данные: соседние в исходнике сэмплы читаем одним куском
        var buffer = new byte[1 << 20];
        long copied = 0;
        foreach (var (n, first, count) in order)
        {
            ct.ThrowIfCancellationRequested();
            var t = picked[n].Track;
            int i = first, stop = first + count;
            if (t.Memory is { } frames)
            {
                for (; i < stop; i++) { target.Write(frames[i]); copied += frames[i].Length; }
                progress?.Invoke(dataLength == 0 ? 1 : (double)copied / dataLength);
                continue;
            }
            while (i < stop)
            {
                long offset = t.Offsets[i];
                long length = t.Sizes[i++];
                while (i < stop && t.Offsets[i] == offset + length) length += t.Sizes[i++];
                for (long done = 0; done < length;)
                {
                    int part = (int)Math.Min(buffer.Length, length - done);
                    source.Position = offset + done;
                    source.ReadExactly(buffer, 0, part);
                    target.Write(buffer, 0, part);
                    done += part;
                }
                copied += length;
                progress?.Invoke(dataLength == 0 ? 1 : (double)copied / dataLength);
            }
        }
        target.Flush(flushToDisk: true);
    }

    /// <summary>Кадры звуковой дорожки [first, last] из файла — для сведения.</summary>
    private static AacClip Clip(Stream source, SourceTrack t, int first, int last)
    {
        var (rate, channels, config, bitrate) = ParseMp4a(t.Stsd);
        var frames = new byte[last - first + 1][];
        for (int i = first; i <= last; i++)
        {
            var frame = new byte[t.Sizes[i]];
            source.Position = t.Offsets[i];
            source.ReadExactly(frame);
            frames[i - first] = frame;
        }
        return new AacClip(rate, channels, config, bitrate, frames, t.DtsTime(first));
    }

    /// <summary>Сведённая дорожка как дорожка-источник: кадры в памяти, шкала — частота.</summary>
    private static SourceTrack InMemory(AacClip clip)
    {
        int n = clip.Frames.Count;
        if (n == 0) throw new InvalidOperationException("сведение не дало ни одного кадра");
        var format = new Mp4AudioFormat(clip.SampleRate, clip.Channels, clip.Config, clip.Bitrate, "Mixed audio");
        var w = new BoxWriter();
        w.BeginFull("stsd", 0, 0);
        w.U32(1);
        format.WriteSampleEntry(w);
        w.End();
        var t = new SourceTrack
        {
            Audio = true, Timescale = clip.SampleRate, Name = format.Name, Stsd = w.ToArray(),
            EmptySeconds = clip.StartSeconds, Memory = clip.Frames.ToArray(),
            Sizes = clip.Frames.Select(f => f.Length).ToArray(),
            Dts = Enumerable.Range(0, n).Select(i => (long)i * Mp4AudioFormat.FrameSamples).ToArray(),
            Cts = new int[n], Key = Enumerable.Repeat(true, n).ToArray(), Offsets = new long[n],
            LastDuration = Mp4AudioFormat.FrameSamples,
        };
        return t;
    }

    /// <summary>Частота, каналы, AudioSpecificConfig и битрейт из stsd с одной записью mp4a.</summary>
    internal static (int Rate, int Channels, byte[] Config, int Bitrate) ParseMp4a(byte[] stsd)
    {
        const int entry = 16;   // заголовок stsd (8) + версия и флаги (4) + число записей (4)
        if (stsd.Length < entry + 36 || TextEncoding.ASCII.GetString(stsd, entry + 4, 4) != "mp4a")
            throw new NotSupportedException("звук не AAC");
        int channels = BinaryPrimitives.ReadUInt16BigEndian(stsd.AsSpan(entry + 24));
        int rate = (int)(U32(stsd, entry + 32) >> 16);
        int esds = stsd.AsSpan(entry + 36).IndexOf("esds"u8);
        if (esds < 0) throw new NotSupportedException("нет esds");
        int pos = entry + 36 + esds + 8;   // после типа и версии с флагами
        int end = (int)Math.Min(stsd.Length, entry + 36 + esds - 4 + U32(stsd, entry + 36 + esds - 4));
        int bitrate = 0;
        byte[]? config = null;
        while (pos + 2 <= end && config is null)
        {
            byte tag = stsd[pos++];
            int length = 0;
            for (int k = 0; k < 4 && pos < end; k++)
            {
                byte b = stsd[pos++];
                length = (length << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) break;
            }
            switch (tag)
            {
                case 0x03:   // ES_Descriptor: ES_ID, флаги и необязательные поля перед вложенными
                    byte flags = stsd[pos + 2];
                    pos += 3;
                    if ((flags & 0x80) != 0) pos += 2;
                    if ((flags & 0x40) != 0) pos += 1 + stsd[pos];
                    if ((flags & 0x20) != 0) pos += 2;
                    break;
                case 0x04:   // DecoderConfigDescriptor: 13 байт, дальше вложенные
                    bitrate = (int)U32(stsd, pos + 9);
                    pos += 13;
                    break;
                case 0x05:
                    config = stsd.AsSpan(pos, length).ToArray();
                    break;
                default:
                    pos += length;
                    break;
            }
        }
        return (rate, channels, config ?? throw new NotSupportedException("нет AudioSpecificConfig"), bitrate);
    }

    /// <summary>Последний сэмпл (по порядку декодирования), который ещё нужен до конца фрагмента.</summary>
    private static int LastBefore(SourceTrack t, int first, double end)
    {
        int last = first;
        // DTS ≤ PTS: всё, что показывается раньше конца, декодируется раньше него
        while (last + 1 < t.Sizes.Length && t.DtsTime(last + 1) < end) last++;
        return last;
    }

    private static byte[] DefaultFtyp()
    {
        var w = new BoxWriter();
        w.Begin("ftyp");
        w.FourCc("isom"); w.U32(0x200); w.FourCc("isom"); w.FourCc("iso2"); w.FourCc("mp41");
        w.End();
        return w.ToArray();
    }

    /// <summary>
    /// Дорожки старой фрагментированной записи — те же таблицы, что дописал бы
    /// Mp4Defragment: звук, поднявшийся позже видео, начинается со своего первого DTS.
    /// </summary>
    internal static List<SourceTrack> FromFragments(List<Mp4Defragment.Track> tracks)
    {
        var result = new List<SourceTrack>();
        foreach (var f in tracks)
        {
            var index = f.Index;
            int n = index.Sizes.Count;
            var t = new SourceTrack
            {
                Id = f.Id, Audio = f.Audio, Timescale = f.Timescale, Width = f.Width, Height = f.Height,
                Name = f.Name, Stsd = f.Stsd, MediaStart = f.MediaStart,
                Sizes = index.Sizes.ToArray(), Cts = index.Cts.ToArray(),
                Dts = new long[n], Key = new bool[n], Offsets = new long[n],
                LastDuration = n > 0 ? index.Durations[^1] : 1,
            };
            long dts = f.Audio ? Math.Max(0, index.FirstDts) : 0;
            for (int i = 0; i < n; i++) { t.Dts[i] = dts; dts += index.Durations[i]; }
            foreach (int number in index.Sync) t.Key[number - 1] = true;
            int s = 0;
            foreach (var (offset, count) in index.Chunks)
            {
                long at = offset;
                for (int k = 0; k < count && s < n; k++, s++) { t.Offsets[s] = at; at += t.Sizes[s]; }
            }
            if (s < n) throw new NotSupportedException("фрагменты описывают не все сэмплы");
            result.Add(t);
        }
        return result;
    }

    internal static List<SourceTrack> ReadTracks(byte[] moov)
    {
        if (Mp4Keyframes.Find(moov, 8, moov.Length, "mvex") is not null)
            throw new NotSupportedException("фрагментированный MP4");
        long movieTimescale = 1000;
        if (Mp4Keyframes.Find(moov, 8, moov.Length, "mvhd") is { } mvhd)
            movieTimescale = U32(moov, mvhd.Body + 4 + (moov[mvhd.Body] == 1 ? 16 : 8));
        if (movieTimescale <= 0) throw new NotSupportedException("нулевая шкала фильма");

        var result = new List<SourceTrack>();
        foreach (var trak in Mp4Keyframes.Children(moov, 8, moov.Length).Where(b => b.Type == "trak"))
        {
            if (Mp4Keyframes.Find(moov, trak.Body, trak.End, "mdia") is not { } mdia ||
                Mp4Keyframes.Find(moov, mdia.Body, mdia.End, "hdlr") is not { } hdlr ||
                Mp4Keyframes.Find(moov, mdia.Body, mdia.End, "mdhd") is not { } mdhd ||
                Mp4Keyframes.Find(moov, trak.Body, trak.End, "tkhd") is not { } tkhd ||
                Mp4Keyframes.FindPath(moov, mdia, "minf", "stbl") is not { } stbl) continue;
            string handler = TextEncoding.ASCII.GetString(moov, hdlr.Body + 8, 4);
            if (handler is not ("vide" or "soun")) continue;

            var t = new SourceTrack
            {
                Audio = handler == "soun",
                Id = (int)U32(moov, tkhd.Body + 4 + (moov[tkhd.Body] == 1 ? 16 : 8)),
                Width = (int)(U32(moov, tkhd.End - 8) >> 16),
                Height = (int)(U32(moov, tkhd.End - 4) >> 16),
                Timescale = (int)U32(moov, mdhd.Body + 4 + (moov[mdhd.Body] == 1 ? 16 : 8)),
            };
            if (t.Timescale <= 0) continue;
            int nameStart = hdlr.Body + 24;
            int nameEnd = nameStart < hdlr.End ? Array.IndexOf(moov, (byte)0, nameStart, hdlr.End - nameStart) : -1;
            t.Name = nameStart < hdlr.End ? TextEncoding.UTF8.GetString(moov, nameStart, (nameEnd < 0 ? hdlr.End : nameEnd) - nameStart) : "";
            var (emptyMovie, mediaStart) = Mp4Keyframes.EditList(moov, trak, movieTimescale);
            t.EmptySeconds = (double)emptyMovie / movieTimescale;
            t.MediaStart = mediaStart;

            if (Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "stsd") is not { } stsd ||
                Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "stsz") is not { } stsz ||
                Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "stsc") is not { } stsc ||
                Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "stts") is not { } stts)
                throw new NotSupportedException("неполная таблица сэмплов");
            t.Stsd = moov.AsSpan(stsd.Start, stsd.End - stsd.Start).ToArray();

            // Размеры
            uint fixedSize = U32(moov, stsz.Body + 4);
            int n = (int)U32(moov, stsz.Body + 8);
            t.Sizes = new int[n];
            for (int i = 0; i < n; i++) t.Sizes[i] = (int)(fixedSize != 0 ? fixedSize : U32(moov, stsz.Body + 12 + i * 4));

            // Время декодирования и сдвиг показа
            t.Dts = new long[n];
            t.Cts = new int[n];
            t.Key = new bool[n];
            long dts = 0;
            int s = 0;
            foreach (var (count, delta) in Mp4Keyframes.Runs(moov, stts, signed: false))
                for (uint k = 0; k < count && s < n; k++, s++) { t.Dts[s] = dts; dts += delta; t.LastDuration = (uint)delta; }
            if (s < n) throw new NotSupportedException("stts короче stsz");
            s = 0;
            foreach (var (count, value) in Mp4Keyframes.Runs(moov, Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "ctts"), signed: true))
                for (uint k = 0; k < count && s < n; k++) t.Cts[s++] = (int)value;
            if (Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "stss") is { } stss)
            {
                int count = (int)U32(moov, stss.Body + 4);
                for (int i = 0; i < count; i++)
                {
                    int number = (int)U32(moov, stss.Body + 8 + i * 4);
                    if (number >= 1 && number <= n) t.Key[number - 1] = true;
                }
            }
            else Array.Fill(t.Key, true);

            // Где лежит каждый сэмпл: куски из stco/co64, по скольку сэмплов в куске — stsc
            bool co64 = false;
            var chunkBox = Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "stco");
            if (chunkBox is null) { chunkBox = Mp4Keyframes.Find(moov, stbl.Body, stbl.End, "co64"); co64 = true; }
            if (chunkBox is not { } chunks) throw new NotSupportedException("нет таблицы кусков");
            int chunkCount = (int)U32(moov, chunks.Body + 4);
            int entries = (int)U32(moov, stsc.Body + 4);
            t.Offsets = new long[n];
            s = 0;
            for (int e = 0; e < entries && s < n; e++)
            {
                int firstChunk = (int)U32(moov, stsc.Body + 8 + e * 12);
                int perChunk = (int)U32(moov, stsc.Body + 8 + e * 12 + 4);
                int nextFirst = e + 1 < entries ? (int)U32(moov, stsc.Body + 8 + (e + 1) * 12) : chunkCount + 1;
                for (int c = firstChunk; c < nextFirst && c <= chunkCount && s < n; c++)
                {
                    long offset = co64
                        ? (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(chunks.Body + 8 + (c - 1) * 8))
                        : U32(moov, chunks.Body + 8 + (c - 1) * 4);
                    for (int k = 0; k < perChunk && s < n; k++, s++)
                    {
                        t.Offsets[s] = offset;
                        offset += t.Sizes[s];
                    }
                }
            }
            if (s < n) throw new NotSupportedException("stsc описывает не все сэмплы");
            result.Add(t);
        }
        return result;
    }

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at));
}
