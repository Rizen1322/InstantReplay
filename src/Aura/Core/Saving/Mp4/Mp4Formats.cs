namespace Aura.Core.Saving.Mp4;

/// <summary>Описание видеодорожки.</summary>
public sealed record Mp4VideoFormat(
    Mp4VideoCodec Codec, int Width, int Height, byte[] DecoderConfig, bool TenBit = false)
{
    /// <summary>
    /// Частота кадров записи. Задана — шкала времени дорожки fps × 1000, и каждый
    /// кадр длится ровно 1000 единиц.
    ///
    /// ЗАЧЕМ. Сетка кадров конвейера — целые 100-нс тики: 1/60 с = 166 666 тиков
    /// вместо 166 666.67. В файле со шкалой 10 МГц это читается как 60.0002 или
    /// 59.9999 кадра в секунду, и MediaInfo, редакторы и плееры так и пишут.
    /// Перевод промежутков между кадрами в целое число кадровых слотов даёт ровно 60.
    /// </summary>
    public int FrameRate { get; init; }

    /// <summary>Шкала времени видеодорожки.</summary>
    public int Timescale => FrameRate > 0 ? FrameRate * 1000 : 10_000_000;

    /// <summary>Промежуток в 100-нс тиках → единицы шкалы (для длительностей: не меньше одного слота).</summary>
    public long ToUnits(long deltaTicks) => FrameRate > 0
        ? Math.Max(1, (long)Math.Round(deltaTicks * FrameRate / 10_000_000.0)) * 1000
        : Math.Max(1, deltaTicks);

    /// <summary>Сдвиг показа относительно декодирования → единицы шкалы.</summary>
    public int CtsUnits(long ctsTicks) => FrameRate > 0
        ? checked((int)Math.Round(ctsTicks * FrameRate / 10_000_000.0) * 1000)
        : checked((int)ctsTicks);

    /// <summary>
    /// Собрать описание из заголовка последовательности энкодера (Annex B:
    /// VPS/SPS/PPS). Если в нём чего-то не хватает, добирается из ключевого кадра.
    /// </summary>
    public static Mp4VideoFormat FromBitstream(Mp4VideoCodec codec, int width, int height,
                                              ReadOnlySpan<byte> sequenceHeader, ReadOnlySpan<byte> keyframe)
    {
        if (codec == Mp4VideoCodec.Av1)
        {
            byte[] obu = Av1Obu.FindSequenceHeader(sequenceHeader, keyframe)
                ?? throw new InvalidOperationException("В потоке AV1 нет заголовка последовательности");
            byte[] av1C = Av1Obu.BuildAv1C(obu, out bool tenBit);
            return new Mp4VideoFormat(codec, width, height, av1C, tenBit);
        }

        var sets = NalUnits.ExtractParameterSets(codec, sequenceHeader);
        if (!sets.Complete(codec))
        {
            var fromFrame = NalUnits.ExtractParameterSets(codec, keyframe);
            if (sets.Vps.Count == 0) sets.Vps.AddRange(fromFrame.Vps);
            if (sets.Sps.Count == 0) sets.Sps.AddRange(fromFrame.Sps);
            if (sets.Pps.Count == 0) sets.Pps.AddRange(fromFrame.Pps);
        }
        if (!sets.Complete(codec))
            throw new InvalidOperationException("В потоке нет наборов параметров кодека (SPS/PPS)");

        if (codec == Mp4VideoCodec.H264)
        {
            var info = H264SpsInfo.Parse(sets.Sps[0]);
            return new Mp4VideoFormat(codec, width, height, NalUnits.BuildAvcC(sets), info.BitDepthLumaMinus8 > 0);
        }

        var hevc = HevcSpsInfo.Parse(sets.Sps[0]);
        return new Mp4VideoFormat(codec, width, height, NalUnits.BuildHvcC(sets, 0), hevc.BitDepthLumaMinus8 > 0);
    }

    internal string SampleEntryType => Codec switch
    {
        Mp4VideoCodec.H264 => "avc1", Mp4VideoCodec.Hevc => "hvc1", _ => "av01"
    };

    internal void WriteSampleEntry(BoxWriter w)
    {
        w.Begin(SampleEntryType);
        w.Zeros(6);
        w.U16(1);                      // data_reference_index
        w.U16(0); w.U16(0);            // pre_defined, reserved
        w.Zeros(12);                   // pre_defined
        w.U16((ushort)Width);
        w.U16((ushort)Height);
        w.U32(0x00480000);             // 72 dpi
        w.U32(0x00480000);
        w.U32(0);
        w.U16(1);                      // frame_count
        var name = new byte[32];
        byte[] text = "Aura"u8.ToArray();
        name[0] = (byte)text.Length;
        text.CopyTo(name, 1);
        w.Bytes(name);
        w.U16(0x0018);                 // depth
        w.U16(0xFFFF);                 // pre_defined = -1

        w.Begin(Codec switch { Mp4VideoCodec.H264 => "avcC", Mp4VideoCodec.Hevc => "hvcC", _ => "av1C" });
        w.Bytes(DecoderConfig);
        w.End();

        // Цвет явно: BT.709, ограниченный диапазон — ровно то, что выдаёт
        // видеопроцессор. Без этого бокса браузеры и часть плееров гадают сами.
        w.Begin("colr");
        w.FourCc("nclx");
        w.U16(1); w.U16(1); w.U16(1);  // BT.709 первичные, передача, матрица
        w.U8(0);                       // limited range
        w.End();

        w.Begin("pasp");
        w.U32(1); w.U32(1);
        w.End();

        w.End();
    }
}

