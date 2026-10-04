using System.Buffers.Binary;
using TextEncoding = System.Text.Encoding;

namespace Aura.Core.Saving.Mp4;

/// <summary>
/// Превращение фрагментированного MP4 в обычный, не трогая данные кадров.
///
/// ЗАЧЕМ. Запись в файл идёт фрагментами: так файл переживает падение процесса.
/// Но Vegas (и часть старых редакторов) фрагментированный MP4 не открывает вовсе,
/// хотя DaVinci, Premiere и плееры открывают. Обычный MP4 открывают все.
///
/// КАК. В конец файла дописывается обычный moov с полными таблицами сэмплов, а
/// старый заголовок, moof и mfra переименовываются в «free». Данные остаются на
/// своих местах внутри mdat, поэтому на часовой записи это доли секунды. Новый
/// moov пишется раньше переименований: упади на середине — у файла останутся оба
/// оглавления, и фрагментированное по-прежнему читается.
/// </summary>
public static class Mp4Defragment
{
    /// <summary>Сэмплы одной дорожки для обычного оглавления.</summary>
    internal sealed class TrackIndex
    {
        public readonly List<int> Sizes = [];
        public readonly List<uint> Durations = [];
        public readonly List<int> Cts = [];
        public readonly List<int> Sync = [];                     // номера ключевых, с 1
        public readonly List<(long Offset, int Count)> Chunks = [];
        public long FirstDts = -1;
        public long MediaDuration;

        public void Add(int size, uint duration, int cts, bool key)
        {
            Sizes.Add(size);
            Durations.Add(duration);
            Cts.Add(cts);
            if (key) Sync.Add(Sizes.Count);
            MediaDuration += duration;
        }
    }

    /// <summary>Всё о дорожке, что нужно для её trak.</summary>
    internal sealed record Track(int Id, bool Audio, int Timescale, int Width, int Height, string Name,
                                 byte[] Stsd, long MediaStart, TrackIndex Index);

    /// <summary>Обычный moov по готовым индексам дорожек.</summary>
    internal static byte[] BuildMoov(IReadOnlyList<Track> tracks, bool co64)
    {
        var w = new BoxWriter();
        w.Begin("moov");
        long movie = 0;
        foreach (var t in tracks)
            movie = Math.Max(movie, DelayMovie(t) + (long)Mp4Boxes.ToMovie(t.Index.MediaDuration - t.MediaStart, t.Timescale));
        Mp4Boxes.Mvhd(w, (ulong)movie, tracks.Count == 0 ? 1 : tracks.Max(t => t.Id) + 1);
        foreach (var t in tracks) WriteTrak(w, t, co64);
        w.End();
        return w.ToArray();
    }

    /// <summary>Дорожка начинается позже фильма (звук поднялся позже видео): пустой отрезок, мс.</summary>
    private static long DelayMovie(Track t) =>
        t.Audio && t.Index.FirstDts > 0 ? t.Index.FirstDts * Mp4Boxes.MovieTimescale / t.Timescale : 0;

