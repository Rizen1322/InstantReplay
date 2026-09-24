namespace Aura.Core.Saving.Mp4;

/// <summary>Кодек видеодорожки контейнера.</summary>
public enum Mp4VideoCodec { H264, Hevc, Av1 }

/// <summary>Перевод кадра из вида энкодера в вид MP4 — для любого кодека.</summary>
public static class VideoSamples
{
    public static int Size(Mp4VideoCodec codec, ReadOnlySpan<byte> frame) =>
        codec == Mp4VideoCodec.Av1 ? Av1Obu.SampleSize(frame) : NalUnits.SampleSize(codec, frame);

    public static int Write(Mp4VideoCodec codec, ReadOnlySpan<byte> frame, Span<byte> dest) =>
        codec == Mp4VideoCodec.Av1 ? Av1Obu.WriteSample(frame, dest) : NalUnits.WriteSample(codec, frame, dest);
}

/// <summary>
/// Разбор потока H.264/HEVC в формате Annex B (так их отдают MFT-энкодеры и NVENC):
/// NAL-блоки, разделённые стартовыми кодами 00 00 01 / 00 00 00 01.
///
/// В MP4 тот же поток хранится иначе: перед каждым NAL-блоком четыре байта его
/// длины, а наборы параметров (VPS/SPS/PPS) лежат один раз в описании дорожки
/// (avcC/hvcC). Здесь — всё, что нужно для этого перевода.
/// </summary>
public static class NalUnits
{
    /// <summary>Обойти NAL-блоки: (смещение начала полезных байт, длина).</summary>
    public static int Split(ReadOnlySpan<byte> data, Span<(int Offset, int Length)> result)
    {
        int count = 0;
        int start = FindStart(data, 0, out int codeLength);
        while (start >= 0 && count < result.Length)
        {
            int payload = start + codeLength;
            int next = FindStart(data, payload, out int nextCodeLength);
            int end = next < 0 ? data.Length : next;
            // Хвостовые нули перед следующим стартовым кодом — часть 00 00 00 01,
            // а не данных (trailing_zero_8bits).
            while (end > payload && data[end - 1] == 0) end--;
            if (end > payload) result[count++] = (payload, end - payload);
            start = next;
            codeLength = nextCodeLength;
        }
        return count;
    }

    /// <summary>Сколько NAL-блоков в кадре (верхняя оценка для буфера разбора).</summary>
    public const int MaxNalsPerFrame = 1024;

    private static int FindStart(ReadOnlySpan<byte> data, int from, out int codeLength)
    {
        codeLength = 0;
        while (from + 3 <= data.Length)
        {
            int rel = data[from..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (rel < 0) return -1;
            int at = from + rel;
            codeLength = 3;
            return at;
        }
        return -1;
    }

    /// <summary>Тип NAL-блока по первому байту.</summary>
    public static int Type(Mp4VideoCodec codec, byte header) =>
        codec == Mp4VideoCodec.H264 ? header & 0x1F : (header >> 1) & 0x3F;

    /// <summary>
    /// Выбрасывается при записи сэмпла: наборы параметров уже в описании дорожки,
    /// разделители кадров (AUD) в MP4 не нужны.
    /// </summary>
    public static bool IsDroppedInSample(Mp4VideoCodec codec, int type) => codec == Mp4VideoCodec.H264
        ? type is 7 or 8 or 9          // SPS, PPS, AUD
        : type is 32 or 33 or 34 or 35; // VPS, SPS, PPS, AUD

    /// <summary>Размер кадра в MP4-виде: 4 байта длины на каждый оставленный NAL.</summary>
    public static int SampleSize(Mp4VideoCodec codec, ReadOnlySpan<byte> frame)
    {
        Span<(int, int)> nals = stackalloc (int, int)[MaxNalsPerFrame];
        int count = Split(frame, nals);
        int size = 0;
        for (int i = 0; i < count; i++)
        {
            var (offset, length) = nals[i];
            if (IsDroppedInSample(codec, Type(codec, frame[offset]))) continue;
            size += 4 + length;
        }
        return size;
    }

    /// <summary>Записать кадр в MP4-виде в <paramref name="dest"/>. Возвращает число байт.</summary>
    public static int WriteSample(Mp4VideoCodec codec, ReadOnlySpan<byte> frame, Span<byte> dest)
    {
        Span<(int, int)> nals = stackalloc (int, int)[MaxNalsPerFrame];
        int count = Split(frame, nals);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            var (offset, length) = nals[i];
            if (IsDroppedInSample(codec, Type(codec, frame[offset]))) continue;
            dest[written] = (byte)(length >> 24);
            dest[written + 1] = (byte)(length >> 16);
            dest[written + 2] = (byte)(length >> 8);
            dest[written + 3] = (byte)length;
            frame.Slice(offset, length).CopyTo(dest[(written + 4)..]);
            written += 4 + length;
        }
        return written;
    }

