using System.Buffers;

namespace Aura.Core.Saving.Mp4;

/// <summary>Кадр видео на входе писателя (Annex B, время в 100-нс тиках от начала клипа).</summary>
public interface IMp4VideoSource
{
    int Count { get; }
    ReadOnlySpan<byte> Data(int index);
    long Pts(int index);
    /// <summary>Время декодирования; без B-кадров совпадает с <see cref="Pts"/>.</summary>
    long Dts(int index);
    bool IsKeyframe(int index);
    /// <summary>Длительность последнего кадра (у остальных её задаёт следующий кадр).</summary>
    long LastDuration { get; }
}

/// <summary>Дорожка AAC на входе писателя: кадры подряд, начало — со сдвигом от видео.</summary>
public sealed record Mp4AudioInput(Mp4AudioFormat Format, IReadOnlyList<byte[]> Frames, long StartTicks);

/// <summary>
/// Обычный MP4 с оглавлением (moov) В НАЧАЛЕ файла — для сохранения повтора, когда
/// все кадры известны заранее.
///
/// ЗАЧЕМ СВОЙ. Раньше клип собирал IMFSinkWriter Media Foundation, и от него шла
/// целая серия бед из docs/: утечки сэмплов и буферов, закрепление арены на время
/// записи, обратное давление через недокументированный слот таблицы методов,
/// финализация, время которой растёт с ЧИСЛОМ сэмплов, и перекодирование звука в
/// AAC прямо в момент сохранения — под игрой. Видео при этом шло «как есть», то есть
/// писатель делал ровно то, что умеет любой мультиплексор: раскладывал готовые байты.
/// Здесь он и есть такой мультиплексор: только копирование, никаких кодеков.
///
/// Плюсы по сравнению с прошлым файлом:
/// • moov в начале (faststart): Telegram, Discord и браузеры показывают превью и
///   начинают играть, не дочитав файл;
/// • обычный, не фрагментированный MP4: редакторы (Premiere, DaVinci, Vegas)
///   открывают его без оговорок;
/// • 64-битные смещения (co64), когда файл больше 4 ГБ;
/// • дорожки перемежаются кусками по полсекунды — плеер читает файл подряд.
/// </summary>
public static class Mp4ProgressiveWriter
{
    private const long ChunkTicks = 5_000_000; // 0.5 с

    private sealed class Track
    {
        public required bool Audio;
        public required int Timescale;
        public int[] Sizes = [];
        public uint[] Durations = [];
        public int[] CtsOffsets = [];
        public bool HasCts;
        public List<int> SyncSamples = [];         // 1-based
        public List<(int First, int Count)> Chunks = [];
        public long[] ChunkOffsets = [];
        public long MediaDuration;
        public long StartTicks;                    // сдвиг начала от видео, 100 нс
        public Mp4VideoFormat? Video;
        public Mp4AudioFormat? AudioFormat;
        public IReadOnlyList<byte[]>? AudioFrames;
    }

