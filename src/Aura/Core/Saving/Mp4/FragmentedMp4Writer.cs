namespace Aura.Core.Saving.Mp4;

/// <summary>
/// Фрагментированный MP4 для обычной записи в файл: оглавление в начале описывает
/// только дорожки, а кадры идут фрагментами (moof + mdat) по мере записи.
///
/// ЗАЧЕМ ФРАГМЕНТЫ. Запись может идти часами, и если процесс упадёт или пропадёт
/// питание, обычный MP4 без финального moov не откроется вовсе. Каждый дописанный
/// фрагмент здесь самодостаточен: файл играется до последнего целого фрагмента.
///
/// ЗАЧЕМ СВОЙ. Писатель Media Foundation адресует данные 32-битными смещениями,
/// поэтому запись приходилось резать на части по 3.5 ГБ. Во фрагментах смещения
/// отсчитываются от начала своего moof, и длина файла не ограничена.
///
/// Не потокобезопасен: все вызовы — из одного потока писателя.
/// </summary>
public sealed class FragmentedMp4Writer : IDisposable
{
    /// <summary>Фрагмент закрывается на ключевом кадре, когда накопилось не меньше этого.</summary>
    private const long FragmentTicks = 20_000_000; // 2 с

    private sealed class Pending
    {
        public readonly List<(int Size, long Dts, int Cts, bool Key)> Samples = [];
        public readonly MemoryStream Data = new();
        public long FirstDts = -1;
    }

    private readonly Stream _out;
    private readonly Mp4VideoFormat _video;
    private readonly IReadOnlyList<Mp4AudioFormat> _audio;
    private readonly Pending _videoPending = new();
    private readonly Pending[] _audioPending;
    private readonly long[] _audioNextDts;   // в сэмплах 48 кГц
    private readonly List<(long Time, long MoofOffset)> _randomAccess = [];
    private uint _sequence;
    private long _mehdOffset;
    private long _lastVideoDts;
    private bool _hasVideo;   // время декодирования с B-кадрами бывает отрицательным — это не признак
    private long _videoEnd;
    private byte[] _scratch = new byte[1 << 20];
    private bool _finished;

    /// <summary>
    /// Всё о записанных сэмплах, чтобы в конце дописать обычное оглавление. Vegas
    /// (и часть старых редакторов) фрагментированный MP4 не открывает вовсе, а
    /// DaVinci, Premiere и плееры открывают. Поэтому при закрытии файла он
    /// превращается в обычный: кусок на дорожку в каждом фрагменте, данные остаются
    /// на месте, переписывается лишь несколько байт и дописывается moov в конец.
    /// </summary>
    private readonly Mp4Defragment.TrackIndex _videoIndex = new();
    private Mp4Defragment.TrackIndex[] _audioIndex = [];
    private long _headerMoovOffset = -1;
    private readonly List<long> _moofOffsets = [];
    private long _mfraOffset = -1;

    public FragmentedMp4Writer(Stream output, Mp4VideoFormat video, IReadOnlyList<Mp4AudioFormat> audio)
    {
        _out = output;
        _video = video;
        _audio = audio;
        _audioPending = new Pending[audio.Count];
        _audioNextDts = new long[audio.Count];
        for (int i = 0; i < audio.Count; i++) { _audioPending[i] = new Pending(); _audioNextDts[i] = -1; }
        _audioIndex = new Mp4Defragment.TrackIndex[audio.Count];
        for (int i = 0; i < audio.Count; i++) _audioIndex[i] = new Mp4Defragment.TrackIndex();
    }

    private bool _headerWritten;

    /// <summary>Сколько байт уже в файле.</summary>
    public long BytesWritten => _out.Position;

    /// <summary>Длительность записанного видео, 100-нс тики.</summary>
    public long DurationTicks => _videoEnd * 10_000_000 / _video.Timescale;

