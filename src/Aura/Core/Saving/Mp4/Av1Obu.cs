namespace Aura.Core.Saving.Mp4;

/// <summary>
/// AV1 в MP4 (спецификация «AV1 Codec ISO Media File Format Binding»).
///
/// Энкодеры отдают AV1 в «низкоуровневом» формате: последовательность OBU, у каждой
/// заголовок и длина (leb128). В MP4 сэмпл — это те же OBU подряд, только без
/// разделителей временных единиц (OBU_TEMPORAL_DELIMITER), а описание дорожки
/// (av1C) несёт профиль, уровень, глубину цвета и сам заголовок последовательности.
/// </summary>
public static class Av1Obu
{
    public const int SequenceHeader = 1;
    public const int TemporalDelimiter = 2;
    public const int Padding = 15;

    /// <summary>Один OBU: тип, начало (с заголовком), полная длина.</summary>
    public readonly record struct Unit(int Type, int Offset, int Length);

    /// <summary>Разобрать поток OBU. Возвращает число найденных.</summary>
    public static int Split(ReadOnlySpan<byte> data, Span<Unit> result)
    {
        int count = 0, pos = 0;
        while (pos < data.Length && count < result.Length)
        {
            byte header = data[pos];
            int type = (header >> 3) & 0x0F;
            bool extension = (header & 0x04) != 0;
            bool hasSize = (header & 0x02) != 0;
            int headerLength = 1 + (extension ? 1 : 0);
            int payloadStart = pos + headerLength;
            long payloadSize;
            if (hasSize)
            {
                payloadSize = ReadLeb128(data, payloadStart, out int lebLength);
                payloadStart += lebLength;
            }
            else
            {
                // Без поля размера OBU тянется до конца блока
                payloadSize = data.Length - payloadStart;
            }
            long end = payloadStart + payloadSize;
            if (payloadSize < 0 || end > data.Length) break;
            result[count++] = new Unit(type, pos, (int)(end - pos));
            pos = (int)end;
        }
        return count;
    }

    public static long ReadLeb128(ReadOnlySpan<byte> data, int at, out int length)
    {
        long value = 0;
        length = 0;
        for (int i = 0; i < 8 && at + i < data.Length; i++)
        {
            byte b = data[at + i];
            value |= (long)(b & 0x7F) << (i * 7);
            length++;
            if ((b & 0x80) == 0) break;
        }
        return value;
    }

    private static bool Dropped(int type) => type is TemporalDelimiter or Padding;

    public static int SampleSize(ReadOnlySpan<byte> frame)
    {
        Span<Unit> units = stackalloc Unit[NalUnits.MaxNalsPerFrame];
        int count = Split(frame, units);
        int size = 0;
        for (int i = 0; i < count; i++) if (!Dropped(units[i].Type)) size += units[i].Length;
        return size;
    }

    public static int WriteSample(ReadOnlySpan<byte> frame, Span<byte> dest)
    {
        Span<Unit> units = stackalloc Unit[NalUnits.MaxNalsPerFrame];
        int count = Split(frame, units);
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            var u = units[i];
            if (Dropped(u.Type)) continue;
            frame.Slice(u.Offset, u.Length).CopyTo(dest[written..]);
            written += u.Length;
        }
        return written;
    }

    /// <summary>
    /// Найти заголовок последовательности: в заголовке от энкодера (это может быть
    /// сам OBU или уже готовая запись av1C — у неё первый байт 0x81) либо в
    /// ключевом кадре.
    /// </summary>
    public static byte[]? FindSequenceHeader(ReadOnlySpan<byte> sequenceHeader, ReadOnlySpan<byte> keyframe)
    {
        if (sequenceHeader.Length > 4 && sequenceHeader[0] == 0x81)
        {
            var fromConfig = FindIn(sequenceHeader[4..]);
            if (fromConfig is not null) return fromConfig;
        }
        return FindIn(sequenceHeader) ?? FindIn(keyframe);
    }

    private static byte[]? FindIn(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        Span<Unit> units = stackalloc Unit[NalUnits.MaxNalsPerFrame];
        int count = Split(data, units);
        for (int i = 0; i < count; i++)
            if (units[i].Type == SequenceHeader)
                return WithSizeField(data.Slice(units[i].Offset, units[i].Length));
        return null;
    }

    /// <summary>В av1C OBU обязан нести поле размера — дописываем, если его нет.</summary>
    private static byte[] WithSizeField(ReadOnlySpan<byte> obu)
    {
        if ((obu[0] & 0x02) != 0) return obu.ToArray();
        int headerLength = (obu[0] & 0x04) != 0 ? 2 : 1;
        int payload = obu.Length - headerLength;
        var leb = new List<byte>();
        int v = payload;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            leb.Add(b);
        } while (v != 0);
        var result = new byte[headerLength + leb.Count + payload];
        obu[..headerLength].CopyTo(result);
        result[0] |= 0x02;
        leb.CopyTo(result, headerLength);
        obu[headerLength..].CopyTo(result.AsSpan(headerLength + leb.Count));
        return result;
    }

    /// <summary>AV1CodecConfigurationRecord.</summary>
    public static byte[] BuildAv1C(byte[] sequenceHeaderObu, out bool tenBit)
    {
        var info = Av1SequenceInfo.Parse(sequenceHeaderObu);
        tenBit = info.HighBitdepth;
        var w = new BoxWriter();
        w.U8(0x81);
        w.U8((byte)((info.Profile << 5) | info.Level));
        w.U8((byte)((info.Tier << 7) | ((info.HighBitdepth ? 1 : 0) << 6) | ((info.TwelveBit ? 1 : 0) << 5) |
                    ((info.Monochrome ? 1 : 0) << 4) | (info.SubsamplingX << 3) | (info.SubsamplingY << 2) |
                    info.ChromaSamplePosition));
        w.U8(0);
        w.Bytes(sequenceHeaderObu);
        return w.ToArray();
    }
}