    /// <summary>Наборы параметров из заголовка последовательности или ключевого кадра.</summary>
    public sealed record ParameterSets(List<byte[]> Vps, List<byte[]> Sps, List<byte[]> Pps)
    {
        public bool Complete(Mp4VideoCodec codec) =>
            Sps.Count > 0 && Pps.Count > 0 && (codec == Mp4VideoCodec.H264 || Vps.Count > 0);
    }

    public static ParameterSets ExtractParameterSets(Mp4VideoCodec codec, ReadOnlySpan<byte> data)
    {
        var result = new ParameterSets([], [], []);
        Span<(int, int)> nals = stackalloc (int, int)[MaxNalsPerFrame];
        int count = Split(data, nals);
        for (int i = 0; i < count; i++)
        {
            var (offset, length) = nals[i];
            var nal = data.Slice(offset, length);
            int type = Type(codec, nal[0]);
            List<byte[]>? target = codec == Mp4VideoCodec.H264
                ? type switch { 7 => result.Sps, 8 => result.Pps, _ => null }
                : type switch { 32 => result.Vps, 33 => result.Sps, 34 => result.Pps, _ => null };
            if (target is null) continue;
            byte[] bytes = nal.ToArray();
            if (!target.Any(existing => existing.AsSpan().SequenceEqual(bytes))) target.Add(bytes);
        }
        return result;
    }

    // ---------------- Описания дорожки ----------------

    /// <summary>AVCDecoderConfigurationRecord (ISO/IEC 14496-15, 5.3.3.1).</summary>
    public static byte[] BuildAvcC(ParameterSets sets)
    {
        byte[] sps = sets.Sps[0];
        var w = new BoxWriter();
        w.U8(1);
        w.U8(sps[1]);            // profile_idc
        w.U8(sps[2]);            // constraint flags
        w.U8(sps[3]);            // level_idc
        w.U8(0xFF);              // 6 бит резерва + lengthSizeMinusOne = 3
        w.U8((byte)(0xE0 | sets.Sps.Count));
        foreach (var s in sets.Sps) { w.U16((ushort)s.Length); w.Bytes(s); }
        w.U8((byte)sets.Pps.Count);
        foreach (var p in sets.Pps) { w.U16((ushort)p.Length); w.Bytes(p); }

        // Для High и выше стандарт требует хвост с форматом цветности и глубиной.
        if (sps[1] is 100 or 110 or 122 or 144)
        {
            var info = H264SpsInfo.Parse(sps);
            w.U8((byte)(0xFC | info.ChromaFormatIdc));
            w.U8((byte)(0xF8 | info.BitDepthLumaMinus8));
            w.U8((byte)(0xF8 | info.BitDepthChromaMinus8));
            w.U8(0);             // numOfSequenceParameterSetExt
        }
        return w.ToArray();
    }

    /// <summary>HEVCDecoderConfigurationRecord (ISO/IEC 14496-15, 8.3.3.1).</summary>
    public static byte[] BuildHvcC(ParameterSets sets, int fpsHint)
    {
        var info = HevcSpsInfo.Parse(sets.Sps[0]);
        var w = new BoxWriter();
        w.U8(1);
        w.U8((byte)((info.ProfileSpace << 6) | (info.TierFlag << 5) | info.ProfileIdc));
        w.U32(info.CompatibilityFlags);
        w.Bytes(info.ConstraintFlags);            // 6 байт
        w.U8(info.LevelIdc);
        w.U16(0xF000);                            // min_spatial_segmentation_idc = 0
        w.U8(0xFC);                               // parallelismType = 0
        w.U8((byte)(0xFC | info.ChromaFormatIdc));
        w.U8((byte)(0xF8 | info.BitDepthLumaMinus8));
        w.U8((byte)(0xF8 | info.BitDepthChromaMinus8));
        w.U16(0);                                 // avgFrameRate: не задано
        // constantFrameRate(2)=0, numTemporalLayers(3), temporalIdNested(1), lengthSizeMinusOne(2)=3
        w.U8((byte)(((info.MaxSubLayersMinus1 + 1) << 3) | (info.TemporalIdNesting << 2) | 3));

        var arrays = new List<(int Type, List<byte[]> Units)>
        {
            (32, sets.Vps), (33, sets.Sps), (34, sets.Pps)
        };
        w.U8((byte)arrays.Count);
        foreach (var (type, units) in arrays)
        {
            w.U8((byte)(0x80 | type));            // array_completeness = 1
            w.U16((ushort)units.Count);
            foreach (var u in units) { w.U16((ushort)u.Length); w.Bytes(u); }
        }
        _ = fpsHint;
        return w.ToArray();
    }