    /// <summary>
    /// Сдвиг показа первого кадра относительно декодирования (B-кадры): первый кадр
    /// показывается позже, чем декодируется. В edts показ начинается с него — как
    /// делает сам ffmpeg во фрагментированных файлах. Отрицательные сдвиги в trun
    /// пробовали: ffmpeg и плееры на нём тогда сдвигают картинку на кадр вперёд
    /// относительно звука.
    /// </summary>
    private int _ctsShift;

    /// <summary>Заголовок пишется на первом кадре: к нему уже известен сдвиг показа.</summary>
    private void WriteHeader(int firstCts)
    {
        _headerWritten = true;
        _ctsShift = firstCts;   // для диагностики: сдвиг ушёл в edts
        var w = new BoxWriter();
        Mp4Boxes.Ftyp(w, fragmented: true, _video.Codec);

        _headerMoovOffset = _out.Position + w.Length;
        w.Begin("moov");
        Mp4Boxes.Mvhd(w, 0, _audio.Count + 2);
        WriteEmptyTrak(w, 1, audio: false, null, firstCts);
        for (int i = 0; i < _audio.Count; i++) WriteEmptyTrak(w, i + 2, audio: true, _audio[i], 0);

        w.Begin("mvex");
        // Общая длительность; проставляется при закрытии файла. Пока запись идёт,
        // плееры узнают её сами, обходя фрагменты.
        w.BeginFull("mehd", 1, 0);
        _mehdOffset = w.Length;
        w.U64(0);
        w.End();
        for (int id = 1; id <= _audio.Count + 1; id++)
        {
            w.BeginFull("trex", 0, 0);
            w.U32((uint)id);
            w.U32(1);   // sample description index
            w.U32(0); w.U32(0); w.U32(0);
            w.End();
        }
        w.End(); // mvex
        w.End(); // moov

        // Заголовок пишется с начала файла, поэтому смещение mehd в буфере и в файле совпадает.
        _out.Write(w.Span);
        _out.Flush();
    }

    private void WriteEmptyTrak(BoxWriter w, int trackId, bool audio, Mp4AudioFormat? format, int mediaStart)
    {
        w.Begin("trak");
        Mp4Boxes.Tkhd(w, trackId, 0, audio, audio ? 0 : _video.Width, audio ? 0 : _video.Height);
        Mp4Boxes.Edts(w, 0, 0, mediaStart);
        w.Begin("mdia");
        Mp4Boxes.Mdhd(w, audio ? format!.SampleRate : _video.Timescale, 0);
        Mp4Boxes.Hdlr(w, audio, audio ? format!.Name : "Video");
        w.Begin("minf");
        Mp4Boxes.MediaHeaderAndDinf(w, audio);
        w.Begin("stbl");
        w.BeginFull("stsd", 0, 0);
        w.U32(1);
        if (audio) format!.WriteSampleEntry(w); else _video.WriteSampleEntry(w);
        w.End();
        foreach (string box in new[] { "stts", "stsc", "stco" })
        {
            w.BeginFull(box, 0, 0);
            w.U32(0);
            w.End();
        }
        w.BeginFull("stsz", 0, 0);
        w.U32(0); w.U32(0);
        w.End();
        w.End(); w.End(); w.End(); w.End();
    }