/// <summary>Нужное для av1C из заголовка последовательности AV1 (раздел 5.5 спецификации).</summary>
public readonly record struct Av1SequenceInfo(
    int Profile, int Level, int Tier, bool HighBitdepth, bool TwelveBit, bool Monochrome,
    int SubsamplingX, int SubsamplingY, int ChromaSamplePosition)
{
    public static Av1SequenceInfo Parse(byte[] obu)
    {
        int headerLength = (obu[0] & 0x04) != 0 ? 2 : 1;
        int payloadStart = headerLength;
        if ((obu[0] & 0x02) != 0)
        {
            Av1Obu.ReadLeb128(obu, headerLength, out int leb);
            payloadStart += leb;
        }
        var r = new BitReader(obu.AsSpan(payloadStart));

        int profile = (int)r.Bits(3);
        r.Skip(1);                                  // still_picture
        bool reduced = r.Flag();
        int level = 0, tier = 0;
        if (reduced)
        {
            level = (int)r.Bits(5);
        }
        else
        {
            bool timingInfo = r.Flag();
            bool decoderModelInfo = false;
            int bufferDelayLength = 0;
            if (timingInfo)
            {
                r.Skip(64);                         // num_units_in_display_tick, time_scale
                if (r.Flag()) Uvlc(ref r);          // equal_picture_interval
                decoderModelInfo = r.Flag();
                if (decoderModelInfo)
                {
                    bufferDelayLength = (int)r.Bits(5) + 1;
                    r.Skip(32);                     // num_units_in_decoding_tick
                    r.Skip(10);                     // buffer_removal_time_length, frame_presentation_time_length
                }
            }
            bool initialDisplayDelay = r.Flag();
            int operatingPoints = (int)r.Bits(5) + 1;
            for (int i = 0; i < operatingPoints; i++)
            {
                r.Skip(12);                         // operating_point_idc
                int seqLevel = (int)r.Bits(5);
                int seqTier = seqLevel > 7 ? (int)r.Bits(1) : 0;
                if (decoderModelInfo && r.Flag())
                    r.Skip(bufferDelayLength * 2 + 1);
                if (initialDisplayDelay && r.Flag()) r.Skip(4);
                if (i == 0) { level = seqLevel; tier = seqTier; }
            }
        }

        int widthBits = (int)r.Bits(4) + 1;
        int heightBits = (int)r.Bits(4) + 1;
        r.Skip(widthBits + heightBits);
        if (!reduced && r.Flag()) r.Skip(4 + 3);    // frame_id_numbers_present
        r.Skip(3);                                  // 128x128, filter_intra, intra_edge
        if (!reduced)
        {
            r.Skip(4);                              // interintra, masked, warped, dual_filter
            bool orderHint = r.Flag();
            if (orderHint) r.Skip(2);               // jnt_comp, ref_frame_mvs
            int forceScreenContent = r.Flag() ? 2 : (int)r.Bits(1);
            if (forceScreenContent > 0)
            {
                if (!r.Flag()) r.Skip(1);           // seq_force_integer_mv
            }
            if (orderHint) r.Skip(3);
        }
        r.Skip(3);                                  // superres, cdef, restoration

        // color_config
        bool high = r.Flag();
        bool twelve = profile == 2 && high && r.Flag();
        int bitDepth = twelve ? 12 : high ? 10 : 8;
        bool mono = profile != 1 && r.Flag();
        int primaries = 2, transfer = 2, matrix = 2;
        if (r.Flag())
        {
            primaries = (int)r.Bits(8);
            transfer = (int)r.Bits(8);
            matrix = (int)r.Bits(8);
        }
        int sx, sy, csp = 0;
        if (mono)
        {
            r.Skip(1);
            sx = sy = 1;
        }
        else if (primaries == 1 && transfer == 13 && matrix == 0)
        {
            sx = sy = 0;
        }
        else
        {
            r.Skip(1);                              // color_range
            if (profile == 0) { sx = sy = 1; }
            else if (profile == 1) { sx = sy = 0; }
            else if (bitDepth == 12)
            {
                sx = (int)r.Bits(1);
                sy = sx == 1 ? (int)r.Bits(1) : 0;
            }
            else { sx = 1; sy = 0; }
            if (sx == 1 && sy == 1) csp = (int)r.Bits(2);
        }

        return new Av1SequenceInfo(profile, level, tier, high, twelve, mono, sx, sy, csp);
    }

    private static void Uvlc(ref BitReader r)
    {
        int zeros = 0;
        while (!r.Flag() && zeros < 32) zeros++;
        if (zeros < 32) r.Skip(zeros);
    }
}