/// <summary>Описание звуковой дорожки AAC.</summary>
public sealed record Mp4AudioFormat(
    int SampleRate, int Channels, byte[] AudioSpecificConfig, int Bitrate, string Name)
{
    /// <summary>Сэмплов в кадре AAC-LC.</summary>
    public const int FrameSamples = 1024;

    /// <summary>
    /// Кадр тишины этого же кодера — им писатель заполняет разрывы в звуке
    /// (перезапуск звука при смене устройства). null — разрывы не заполняются.
    /// </summary>
    public byte[]? SilentFrame { get; init; }

    internal void WriteSampleEntry(BoxWriter w)
    {
        w.Begin("mp4a");
        w.Zeros(6);
        w.U16(1);                      // data_reference_index
        w.U32(0); w.U32(0);
        w.U16((ushort)Channels);
        w.U16(16);                     // samplesize
        w.U16(0); w.U16(0);
        w.U32((uint)SampleRate << 16);

        w.BeginFull("esds", 0, 0);
        // ES_Descriptor
        int decoderSpecific = 2 + AudioSpecificConfig.Length;
        int decoderConfig = 2 + 13 + decoderSpecific;
        int sl = 3;
        w.U8(0x03);
        w.U8((byte)(3 + decoderConfig + sl));
        w.U16(0);                      // ES_ID
        w.U8(0);                       // flags
        // DecoderConfigDescriptor
        w.U8(0x04);
        w.U8((byte)(13 + decoderSpecific));
        w.U8(0x40);                    // MPEG-4 Audio
        w.U8(0x15);                    // AudioStream, upstream = 0, reserved = 1
        w.U24(1536 * (uint)Channels);  // bufferSizeDB
        w.U32((uint)Bitrate);          // maxBitrate
        w.U32((uint)Bitrate);          // avgBitrate
        // DecoderSpecificInfo
        w.U8(0x05);
        w.U8((byte)AudioSpecificConfig.Length);
        w.Bytes(AudioSpecificConfig);
        // SLConfigDescriptor
        w.U8(0x06);
        w.U8(1);
        w.U8(0x02);
        w.End();

        w.End();
    }

    /// <summary>
    /// AudioSpecificConfig из MF_MT_USER_DATA энкодера AAC: там сначала 12 байт
    /// хвоста HEAACWAVEINFO, затем сам AudioSpecificConfig.
    /// </summary>
    public static byte[] AscFromMfUserData(byte[] userData) =>
        userData.Length > 12 ? userData[12..] : throw new InvalidOperationException("Нет AudioSpecificConfig");

    /// <summary>AudioSpecificConfig для AAC-LC без внешнего источника.</summary>
    public static byte[] AacLcConfig(int sampleRate, int channels)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int index = Array.IndexOf(rates, sampleRate);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        int value = (2 << 11) | (index << 7) | (channels << 3); // objectType 2 = AAC-LC
        return [(byte)(value >> 8), (byte)value];
    }
}

/// <summary>Общие части moov для обоих писателей.</summary>
internal static class Mp4Boxes
{
    public const int MovieTimescale = 1000;