    /// <summary>Кадр видео в Annex B. Время — 100-нс тики от начала записи.</summary>
    public void WriteVideo(ReadOnlySpan<byte> annexB, long pts, long dts, bool keyframe)
    {
        if (_finished) return;
        if (!_hasVideo && !keyframe) return;
        if (!_headerWritten) WriteHeader(_video.CtsUnits(pts - dts));

        // Время в единицах шкалы дорожки считается нарастающим итогом по промежуткам
        // между кадрами — так сетка остаётся ровной (см. Mp4VideoFormat.FrameRate).
        long units = !_hasVideo ? 0 : _lastVideoUnits + _video.ToUnits(dts - _lastVideoDts);

        // Фрагмент закрываем ПЕРЕД ключевым кадром: каждый фрагмент начинается с
        // него, и с любого можно начать воспроизведение.
        if (keyframe && _videoPending.Samples.Count > 0 &&
            (units - _videoPending.FirstDts) * 10_000_000 / _video.Timescale >= FragmentTicks)
            Flush(nextVideoDts: units);

        if (_scratch.Length < annexB.Length + 64) _scratch = new byte[annexB.Length * 2 + 64];
        int size = VideoSamples.Write(_video.Codec, annexB, _scratch);
        if (size == 0) return;
        _videoPending.Data.Write(_scratch, 0, size);
        if (_videoPending.FirstDts < 0) _videoPending.FirstDts = units;
        _videoPending.Samples.Add((size, units, _video.CtsUnits(pts - dts), keyframe));
        _lastVideoDts = dts;
        _hasVideo = true;
        _lastVideoUnits = units;
    }

    private long _lastVideoUnits;

    /// <summary>Кадр AAC дорожки <paramref name="track"/>. Время — 100-нс тики от начала записи.</summary>
    public void WriteAudio(int track, ReadOnlySpan<byte> frame, long pts)
    {
        if (_finished || track >= _audio.Count || !_headerWritten) return;
        var pending = _audioPending[track];
        int rate = _audio[track].SampleRate;
        long position = pts * rate / 10_000_000;         // где кадр должен лежать, в сэмплах
        if (_audioNextDts[track] < 0)
        {
            if (pts < -Mp4AudioFormat.FrameSamples * 10_000_000L / rate / 2) return;
            _audioNextDts[track] = Math.Max(0, position);
        }
        else
        {
            // Кадры в дорожке идут подряд: разрыв (перезапуск звука) заполняем
            // тишиной, иначе весь звук после него съехал бы назад; налезающий на уже
            // записанное кадр пропускаем.
            long gap = position - _audioNextDts[track];
            if (gap < -Mp4AudioFormat.FrameSamples / 2) return;
            byte[]? silence = _audio[track].SilentFrame;
            if (gap > Mp4AudioFormat.FrameSamples / 2 && silence is not null && gap <= 60L * rate)
            {
                long missing = (gap + Mp4AudioFormat.FrameSamples / 2) / Mp4AudioFormat.FrameSamples;
                for (long m = 0; m < missing; m++)
                {
                    pending.Data.Write(silence);
                    if (pending.FirstDts < 0) pending.FirstDts = _audioNextDts[track];
                    pending.Samples.Add((silence.Length, _audioNextDts[track], 0, true));
                    _audioNextDts[track] += Mp4AudioFormat.FrameSamples;
                }
            }
        }
        pending.Data.Write(frame);
        if (pending.FirstDts < 0) pending.FirstDts = _audioNextDts[track];
        pending.Samples.Add((frame.Length, _audioNextDts[track], 0, true));
        _audioNextDts[track] += Mp4AudioFormat.FrameSamples;
    }