    private static void WriteTrak(BoxWriter w, Track t, bool co64)
    {
        var index = t.Index;
        long mediaStart = Math.Clamp(t.MediaStart, 0, Math.Max(0, index.MediaDuration - 1));
        ulong mediaMovie = Mp4Boxes.ToMovie(index.MediaDuration - mediaStart, t.Timescale);
        long delay = DelayMovie(t);

        w.Begin("trak");
        Mp4Boxes.Tkhd(w, t.Id, mediaMovie + (ulong)delay, t.Audio, t.Width, t.Height);
        Mp4Boxes.Edts(w, delay, mediaMovie, mediaStart);
        w.Begin("mdia");
        Mp4Boxes.Mdhd(w, t.Timescale, (ulong)index.MediaDuration);
        Mp4Boxes.Hdlr(w, t.Audio, t.Name);
        w.Begin("minf");
        Mp4Boxes.MediaHeaderAndDinf(w, t.Audio);
        w.Begin("stbl");
        w.Bytes(t.Stsd);

        var stts = new List<(uint Count, uint Delta)>();
        foreach (uint d in index.Durations)
        {
            if (stts.Count > 0 && stts[^1].Delta == d) stts[^1] = (stts[^1].Count + 1, d);
            else stts.Add((1, d));
        }
        w.BeginFull("stts", 0, 0);
        w.U32((uint)stts.Count);
        foreach (var (count, delta) in stts) { w.U32(count); w.U32(delta); }
        w.End();

        if (!t.Audio && index.Cts.Any(c => c != 0))
        {
            var ctts = new List<(uint Count, int Offset)>();
            foreach (int o in index.Cts)
            {
                if (ctts.Count > 0 && ctts[^1].Offset == o) ctts[^1] = (ctts[^1].Count + 1, o);
                else ctts.Add((1, o));
            }
            w.BeginFull("ctts", 1, 0);
            w.U32((uint)ctts.Count);
            foreach (var (count, off) in ctts) { w.U32(count); w.I32(off); }
            w.End();
        }

        if (!t.Audio && index.Sync.Count < index.Sizes.Count)
        {
            w.BeginFull("stss", 0, 0);
            w.U32((uint)index.Sync.Count);
            foreach (int s in index.Sync) w.U32((uint)s);
            w.End();
        }

        var stsc = new List<(uint FirstChunk, uint PerChunk)>();
        for (int c = 0; c < index.Chunks.Count; c++)
        {
            uint per = (uint)index.Chunks[c].Count;
            if (stsc.Count == 0 || stsc[^1].PerChunk != per) stsc.Add(((uint)c + 1, per));
        }
        w.BeginFull("stsc", 0, 0);
        w.U32((uint)stsc.Count);
        foreach (var (first, per) in stsc) { w.U32(first); w.U32(per); w.U32(1); }
        w.End();

        w.BeginFull("stsz", 0, 0);
        w.U32(0);
        w.U32((uint)index.Sizes.Count);
        foreach (int s in index.Sizes) w.U32((uint)s);
        w.End();

        w.BeginFull(co64 ? "co64" : "stco", 0, 0);
        w.U32((uint)index.Chunks.Count);
        foreach (var (offset, _) in index.Chunks)
            if (co64) w.U64((ulong)offset); else w.U32((uint)offset);
        w.End();

        w.End(); w.End(); w.End(); w.End(); // stbl, minf, mdia, trak
    }

