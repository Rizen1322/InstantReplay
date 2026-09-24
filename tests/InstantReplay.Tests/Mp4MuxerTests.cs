using System.Buffers.Binary;
using Aura.Core.Saving.Mp4;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Свой мультиплексор MP4. Наборы параметров — настоящие (из потоков libx264,
/// libx265 и SVT-AV1), кадры — синтетические: мультиплексору важна раскладка
/// байт, а не содержимое срезов. Полная проверка декодером делалась ffmpeg и
/// Media Foundation на реальных клипах; здесь закреплена структура файла.
/// </summary>
public class Mp4MuxerTests
{
    private static readonly byte[] H264Sps = Convert.FromHexString("6764001facb201405ff2e022000003000200000300f01e306490");
    private static readonly byte[] H264Pps = Convert.FromHexString("68ebc3cb22c0");
    private static readonly byte[] HevcVps = Convert.FromHexString("40010c01ffff01600000030090000003000003005a928090");
    private static readonly byte[] HevcSps = Convert.FromHexString("42010101600000030090000003000003005aa0050201696592a4932bc05a020000030002000003007810");
    private static readonly byte[] HevcPps = Convert.FromHexString("4401c172b46240");
    private static readonly byte[] Av1SequenceHeader = Convert.FromHexString("0a0c020000256627fb38d5f30080");

    private static byte[] AnnexB(params byte[][] nals)
    {
        var ms = new MemoryStream();
        foreach (var nal in nals) { ms.Write([0, 0, 0, 1]); ms.Write(nal); }
        return ms.ToArray();
    }

    /// <summary>
    /// Синтетический срез. Последний байт не нулевой — как у настоящего NAL-блока
    /// (rbsp_stop_one_bit): нули в хвосте мультиплексор законно считает частью
    /// следующего стартового кода.
    /// </summary>
    private static byte[] Slice(byte header, int length, byte fill)
    {
        var nal = new byte[length];
        Array.Fill(nal, fill);
        nal[0] = header;
        nal[^1] = 0x80;
        return nal;
    }

    private sealed class Frames(List<(byte[] Data, bool Key)> frames, long frameTicks) : IMp4VideoSource
    {
        public int Count => frames.Count;
        public ReadOnlySpan<byte> Data(int i) => frames[i].Data;
        public long Pts(int i) => i * frameTicks;
        public long Dts(int i) => i * frameTicks;
        public bool IsKeyframe(int i) => frames[i].Key;
        public long LastDuration => frameTicks;
    }

    /// <summary>Найти бокс по пути вида "moov/trak/mdia/minf/stbl/stsz".</summary>
    private static (int Offset, int Size) Find(byte[] file, string path, int start = 0, int end = -1)
    {
        if (end < 0) end = file.Length;
        string[] parts = path.Split('/', 2);
        int pos = start;
        while (pos + 8 <= end)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            if (size < 8) break;
            if (type == parts[0])
                return parts.Length == 1 ? (pos, size) : Find(file, parts[1], pos + 8, pos + size);
            pos += size;
        }
        return (-1, 0);
    }

    private static List<(byte[], bool)> H264Clip(int count)
    {
        var list = new List<(byte[], bool)>();
        for (int i = 0; i < count; i++)
        {
            bool key = i % 60 == 0;
            byte[] frame = key
                ? AnnexB(Convert.FromHexString("0910"), H264Sps, H264Pps, Slice(0x65, 900, (byte)i))
                : AnnexB(Slice(0x41, 300 + i, (byte)i));
            list.Add((frame, key));
        }
        return list;
    }

    [Fact]
    public void Progressive_file_has_moov_before_mdat_and_consistent_tables()
    {
        var frames = H264Clip(120);
        var format = Mp4VideoFormat.FromBitstream(Mp4VideoCodec.H264, 1920, 1080, [], frames[0].Item1)
                     with { FrameRate = 60 };
        var aac = new Mp4AudioFormat(48000, 2, Mp4AudioFormat.AacLcConfig(48000, 2), 192000, "Game audio");
        var audio = Enumerable.Range(0, 94).Select(i => new byte[] { 0x21, (byte)i, 0x10 }).ToList();

        var ms = new MemoryStream();
        long written = Mp4ProgressiveWriter.Write(ms, format, new Frames(frames, 166_666),
                                                  [new Mp4AudioInput(aac, audio, 0)]);
        byte[] file = ms.ToArray();
        Assert.Equal(file.Length, written);

        var moov = Find(file, "moov");
        var mdat = Find(file, "mdat");
        Assert.True(moov.Offset >= 0 && mdat.Offset > moov.Offset, "оглавление обязано идти до данных");

        // Размер сэмпла: SPS/PPS/AUD выброшены, у среза 4 байта длины вместо стартового кода
        var stsz = Find(file, "moov/trak/mdia/minf/stbl/stsz");
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Offset + 16));
        Assert.Equal(120, count);
        int first = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsz.Offset + 20));
        Assert.Equal(4 + 900, first);

        // Смещение первого куска ведёт прямо на длину первого среза
        var stco = Find(file, "moov/trak/mdia/minf/stbl/stco");
        int chunk0 = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stco.Offset + 16));
        Assert.Equal(900, (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(chunk0)));
        Assert.Equal(0x65, file[chunk0 + 4]);

        // Ключевые: 1 и 61
        var stss = Find(file, "moov/trak/mdia/minf/stbl/stss");
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stss.Offset + 12)));
        Assert.Equal(61u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stss.Offset + 20)));

        // Шкала fps×1000: каждый кадр ровно 1000 единиц — «60/1», а не 59.9999
        var stts = Find(file, "moov/trak/mdia/minf/stbl/stts");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stts.Offset + 12)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stts.Offset + 20)));
    }

    [Fact]
    public void Hevc_config_carries_all_parameter_sets()
    {
        byte[] key = AnnexB(HevcVps, HevcSps, HevcPps, Slice(0x26, 500, 1));
        var format = Mp4VideoFormat.FromBitstream(Mp4VideoCodec.Hevc, 640, 360, [], key);
        byte[] hvcC = format.DecoderConfig;
        Assert.Equal(1, hvcC[0]);
        Assert.Equal(3, hvcC[22]);                         // три массива: VPS, SPS, PPS
        Assert.Equal(0x80 | 32, hvcC[23]);
        Assert.Equal(1, hvcC[1] & 0x1F);                   // Main
        Assert.Equal(4 + 500, VideoSamples.Size(Mp4VideoCodec.Hevc, key));
    }

    [Fact]
    public void Av1_config_is_built_from_sequence_header()
    {
        byte[] td = [0x12, 0x00];
        byte[] frame = [0x32, 0x03, 1, 2, 3];              // OBU_FRAME с полем размера
        byte[] tu = [.. td, .. Av1SequenceHeader, .. frame];

        var format = Mp4VideoFormat.FromBitstream(Mp4VideoCodec.Av1, 640, 360, [], tu);
        Assert.Equal(0x81, format.DecoderConfig[0]);
        Assert.Equal(Av1SequenceHeader, format.DecoderConfig[4..]);
        Assert.False(format.TenBit);
        // Разделитель временных единиц в сэмпл не попадает
        Assert.Equal(Av1SequenceHeader.Length + frame.Length, VideoSamples.Size(Mp4VideoCodec.Av1, tu));
    }

    [Fact]
    public void Fragmented_file_is_playable_prefix_and_has_random_access_table()
    {
        var frames = H264Clip(300);
        var format = Mp4VideoFormat.FromBitstream(Mp4VideoCodec.H264, 1280, 720, [], frames[0].Item1)
                     with { FrameRate = 60 };
        var ms = new MemoryStream();
        using (var writer = new FragmentedMp4Writer(ms, format, []))
        {
            for (int i = 0; i < frames.Count; i++)
                writer.WriteVideo(frames[i].Item1, i * 166_666, i * 166_666, frames[i].Item2);
            writer.Finish(166_666);
        }
        byte[] file = ms.ToArray();

        Assert.True(Find(file, "moov/mvex/trex").Offset > 0);
        int moofs = 0;
        for (int pos = 0; pos + 8 <= file.Length;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
            if (System.Text.Encoding.ASCII.GetString(file, pos + 4, 4) == "moof") moofs++;
            pos += size;
        }
        Assert.True(moofs >= 2, $"фрагментов {moofs}");
        // mfra в конце: последние 4 байта — его размер
        int mfra = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(file.Length - 4));
        Assert.Equal("mfra", System.Text.Encoding.ASCII.GetString(file, file.Length - mfra + 4, 4));
    }

    [Fact]
    public void Large_timescale_rounding_never_produces_zero_durations()
    {
        // 144 кадра/с: сетка 69 444 тика против точных 69 444.4 — нарастающее округление
        // обязано держать каждую длительность ровно в слот, без нулей.
        var format = new Mp4VideoFormat(Mp4VideoCodec.H264, 16, 16, []) { FrameRate = 144 };
        for (int i = 0; i < 1_000_000; i += 997)
            Assert.Equal(1000, format.ToUnits(69_444));
        Assert.Equal(2000, format.ToUnits(2 * 69_444));
    }
}