    /// <summary>Убрать байты защиты от эмуляции (00 00 03 → 00 00) — перед разбором битов.</summary>
    public static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        var result = new List<byte>(nal.Length);
        int zeros = 0;
        foreach (byte b in nal)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            zeros = b == 0 ? zeros + 1 : 0;
            result.Add(b);
        }
        return [.. result];
    }
}

/// <summary>Чтение битов и exp-Golomb из RBSP.</summary>
internal ref struct BitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bit;

    public uint Bits(int count)
    {
        uint value = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIndex = _bit >> 3;
            int bit = byteIndex < _data.Length ? (_data[byteIndex] >> (7 - (_bit & 7))) & 1 : 0;
            value = (value << 1) | (uint)bit;
            _bit++;
        }
        return value;
    }

    public bool Flag() => Bits(1) != 0;

    public void Skip(int count) => _bit += count;

    public uint Ue()
    {
        int zeros = 0;
        while (Bits(1) == 0 && zeros < 32) zeros++;
        return zeros == 0 ? 0 : (1u << zeros) - 1 + Bits(zeros);
    }

    public int Se()
    {
        uint v = Ue();
        return (v & 1) != 0 ? (int)((v + 1) / 2) : -(int)(v / 2);
    }
}

/// <summary>Нужное из SPS H.264: формат цветности и глубина (для хвоста avcC).</summary>
public readonly record struct H264SpsInfo(int ChromaFormatIdc, int BitDepthLumaMinus8, int BitDepthChromaMinus8)
{
    public static H264SpsInfo Parse(byte[] sps)
    {
        var rbsp = NalUnits.Unescape(sps);
        var r = new BitReader(rbsp);
        r.Skip(8);                  // заголовок NAL
        int profile = (int)r.Bits(8);
        r.Skip(16);                 // constraint flags + level
        r.Ue();                     // seq_parameter_set_id
        int chroma = 1, luma = 0, chromaDepth = 0;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chroma = (int)r.Ue();
            if (chroma == 3) r.Skip(1);
            luma = (int)r.Ue();
            chromaDepth = (int)r.Ue();
        }
        return new H264SpsInfo(chroma, luma, chromaDepth);
    }
}

/// <summary>Нужное из SPS HEVC для hvcC.</summary>
public sealed record HevcSpsInfo(
    int ProfileSpace, int TierFlag, int ProfileIdc, uint CompatibilityFlags, byte[] ConstraintFlags,
    byte LevelIdc, int ChromaFormatIdc, int BitDepthLumaMinus8, int BitDepthChromaMinus8,
    int MaxSubLayersMinus1, int TemporalIdNesting, int Width, int Height)
{
    public static HevcSpsInfo Parse(byte[] sps)
    {
        var rbsp = NalUnits.Unescape(sps);
        var r = new BitReader(rbsp);
        r.Skip(16);                                   // заголовок NAL (2 байта)
        r.Skip(4);                                    // sps_video_parameter_set_id
        int maxSubLayersMinus1 = (int)r.Bits(3);
        int nesting = (int)r.Bits(1);

        // profile_tier_level(1, maxSubLayersMinus1)
        int profileSpace = (int)r.Bits(2);
        int tier = (int)r.Bits(1);
        int profileIdc = (int)r.Bits(5);
        uint compat = r.Bits(32);
        var constraint = new byte[6];
        for (int i = 0; i < 6; i++) constraint[i] = (byte)r.Bits(8);
        byte level = (byte)r.Bits(8);

        var subProfilePresent = new bool[8];
        var subLevelPresent = new bool[8];
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            subProfilePresent[i] = r.Flag();
            subLevelPresent[i] = r.Flag();
        }
        if (maxSubLayersMinus1 > 0)
            for (int i = maxSubLayersMinus1; i < 8; i++) r.Skip(2);
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (subProfilePresent[i]) r.Skip(88);
            if (subLevelPresent[i]) r.Skip(8);
        }

        r.Ue();                                       // sps_seq_parameter_set_id
        int chroma = (int)r.Ue();
        if (chroma == 3) r.Skip(1);                   // separate_colour_plane_flag
        int width = (int)r.Ue();
        int height = (int)r.Ue();
        if (r.Flag())                                 // conformance_window_flag
        {
            r.Ue(); r.Ue(); r.Ue(); r.Ue();
        }
        int luma = (int)r.Ue();
        int chromaDepth = (int)r.Ue();

        return new HevcSpsInfo(profileSpace, tier, profileIdc, compat, constraint, level,
                               chroma, luma, chromaDepth, maxSubLayersMinus1, nesting, width, height);
    }
}