    /// <summary>
    /// Дописать обычный moov и спрятать фрагментную разметку. Поток должен стоять
    /// где угодно; после вызова позиция — в конце файла.
    /// </summary>
    /// <param name="placeholderOffset">
    /// Заглушка «free» на 16 байт перед старым moov (записи с 2.0.14), -1 — её нет.
    /// С ней файл закрывается как гибридный MP4 у OBS: заглушка становится
    /// заголовком ОДНОГО mdat, который накрывает всё от неё до нового moov, со
    /// старым оглавлением, moof и mfra внутри. Снаружи остаются только ftyp, mdat
    /// и moov: так выглядит самый обычный MP4, и строгие программы не спотыкаются
    /// о десятки блоков mdat и free подряд. Без заглушки (старые записи) moof и
    /// старый moov переименовываются в «free», как раньше.
    /// </param>
    internal static void Finalize(Stream file, IReadOnlyList<Track> tracks, long headerMoovOffset,
                                  IEnumerable<long> moofOffsets, long mfraOffset, long placeholderOffset = -1)
    {
        long end = file.Length;
        bool co64 = end > uint.MaxValue - (1L << 26);
        byte[] moov = BuildMoov(tracks, co64);
        file.Position = end;
        file.Write(moov);
        file.Flush();

        // Только после того как новое оглавление целиком на диске. Упади здесь —
        // у файла будут оба оглавления, и фрагментированное по-прежнему читается.
        if (placeholderOffset >= 0)
        {
            long span = end - placeholderOffset;
            var header = new byte[16];
            if (span <= uint.MaxValue)
            {
                // Обычный 32-битный размер: его понимают все. Оставшиеся 8 байт
                // заглушки просто становятся данными внутри mdat.
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)span);
                "mdat"u8.CopyTo(header.AsSpan(4));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(header, 1);
                "mdat"u8.CopyTo(header.AsSpan(4));
                BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)span);
            }
            file.Position = placeholderOffset;
            file.Write(header);
        }
        else
        {
            Rename(file, headerMoovOffset);
            foreach (long moof in moofOffsets) Rename(file, moof);
            if (mfraOffset >= 0) Rename(file, mfraOffset);
        }
        // Совместимые марки «iso5/iso6» в ftyp обещают фрагменты; файл теперь обычный
        if (file.Length >= 32)
        {
            var ftyp = new byte[32];
            file.Position = 0;
            file.ReadExactly(ftyp);
            for (int pos = 16; pos + 4 <= Math.Min(32, (int)BinaryPrimitives.ReadUInt32BigEndian(ftyp)); pos += 4)
            {
                string brand = TextEncoding.ASCII.GetString(ftyp, pos, 4);
                if (brand is "iso5" or "iso6")
                {
                    file.Position = pos;
                    file.Write(brand == "iso5" ? "mp42"u8 : "mp41"u8);
                }
            }
        }
        file.Position = file.Length;
        file.Flush();
    }

    private static void Rename(Stream file, long boxOffset)
    {
        file.Position = boxOffset + 4;
        file.Write("free"u8);
    }

    // ---------------- Готовые файлы ----------------

    /// <summary>Фрагментированный ли это MP4 (в moov есть mvex).</summary>
    public static bool IsFragmented(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            foreach (var box in TopLevel(file))
            {
                if (box.Type != "moov") continue;
                return FindChild(file, box.Offset + box.Header, box.Offset + box.Size, "mvex") >= 0;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Нужна ли записи переделка в обычный MP4: любая старая фрагментированная
    /// запись. С 2.0.14 закрытая запись всегда обычная (гибридный MP4), а старые
    /// с двумя дорожками раньше оставались фрагментированными, и часть программ
    /// их не понимает. Смотрит только заголовок в начале файла, поэтому быстрая.
    /// </summary>
    public static bool NeedsVegasFix(string path) => IsFragmented(path);

    /// <summary>
    /// Превратить готовый фрагментированный файл в обычный. false — файл уже обычный
    /// или разобрать его не удалось (тогда он не тронут).
    /// </summary>
    public static bool ConvertFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        // Файл мог оборваться посреди фрагмента (кончилось место, пропало питание).
        // Новое оглавление, дописанное в конец такого файла, легло бы внутрь
        // объявленного, но недописанного mdat, и его никто бы не нашёл. Сначала
        // обрезаем по последнему целому фрагменту.
        var (validEnd, fragments) = CompletePrefix(file);
        if (fragments == 0) return false;
        if (validEnd < file.Length) file.SetLength(validEnd);
        var boxes = TopLevel(file).ToList();
        var moovBox = boxes.FirstOrDefault(b => b.Type == "moov");
        if (moovBox.Type is null) return false;
        if (FindChild(file, moovBox.Offset + moovBox.Header, moovBox.Offset + moovBox.Size, "mvex") < 0) return false;
        if (boxes.Count(b => b.Type == "moov") > 1) return false;

        // Описание дорожек из заголовка
        var infos = new Dictionary<int, (bool Audio, int Timescale, int Width, int Height, string Name, byte[] Stsd, long MediaStart)>();
        foreach (var trak in Children(file, moovBox.Offset + moovBox.Header, moovBox.Offset + moovBox.Size).Where(c => c.Type == "trak"))
        {
            byte[] trakBytes = Read(file, trak.Offset, (int)trak.Size);
            var tkhd = Find(trakBytes, "tkhd");
            var mdhd = Find(trakBytes, "mdia/mdhd");
            var hdlr = Find(trakBytes, "mdia/hdlr");
            var stsd = Find(trakBytes, "mdia/minf/stbl/stsd");
            var elst = Find(trakBytes, "edts/elst");
            if (tkhd.Offset < 0 || mdhd.Offset < 0 || hdlr.Offset < 0 || stsd.Offset < 0) return false;

            bool v1 = trakBytes[tkhd.Offset + 8] == 1;
            int id = (int)BinaryPrimitives.ReadUInt32BigEndian(trakBytes.AsSpan(tkhd.Offset + 12 + (v1 ? 16 : 8)));
            int width = (int)(BinaryPrimitives.ReadUInt32BigEndian(trakBytes.AsSpan(tkhd.Offset + tkhd.Size - 8)) >> 16);
            int height = (int)(BinaryPrimitives.ReadUInt32BigEndian(trakBytes.AsSpan(tkhd.Offset + tkhd.Size - 4)) >> 16);
            bool mv1 = trakBytes[mdhd.Offset + 8] == 1;
            int timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(trakBytes.AsSpan(mdhd.Offset + 12 + (mv1 ? 16 : 8)));
            string handler = TextEncoding.ASCII.GetString(trakBytes, hdlr.Offset + 16, 4);
            int nameStart = hdlr.Offset + 32;
            int nameEnd = Array.IndexOf(trakBytes, (byte)0, nameStart, hdlr.Offset + hdlr.Size - nameStart);
            string name = TextEncoding.UTF8.GetString(trakBytes, nameStart, (nameEnd < 0 ? hdlr.Offset + hdlr.Size : nameEnd) - nameStart);
            long mediaStart = 0;
            if (elst.Offset >= 0)
            {
                bool ev1 = trakBytes[elst.Offset + 8] == 1;
                int entry = elst.Offset + 16;
                mediaStart = ev1
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(trakBytes.AsSpan(entry + 8))
                    : BinaryPrimitives.ReadInt32BigEndian(trakBytes.AsSpan(entry + 4));
                if (mediaStart < 0) mediaStart = 0;
            }
            infos[id] = (handler == "soun", timescale, width, height, name,
                         trakBytes.AsSpan(stsd.Offset, stsd.Size).ToArray(), mediaStart);
        }

        // Сэмплы из фрагментов
        var indexes = infos.Keys.ToDictionary(id => id, _ => new TrackIndex());
        var moofs = new List<long>();
        foreach (var moof in boxes.Where(b => b.Type == "moof"))
        {
            moofs.Add(moof.Offset);
            byte[] data = Read(file, moof.Offset, (int)moof.Size);
            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                if (size < 8) break;
                if (TextEncoding.ASCII.GetString(data, pos + 4, 4) == "traf")
                    if (!ReadTraf(data, pos, size, moof.Offset, infos, indexes)) return false;
                pos += size;
            }
        }
        if (indexes.Values.All(i => i.Sizes.Count == 0)) return false;

        var tracks = infos.OrderBy(kv => kv.Key)
            .Where(kv => indexes[kv.Key].Sizes.Count > 0)
            .Select(kv => new Track(kv.Key, kv.Value.Audio, kv.Value.Timescale, kv.Value.Width, kv.Value.Height,
                                    kv.Value.Name, kv.Value.Stsd, kv.Value.Audio ? 0 : kv.Value.MediaStart, indexes[kv.Key]))
            .ToList();
        long mfra = boxes.FirstOrDefault(b => b.Type == "mfra") is { Type: not null } m ? m.Offset : -1;
        // Заглушка гибридного MP4 (записи с 2.0.14): «free» на 16 байт прямо перед moov
        long placeholder = boxes.FirstOrDefault(b => b.Type == "free" && b.Size == 16 &&
                                                     b.Offset + b.Size == moovBox.Offset) is { Type: not null } f
            ? f.Offset : -1;
        Finalize(file, tracks, moovBox.Offset, moofs, mfra, placeholder);
        return true;
    }

    /// <summary>
    /// Где кончается целая часть фрагментированного файла: заголовок и все
    /// фрагменты moof+mdat, дописанные полностью. Блок, чей объявленный размер
    /// выходит за конец файла, и всё после него — оборванный хвост.
    /// </summary>
    internal static (long End, int Fragments) CompletePrefix(Stream file)
    {
        long end = 0;
        int fragments = 0;
        bool moofOpen = false;
        foreach (var box in TopLevel(file))
        {
            if (box.Offset + box.Size > file.Length) break;
            switch (box.Type)
            {
                case "moof":
                    moofOpen = true;
                    break;
                case "mdat":
                    if (moofOpen) fragments++;
                    moofOpen = false;
                    end = box.Offset + box.Size;
                    break;
                default:
                    if (!moofOpen) end = box.Offset + box.Size;
                    break;
            }
        }
        return (end, fragments);
    }

    /// <summary>
    /// Спасти запись, которая осталась «.part» после падения или отключения
    /// питания: обрезать по последнему целому фрагменту, при одной звуковой
    /// дорожке сделать обычным MP4 (для Vegas) и переименовать в «… (восстановлено).mp4».
    /// Готовый обычный MP4 под именем «.part» (сбой между финализацией и
    /// переименованием) тоже публикуется, а не удаляется.
    /// null — ни целого фрагмента, ни готового файла.
    /// </summary>
    public static string? RecoverPart(string partPath)
    {
        if (!partPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return null;
        if (IsFragmented(partPath))
        {
            using (var file = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var (end, fragments) = CompletePrefix(file);
                if (fragments == 0) return null;
                if (end < file.Length) file.SetLength(end);
                file.Flush(flushToDisk: true);
            }
            // Как у закрытой записи: гибридный файл становится обычным MP4. Не вышло —
            // остаётся фрагментированным, он тоже играется.
            try { ConvertFile(partPath); }
            catch (Exception ex) { Aura.Core.Logging.Log.Warn("Recorder", $"Восстановленная запись осталась фрагментированной: {ex.Message}"); }
        }
        else if (!IsCompleteProgressive(partPath))
        {
            return null;
        }

        string final = partPath[..^".part".Length];
        string stem = Path.Combine(Path.GetDirectoryName(final)!, Path.GetFileNameWithoutExtension(final) + " (восстановлено)");
        string target = stem + ".mp4";
        for (int n = 2; File.Exists(target); n++) target = $"{stem} {n}.mp4";
        File.Move(partPath, target);
        return target;
    }

    /// <summary>
    /// Обычный MP4 целиком: есть ftyp, moov с ключевыми кадрами видео и mdat,
    /// и ни один верхний блок не выходит за конец файла.
    /// </summary>
    internal static bool IsCompleteProgressive(string path)
    {
        using (var file = File.OpenRead(path))
        {
            bool ftyp = false, moov = false, mdat = false;
            long end = 0;
            foreach (var box in TopLevel(file))
            {
                if (box.Type == "ftyp") ftyp = true;
                else if (box.Type == "moov") moov = true;
                else if (box.Type == "mdat") mdat = true;
                end = box.Offset + box.Size;
            }
            if (!ftyp || !moov || !mdat || end > file.Length) return false;
        }
        return Mp4Keyframes.Read(path).Count > 0;
    }

    /// <summary>
    /// Незавершённый файл точно пустой: ни целого фрагмента, ни готового MP4.
    /// Только такой можно удалить; ошибка чтения не доказывает бесполезность.
    /// </summary>
    public static bool IsUnrecoverablePart(string path)
    {
        if (IsFragmented(path))
        {
            using var file = File.OpenRead(path);
            return CompletePrefix(file).Fragments == 0;
        }
        return !IsCompleteProgressive(path);
    }

    private static bool ReadTraf(byte[] data, int start, int size,
                                 long moofOffset,
                                 Dictionary<int, (bool Audio, int Timescale, int Width, int Height, string Name, byte[] Stsd, long MediaStart)> infos,
                                 Dictionary<int, TrackIndex> indexes)
    {
        int end = start + size;
        int pos = start + 8;
        int trackId = -1;
        long baseOffset = moofOffset;
        uint defDuration = 0, defSize = 0, defFlags = 0;
        long dts = -1;
        TrackIndex? index = null;
        while (pos + 8 <= end)
        {
            int boxSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (boxSize < 8) return false;
            string type = TextEncoding.ASCII.GetString(data, pos + 4, 4);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 8)) & 0xFFFFFF;
            byte version = data[pos + 8];
            int p = pos + 12;
            if (type == "tfhd")
            {
                trackId = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4;
                if (!indexes.TryGetValue(trackId, out index)) return false;
                if ((flags & 0x1) != 0) { baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p)); p += 8; }
                if ((flags & 0x2) != 0) p += 4;
                if ((flags & 0x8) != 0) { defDuration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
                if ((flags & 0x10) != 0) { defSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
                if ((flags & 0x20) != 0) { defFlags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); }
            }
            else if (type == "tfdt" && index is not null)
            {
                dts = version == 1 ? (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p))
                                   : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
                if (index.FirstDts < 0) index.FirstDts = dts;
            }
            else if (type == "trun" && index is not null)
            {
                bool audio = infos[trackId].Audio;
                int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4;
                long offset = baseOffset;
                if ((flags & 0x1) != 0) { offset += BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p)); p += 4; }
                uint firstFlags = defFlags;
                bool hasFirstFlags = (flags & 0x4) != 0;
                if (hasFirstFlags) { firstFlags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
                index.Chunks.Add((offset, count));
                if (index.FirstDts < 0) index.FirstDts = Math.Max(0, dts);
                for (int i = 0; i < count; i++)
                {
                    uint duration = defDuration, sampleSize = defSize, sampleFlags = i == 0 && hasFirstFlags ? firstFlags : defFlags;
                    int cts = 0;
                    if ((flags & 0x100) != 0) { duration = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
                    if ((flags & 0x200) != 0) { sampleSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
                    if ((flags & 0x400) != 0) { sampleFlags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)); p += 4; }
                    if ((flags & 0x800) != 0) { cts = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p)); p += 4; }
                    // sample_is_non_sync_sample — бит 16 флагов
                    bool key = audio || (sampleFlags & 0x00010000) == 0;
                    index.Add((int)sampleSize, Math.Max(1, duration), cts, key);
                }
            }
            pos += boxSize;
        }
        return true;
    }

    private readonly record struct Box(string Type, long Offset, long Size, int Header);

    private static IEnumerable<Box> TopLevel(Stream file) => Children(file, 0, file.Length);

    private static IEnumerable<Box> Children(Stream file, long start, long end)
    {
        var header = new byte[16];
        long pos = start;
        while (pos + 8 <= end)
        {
            file.Position = pos;
            if (file.Read(header, 0, 8) < 8) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = TextEncoding.ASCII.GetString(header, 4, 4);
            int headerSize = 8;
            if (size == 1)
            {
                if (file.Read(header, 8, 8) < 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < headerSize) yield break;
            yield return new Box(type, pos, size, headerSize);
            pos += size;
        }
    }

    private static long FindChild(Stream file, long start, long end, string type) =>
        Children(file, start, end).FirstOrDefault(b => b.Type == type) is { Type: not null } box ? box.Offset : -1;

    private static byte[] Read(Stream file, long offset, int length)
    {
        var buffer = new byte[length];
        file.Position = offset;
        file.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Вложенный блок по пути внутри готового блока (путь от его детей).</summary>
    private static (int Offset, int Size) Find(byte[] box, string path)
    {
        int start = 8, end = box.Length;
        (int Offset, int Size) found = (-1, 0);
        foreach (string part in path.Split('/'))
        {
            found = (-1, 0);
            int pos = start;
            while (pos + 8 <= end)
            {
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(pos));
                if (size < 8) break;
                if (TextEncoding.ASCII.GetString(box, pos + 4, 4) == part) { found = (pos, size); break; }
                pos += size;
            }
            if (found.Offset < 0) return found;
            start = found.Offset + 8;
            end = found.Offset + found.Size;
        }
        return found;
    }
}