    /// <summary>Записать накопленное одним фрагментом.</summary>
    private void Flush(long nextVideoDts)
    {
        bool hasVideo = _videoPending.Samples.Count > 0;
        bool hasAudio = _audioPending.Any(p => p.Samples.Count > 0);
        if (!hasVideo && !hasAudio) return;

        long moofOffset = _out.Position;
        var w = new BoxWriter();
        w.Begin("moof");
        w.BeginFull("mfhd", 0, 0);
        w.U32(++_sequence);
        w.End();

        // Смещения данных в trun отсчитываются от начала moof, а размер moof
        // зависит только от числа сэмплов — заполняем их вторым проходом.
        var dataOffsetFields = new List<int>();
        if (hasVideo)
            dataOffsetFields.Add(WriteTraf(w, 1, _videoPending, video: true, nextVideoDts));
        for (int i = 0; i < _audioPending.Length; i++)
            if (_audioPending[i].Samples.Count > 0)
                dataOffsetFields.Add(WriteTraf(w, i + 2, _audioPending[i], video: false, 0));
        w.End(); // moof

        long mdatSize = 8 + _videoPending.Data.Length + _audioPending.Sum(p => p.Data.Length);
        long cursor = w.Length + 8;
        int field = 0;
        if (hasVideo)
        {
            w.PatchU32(dataOffsetFields[field++], (uint)cursor);
            cursor += _videoPending.Data.Length;
        }
        foreach (var p in _audioPending)
        {
            if (p.Samples.Count == 0) continue;
            w.PatchU32(dataOffsetFields[field++], (uint)cursor);
            cursor += p.Data.Length;
        }

        // Индекс для обычного оглавления: где в файле лежит каждый кусок
        long dataStart = moofOffset + w.Length + 8;
        long chunkOffset = dataStart;
        if (hasVideo)
        {
            Remember(_videoIndex, _videoPending, chunkOffset, video: true, nextVideoDts);
            chunkOffset += _videoPending.Data.Length;
        }
        for (int i = 0; i < _audioPending.Length; i++)
        {
            if (_audioPending[i].Samples.Count == 0) continue;
            Remember(_audioIndex[i], _audioPending[i], chunkOffset, video: false, 0);
            chunkOffset += _audioPending[i].Data.Length;
        }
        _moofOffsets.Add(moofOffset);

        _out.Write(w.Span);
        var header = new BoxWriter();
        header.U32((uint)mdatSize);
        header.FourCc("mdat");
        _out.Write(header.Span);
        if (hasVideo) _videoPending.Data.WriteTo(_out);
        foreach (var p in _audioPending) if (p.Samples.Count > 0) p.Data.WriteTo(_out);
        _out.Flush();

        if (hasVideo)
        {
            _randomAccess.Add((_videoPending.FirstDts, moofOffset));
            _videoEnd = nextVideoDts;
        }
        Reset(_videoPending);
        foreach (var p in _audioPending) Reset(p);
    }

    private static void Remember(Mp4Defragment.TrackIndex index, Pending p, long offset, bool video, long nextVideoDts)
    {
        if (index.FirstDts < 0) index.FirstDts = p.FirstDts;
        index.Chunks.Add((offset, p.Samples.Count));
        for (int i = 0; i < p.Samples.Count; i++)
        {
            var s = p.Samples[i];
            long next = i + 1 < p.Samples.Count
                ? p.Samples[i + 1].Dts
                : video ? nextVideoDts : s.Dts + Mp4AudioFormat.FrameSamples;
            index.Add(s.Size, (uint)Math.Max(1, next - s.Dts), s.Cts, s.Key);
        }
    }

    private static void Reset(Pending p)
    {
        p.Samples.Clear();
        p.Data.SetLength(0);
        p.FirstDts = -1;
    }

    /// <summary>traf одной дорожки; возвращает смещение поля data_offset внутри moof.</summary>
    private static int WriteTraf(BoxWriter w, int trackId, Pending p, bool video, long nextVideoDts)
    {
        w.Begin("traf");
        w.BeginFull("tfhd", 0, 0x020000);  // default-base-is-moof
        w.U32((uint)trackId);
        w.End();
        w.BeginFull("tfdt", 1, 0);
        w.U64((ulong)Math.Max(0, p.FirstDts));
        w.End();

        bool hasCts = video && p.Samples.Any(s => s.Cts != 0);
        // data-offset | duration | size | (флаги сэмпла для видео) | (cts)
        uint flags = 0x000001 | 0x000100 | 0x000200 | (video ? 0x000400u : 0) | (hasCts ? 0x000800u : 0);
        w.BeginFull("trun", hasCts ? (byte)1 : (byte)0, flags);
        w.U32((uint)p.Samples.Count);
        int dataOffsetField = w.Length;
        w.I32(0);
        for (int i = 0; i < p.Samples.Count; i++)
        {
            var s = p.Samples[i];
            long next = i + 1 < p.Samples.Count
                ? p.Samples[i + 1].Dts
                : video ? nextVideoDts : s.Dts + Mp4AudioFormat.FrameSamples;
            w.U32((uint)Math.Max(1, next - s.Dts));
            w.U32((uint)s.Size);
            if (video) w.U32(s.Key ? 0x02000000u : 0x01010000u);
            if (hasCts) w.I32(s.Cts);
        }
        w.End();
        w.End();
        return dataOffsetField;
    }