    public static void Ftyp(BoxWriter w, bool fragmented, Mp4VideoCodec codec)
    {
        w.Begin("ftyp");
        w.FourCc("isom");
        w.U32(0x200);
        w.FourCc("isom");
        w.FourCc("iso2");
        if (fragmented) { w.FourCc("iso5"); w.FourCc("iso6"); }
        w.FourCc(codec switch { Mp4VideoCodec.H264 => "avc1", Mp4VideoCodec.Hevc => "hvc1", _ => "av01" });
        w.FourCc("mp41");
        w.End();
    }

    public static void Mvhd(BoxWriter w, ulong durationMovie, int nextTrackId)
    {
        w.BeginFull("mvhd", 1, 0);
        w.U64(0); w.U64(0);            // creation/modification
        w.U32(MovieTimescale);
        w.U64(durationMovie);
        w.U32(0x00010000);             // rate 1.0
        w.U16(0x0100);                 // volume 1.0
        w.Zeros(10);
        w.UnityMatrix();
        w.Zeros(24);
        w.U32((uint)nextTrackId);
        w.End();
    }

    public static void Tkhd(BoxWriter w, int trackId, ulong durationMovie, bool audio, int width, int height)
    {
        w.BeginFull("tkhd", 1, 0x000003); // enabled | in_movie
        w.U64(0); w.U64(0);
        w.U32((uint)trackId);
        w.U32(0);
        w.U64(durationMovie);
        w.Zeros(8);
        w.U16(0);                      // layer
        // alternate_group = 0: раздельные дорожки игры и микрофона — не альтернативы
        // друг другу, а части одного звука. Плееры, которые играют все включённые
        // дорожки (QuickTime, редакторы), сведут их сами.
        w.U16(0);
        w.U16(audio ? (ushort)0x0100 : (ushort)0);
        w.U16(0);
        w.UnityMatrix();
        w.U32((uint)width << 16);
        w.U32((uint)height << 16);
        w.End();
    }

    public static void Mdhd(BoxWriter w, int timescale, ulong duration)
    {
        w.BeginFull("mdhd", 1, 0);
        w.U64(0); w.U64(0);
        w.U32((uint)timescale);
        w.U64(duration);
        w.U16(0x55C4);                 // 'und'
        w.U16(0);
        w.End();
    }

    public static void Hdlr(BoxWriter w, bool audio, string name)
    {
        w.BeginFull("hdlr", 0, 0);
        w.U32(0);
        w.FourCc(audio ? "soun" : "vide");
        w.Zeros(12);
        w.CString(name);
        w.End();
    }

    public static void MediaHeaderAndDinf(BoxWriter w, bool audio)
    {
        if (audio)
        {
            w.BeginFull("smhd", 0, 0);
            w.U16(0); w.U16(0);
            w.End();
        }
        else
        {
            w.BeginFull("vmhd", 0, 1);
            w.Zeros(8);
            w.End();
        }
        w.Begin("dinf");
        w.BeginFull("dref", 0, 0);
        w.U32(1);
        w.BeginFull("url ", 0, 1);     // данные в этом же файле
        w.End();
        w.End();
        w.End();
    }

    /// <summary>
    /// Список правок дорожки. <paramref name="emptyMovie"/> — пустой отрезок в начале
    /// (дорожка начинается позже фильма), <paramref name="mediaStart"/> — с какого
    /// места своей шкалы дорожка начинает показ (сдвиг из-за B-кадров).
    /// </summary>
    public static void Edts(BoxWriter w, long emptyMovie, ulong mediaMovieDuration, long mediaStart)
    {
        if (emptyMovie <= 0 && mediaStart <= 0) return;
        w.Begin("edts");
        w.BeginFull("elst", 1, 0);
        w.U32(emptyMovie > 0 ? 2u : 1u);
        if (emptyMovie > 0)
        {
            w.U64((ulong)emptyMovie); w.U64(unchecked((ulong)-1L)); w.U32(0x00010000);
        }
        w.U64(mediaMovieDuration); w.U64((ulong)Math.Max(0, mediaStart)); w.U32(0x00010000);
        w.End();
        w.End();
    }

    public static ulong ToMovie(long ticks, int timescale) =>
        (ulong)Math.Max(0, ticks * MovieTimescale / timescale);
}