    /// <summary>Записать клип в поток. Возвращает число записанных байт.</summary>
    public static long Write(Stream output, Mp4VideoFormat video, IMp4VideoSource frames,
                             IReadOnlyList<Mp4AudioInput> audio, Action<double>? progress = null)
    {
        if (frames.Count == 0) throw new InvalidOperationException("Нет кадров");

        // ---- 1. Размеры и времена сэмплов ----
        var vt = new Track { Audio = false, Timescale = video.Timescale, Video = video };
        int n = frames.Count;
        vt.Sizes = new int[n];
        vt.Durations = new uint[n];
        vt.CtsOffsets = new int[n];
        long dataBytes = 0;
        for (int i = 0; i < n; i++)
        {
            vt.Sizes[i] = VideoSamples.Size(video.Codec, frames.Data(i));
            dataBytes += vt.Sizes[i];
            long dur = i + 1 < n ? frames.Dts(i + 1) - frames.Dts(i) : frames.LastDuration;
            vt.Durations[i] = (uint)video.ToUnits(dur);
            vt.MediaDuration += vt.Durations[i];
            int cts = video.CtsUnits(frames.Pts(i) - frames.Dts(i));
            vt.CtsOffsets[i] = cts;
            if (cts != 0) vt.HasCts = true;
            if (frames.IsKeyframe(i)) vt.SyncSamples.Add(i + 1);
        }

        var tracks = new List<Track> { vt };
        foreach (var input in audio)
        {
            if (input.Frames.Count == 0) continue;
            var at = new Track
            {
                Audio = true,
                Timescale = input.Format.SampleRate,
                AudioFormat = input.Format,
                AudioFrames = input.Frames,
                StartTicks = input.StartTicks,
                Sizes = new int[input.Frames.Count],
                Durations = new uint[input.Frames.Count],
            };
            for (int i = 0; i < input.Frames.Count; i++)
            {
                at.Sizes[i] = input.Frames[i].Length;
                at.Durations[i] = Mp4AudioFormat.FrameSamples;
                dataBytes += at.Sizes[i];
            }
            at.MediaDuration = (long)input.Frames.Count * Mp4AudioFormat.FrameSamples;
            tracks.Add(at);
        }

        // ---- 2. Раскладка по кускам с перемежением по времени ----
        var order = new List<(int Track, int Chunk)>();
        var cursor = new int[tracks.Count];
        var mediaTime = new long[tracks.Count];   // в 100-нс тиках от начала видео
        for (int t = 0; t < tracks.Count; t++) mediaTime[t] = tracks[t].StartTicks;
        long window = ChunkTicks;
        while (true)
        {
            bool any = false;
            for (int t = 0; t < tracks.Count; t++)
            {
                var track = tracks[t];
                int first = cursor[t];
                int i = first;
                while (i < track.Sizes.Length && mediaTime[t] < window)
                {
                    mediaTime[t] += (long)track.Durations[i] * 10_000_000 / track.Timescale;
                    i++;
                }
                if (i > first)
                {
                    track.Chunks.Add((first, i - first));
                    order.Add((t, track.Chunks.Count - 1));
                    cursor[t] = i;
                }
                if (cursor[t] < track.Sizes.Length) any = true;
            }
            if (!any) break;
            window += ChunkTicks;
        }
        foreach (var track in tracks) track.ChunkOffsets = new long[track.Chunks.Count];

        // ---- 3. Оглавление: сначала узнаём его размер, потом ставим смещения ----
        bool mdat64 = dataBytes > uint.MaxValue - 16;
        var ftyp = new BoxWriter();
        Mp4Boxes.Ftyp(ftyp, fragmented: false, video.Codec);

        long mdatHeader = mdat64 ? 16 : 8;
        // 32-битные смещения кусков годятся, пока весь файл меньше 4 ГБ.
        byte[] moov = BuildMoov(tracks, co64: false);
        bool co64 = ftyp.Length + moov.Length + mdatHeader + dataBytes > uint.MaxValue;
        if (co64) moov = BuildMoov(tracks, co64: true);
        long offset = ftyp.Length + moov.Length + mdatHeader;
        foreach (var (t, c) in order)
        {
            var track = tracks[t];
            track.ChunkOffsets[c] = offset;
            var (first, count) = track.Chunks[c];
            for (int i = first; i < first + count; i++) offset += track.Sizes[i];
        }
        moov = BuildMoov(tracks, co64);   // размер тот же — ширина полей не зависит от значений

        // ---- 4. Запись ----
        output.Write(ftyp.Span);
        output.Write(moov);
        var header = new BoxWriter();
        if (mdat64)
        {
            header.U32(1);
            header.FourCc("mdat");
            header.U64((ulong)(dataBytes + 16));
        }
        else
        {
            header.U32((uint)(dataBytes + 8));
            header.FourCc("mdat");
        }
        output.Write(header.Span);

        byte[] scratch = ArrayPool<byte>.Shared.Rent(1 << 20);
        long written = 0;
        long lastReport = 0;
        try
        {
            foreach (var (t, c) in order)
            {
                var track = tracks[t];
                var (first, count) = track.Chunks[c];
                for (int i = first; i < first + count; i++)
                {
                    if (!track.Audio)
                    {
                        var data = frames.Data(i);
                        if (scratch.Length < track.Sizes[i] + 64)
                        {
                            ArrayPool<byte>.Shared.Return(scratch);
                            scratch = ArrayPool<byte>.Shared.Rent(track.Sizes[i] + 64);
                        }
                        int size = VideoSamples.Write(video.Codec, data, scratch);
                        if (size != track.Sizes[i])
                            throw new InvalidOperationException("Размер кадра изменился между проходами");
                        output.Write(scratch, 0, size);
                    }
                    else
                    {
                        output.Write(track.AudioFrames![i]);
                    }
                    written += track.Sizes[i];
                }
                if (progress is not null && written - lastReport > 4 << 20)
                {
                    lastReport = written;
                    progress(Math.Clamp(written / (double)Math.Max(1, dataBytes), 0, 1));
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }

        if (written != dataBytes) throw new InvalidOperationException("Записано не столько, сколько объявлено");
        progress?.Invoke(1);
        return ftyp.Length + moov.Length + mdatHeader + dataBytes;
    }

    private static byte[] BuildMoov(List<Track> tracks, bool co64)
    {
        var w = new BoxWriter();
        w.Begin("moov");

        long movieDuration = 0;
        foreach (var t in tracks)
            movieDuration = Math.Max(movieDuration,
                (long)Mp4Boxes.ToMovie(t.MediaDuration, t.Timescale) + t.StartTicks / 10_000);
        Mp4Boxes.Mvhd(w, (ulong)movieDuration, tracks.Count + 1);

        for (int i = 0; i < tracks.Count; i++)
            WriteTrak(w, tracks[i], i + 1, co64);

        w.End();
        return w.ToArray();
    }

    private static void WriteTrak(BoxWriter w, Track t, int trackId, bool co64)
    {
        ulong mediaMovie = Mp4Boxes.ToMovie(t.MediaDuration, t.Timescale);
        long delayMovie = t.StartTicks > 0 ? t.StartTicks / 10_000 : 0;

        w.Begin("trak");
        Mp4Boxes.Tkhd(w, trackId, mediaMovie + (ulong)delayMovie, t.Audio,
                      t.Video?.Width ?? 0, t.Video?.Height ?? 0);

        // Звук начался позже видео (устройство поднялось с опозданием): пустой
        // отрезок в начале, иначе звук уехал бы вперёд на эту разницу. Видео с
        // B-кадрами: первый кадр показывается на свой сдвиг позже, чем декодируется, —
        // показ начинаем с него, иначе картинка отстала бы от звука.
        // Звук, начавшийся чуть раньше видео (снимок берёт кадр AAC, который
        // начинается до первого кадра меньше чем на половину своей длины), тоже
        // срезаем по началу видео — иначе звук опередил бы картинку на эти миллисекунды.
        long mediaStart = !t.Audio ? (t.CtsOffsets.Length > 0 ? t.CtsOffsets[0] : 0)
                        : t.StartTicks < 0 ? -t.StartTicks * t.Timescale / 10_000_000 : 0;
        mediaStart = Math.Clamp(mediaStart, 0, Math.Max(0, t.MediaDuration - 1));
        Mp4Boxes.Edts(w, delayMovie, Mp4Boxes.ToMovie(t.MediaDuration - mediaStart, t.Timescale), mediaStart);

        w.Begin("mdia");
        Mp4Boxes.Mdhd(w, t.Timescale, (ulong)t.MediaDuration);
        Mp4Boxes.Hdlr(w, t.Audio, t.Audio ? t.AudioFormat!.Name : "Video");
        w.Begin("minf");
        Mp4Boxes.MediaHeaderAndDinf(w, t.Audio);
        w.Begin("stbl");

        w.BeginFull("stsd", 0, 0);
        w.U32(1);
        if (t.Audio) t.AudioFormat!.WriteSampleEntry(w); else t.Video!.WriteSampleEntry(w);
        w.End();

        // stts — длительности подряд одинаковыми отрезками
        var stts = new List<(uint Count, uint Delta)>();
        foreach (uint d in t.Durations)
        {
            if (stts.Count > 0 && stts[^1].Delta == d) stts[^1] = (stts[^1].Count + 1, d);
            else stts.Add((1, d));
        }
        w.BeginFull("stts", 0, 0);
        w.U32((uint)stts.Count);
        foreach (var (count, delta) in stts) { w.U32(count); w.U32(delta); }
        w.End();

        if (t.HasCts)
        {
            var ctts = new List<(uint Count, int Offset)>();
            foreach (int o in t.CtsOffsets)
            {
                if (ctts.Count > 0 && ctts[^1].Offset == o) ctts[^1] = (ctts[^1].Count + 1, o);
                else ctts.Add((1, o));
            }
            w.BeginFull("ctts", 1, 0);
            w.U32((uint)ctts.Count);
            foreach (var (count, off) in ctts) { w.U32(count); w.I32(off); }
            w.End();
        }

        if (!t.Audio && t.SyncSamples.Count < t.Sizes.Length)
        {
            w.BeginFull("stss", 0, 0);
            w.U32((uint)t.SyncSamples.Count);
            foreach (int s in t.SyncSamples) w.U32((uint)s);
            w.End();
        }

        // stsc — число сэмплов на кусок, одинаковые подряд сворачиваются
        var stsc = new List<(uint FirstChunk, uint PerChunk)>();
        for (int c = 0; c < t.Chunks.Count; c++)
        {
            uint per = (uint)t.Chunks[c].Count;
            if (stsc.Count == 0 || stsc[^1].PerChunk != per) stsc.Add(((uint)c + 1, per));
        }
        w.BeginFull("stsc", 0, 0);
        w.U32((uint)stsc.Count);
        foreach (var (first, per) in stsc) { w.U32(first); w.U32(per); w.U32(1); }
        w.End();

        w.BeginFull("stsz", 0, 0);
        w.U32(0);
        w.U32((uint)t.Sizes.Length);
        foreach (int s in t.Sizes) w.U32((uint)s);
        w.End();

        if (co64)
        {
            w.BeginFull("co64", 0, 0);
            w.U32((uint)t.ChunkOffsets.Length);
            foreach (long o in t.ChunkOffsets) w.U64((ulong)o);
        }
        else
        {
            w.BeginFull("stco", 0, 0);
            w.U32((uint)t.ChunkOffsets.Length);
            foreach (long o in t.ChunkOffsets) w.U32((uint)o);
        }
        w.End();

        w.End(); // stbl
        w.End(); // minf
        w.End(); // mdia
        w.End(); // trak
    }
}