    /// <summary>
    /// Дописать хвост и закрыть файл. <paramref name="lastFrameTicks"/> —
    /// длительность последнего кадра видео.
    /// </summary>
    public void Finish(long lastFrameTicks)
    {
        if (_finished) return;
        _finished = true;
        if (!_headerWritten) return;   // ни одного кадра — писать нечего
        Flush(_hasVideo ? _lastVideoUnits + _video.ToUnits(lastFrameTicks) : 0);

        // mfra: таблица быстрого перехода по фрагментам — для перемотки.
        if (_randomAccess.Count > 0)
        {
            _mfraOffset = _out.Position;
            var w = new BoxWriter();
            w.Begin("mfra");
            w.BeginFull("tfra", 1, 0);
            w.U32(1);                  // track_ID видео
            w.U32(0);                  // размеры полей номеров — по байту
            w.U32((uint)_randomAccess.Count);
            foreach (var (time, moof) in _randomAccess)
            {
                w.U64((ulong)Math.Max(0, time));
                w.U64((ulong)moof);
                w.U8(1); w.U8(1); w.U8(1);
            }
            w.End();
            w.BeginFull("mfro", 0, 0);
            w.U32((uint)(w.Length + 4));
            w.End();
            w.End();
            _out.Write(w.Span);
        }

        // Общая длительность в mehd (шкала фильма — миллисекунды)
        long end = _out.Position;
        _out.Position = _mehdOffset;
        var d = new BoxWriter();
        d.U64(Mp4Boxes.ToMovie(_videoEnd, _video.Timescale));
        _out.Write(d.Span);
        _out.Position = end;
        _out.Flush();

        // Фрагментированный файл по умолчанию и остаётся таким: так его понимают
        // плееры, DaVinci и Premiere. Vegas же не открывает его, когда звуковая
        // дорожка одна (например, микрофон выключен), а с двумя открывает. Только в
        // этом случае делаем файл обычным; любой другой можно переделать из меню клипа.
        if (_audio.Count < 2) ConvertToProgressive();
    }

    /// <summary>
    /// Сделать из фрагментированного файла обычный, не трогая данные (см.
    /// <see cref="Mp4Defragment"/>). Пока запись идёт, файл остаётся фрагментированным
    /// и переживает падение; превращение — только в самом конце.
    /// </summary>
    private void ConvertToProgressive()
    {
        if (_headerMoovOffset < 0 || _videoIndex.Sizes.Count == 0) return;
        var tracks = new List<Mp4Defragment.Track>
        {
            new(1, false, _video.Timescale, _video.Width, _video.Height, "Video",
                SampleDescription(w => _video.WriteSampleEntry(w)), _ctsShift, _videoIndex)
        };
        for (int i = 0; i < _audio.Count; i++)
        {
            if (_audioIndex[i].Sizes.Count == 0) continue;
            var format = _audio[i];
            tracks.Add(new(i + 2, true, format.SampleRate, 0, 0, format.Name,
                           SampleDescription(w => format.WriteSampleEntry(w)), 0, _audioIndex[i]));
        }
        Mp4Defragment.Finalize(_out, tracks, _headerMoovOffset, _moofOffsets, _mfraOffset);
    }

    private static byte[] SampleDescription(Action<BoxWriter> entry)
    {
        var w = new BoxWriter();
        w.BeginFull("stsd", 0, 0);
        w.U32(1);
        entry(w);
        w.End();
        return w.ToArray();
    }

    public void Dispose()
    {
        _videoPending.Data.Dispose();
        foreach (var p in _audioPending) p.Data.Dispose();
    }
}
