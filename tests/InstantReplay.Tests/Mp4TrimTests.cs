using System.Buffers.Binary;
using Aura.Core.Saving.Mp4;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Своя вырезка фрагмента: кадры копируются байт в байт с ключевого кадра,
/// точное начало и конец задаёт список правок. Источник — файл нашего же
/// мультиплексора: 4 с видео 60 fps с ключевым кадром каждую секунду, звук игры
/// с нуля и микрофон, поднявшийся на 0.5 с позже.
/// </summary>
public class Mp4TrimTests : IDisposable
{
    private static readonly byte[] Sps = Convert.FromHexString("6764001facb201405ff2e022000003000200000300f01e306490");
    private static readonly byte[] Pps = Convert.FromHexString("68ebc3cb22c0");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aura-trim-" + Guid.NewGuid().ToString("N"));

    public Mp4TrimTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Frames(List<(byte[] Data, bool Key)> frames) : IMp4VideoSource
    {
        public int Count => frames.Count;
        public ReadOnlySpan<byte> Data(int i) => frames[i].Data;
        public long Pts(int i) => i * 166_666L;
        public long Dts(int i) => i * 166_666L;
        public bool IsKeyframe(int i) => frames[i].Key;
        public long LastDuration => 166_666;
    }

    private static byte[] Nal(byte header, int length, byte fill)
    {
        var nal = new byte[length];
        Array.Fill(nal, fill);
        nal[0] = header;
        nal[^1] = 0x80;
        return nal;
    }

    private static byte[] AnnexB(params byte[][] nals) =>
        nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();

    private string Source()
    {
        var frames = Enumerable.Range(0, 240).Select(i => i % 60 == 0
            ? (AnnexB(Sps, Pps, Nal(0x65, 900, (byte)i)), true)
            : (AnnexB(Nal(0x41, 300 + i, (byte)i)), false)).ToList();
        var format = Mp4VideoFormat.FromBitstream(Mp4VideoCodec.H264, 1920, 1080, [], frames[0].Item1) with { FrameRate = 60 };
        var game = new Mp4AudioFormat(48000, 2, Mp4AudioFormat.AacLcConfig(48000, 2), 192000, "Game audio");
        var mic = new Mp4AudioFormat(48000, 1, Mp4AudioFormat.AacLcConfig(48000, 1), 128000, "Microphone");
        var gameFrames = Enumerable.Range(0, 188).Select(i => new byte[] { 0x21, (byte)i, 0x10, 1 }).ToList();
        var micFrames = Enumerable.Range(0, 164).Select(i => new byte[] { 0x21, (byte)i, 0x20 }).ToList();

        string path = Path.Combine(_dir, "source.mp4");
        using var file = File.Create(path);
        Mp4ProgressiveWriter.Write(file, format, new Frames(frames),
            [new Mp4AudioInput(game, gameFrames, 0), new Mp4AudioInput(mic, micFrames, 5_000_000)]);
        return path;
    }

    private static (byte[] File, byte[] Moov, List<Mp4Trim.SourceTrack> Tracks) Open(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        using var stream = new MemoryStream(file);
        byte[] moov = Mp4Keyframes.ReadTopLevel(stream, "moov")!;
        return (file, moov, Mp4Trim.ReadTracks(moov));
    }

    private static byte[] Sample(byte[] file, Mp4Trim.SourceTrack t, int i) => file.AsSpan((int)t.Offsets[i], t.Sizes[i]).ToArray();

    [Fact]
    public void Video_starts_at_previous_keyframe_and_edit_list_hides_the_lead_in()
    {
        string source = Source();
        string output = Path.Combine(_dir, "cut.mp4");
        Mp4Trim.Cut(source, output, 1.5, 2.5, audioTrack: -1);

        var (src, _, srcTracks) = Open(source);
        var (dst, moov, tracks) = Open(output);
        var video = tracks.Single(t => !t.Audio);

        // Первый кадр — ключевой на 1.0 с, байт в байт; дальше всё до 2.5 с
        Assert.True(video.Key[0]);
        Assert.Equal(Sample(src, srcTracks[0], 60), Sample(dst, video, 0));
        Assert.Equal(90, video.Sizes.Length);
        Assert.Equal(Sample(src, srcTracks[0], 149), Sample(dst, video, 89));

        // Показ начинается через полсекунды медиа: ровно с 1.5 с исходника
        Assert.Equal(0, video.EmptySeconds);
        Assert.Equal(0.5, (double)video.MediaStart / video.Timescale, 3);

        // Обе звуковые дорожки на месте и показ стартует с того же момента исходника
        var audio = tracks.Where(t => t.Audio).ToList();
        Assert.Equal(2, audio.Count);
        var game = audio[0];
        Assert.True(game.Sizes.Length > 40);
        Assert.Equal(1.5, SourceTime(srcTracks[1], Sample(dst, game, 0), src) + (double)game.MediaStart / game.Timescale, 2);

        // Фильм длится ровно фрагмент; оглавление до данных, как у +faststart
        var mvhd = Mp4Keyframes.Find(moov, 8, moov.Length, "mvhd")!.Value;
        Assert.Equal(1000ul, BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(mvhd.Body + 4 + 20)));
        Assert.True(IndexOf(dst, "moov") < IndexOf(dst, "mdat"));
    }

    [Fact]
    public void Track_that_starts_after_the_cut_gets_an_empty_edit()
    {
        string source = Source();
        string output = Path.Combine(_dir, "mic.mp4");
        Mp4Trim.Cut(source, output, 0.2, 1.2, audioTrack: 1);

        var (_, _, tracks) = Open(output);
        var mic = tracks.Single(t => t.Audio);
        Assert.Equal(1, mic.Stsd.AsSpan().IndexOf("mp4a"u8) > 0 ? 1 : 0);
        Assert.Equal(0x20, Open(output).File[(int)mic.Offsets[0] + 2]);   // это именно микрофон
        // Микрофон поднялся на 0.5 с, фрагмент с 0.2 с: 0.3 с тишины в начале
        Assert.Equal(0.3, mic.EmptySeconds, 2);
        Assert.Equal(0, mic.MediaStart);
        Assert.Equal(2, tracks.Count);
    }

    [Fact]
    public void Cut_past_the_end_takes_everything_to_the_last_frame()
    {
        string source = Source();
        string output = Path.Combine(_dir, "end.mp4");
        // Конец за пределами файла — берём до конца
        Mp4Trim.Cut(source, output, 3.2, 10, audioTrack: 0);
        var (_, _, tracks) = Open(output);
        var video = tracks.Single(t => !t.Audio);
        Assert.Equal(60, video.Sizes.Length);  // с ключевого на 3.0 с до последнего кадра
        Assert.Single(tracks, t => t.Audio);
    }

    [Fact]
    public void Old_fragmented_recording_cuts_the_same_as_its_converted_copy()
    {
        var frames = Enumerable.Range(0, 240).Select(i => i % 60 == 0
            ? (AnnexB(Sps, Pps, Nal(0x65, 900, (byte)i)), true)
            : (AnnexB(Nal(0x41, 300 + i, (byte)i)), false)).ToList();
        var format = Mp4VideoFormat.FromBitstream(Mp4VideoCodec.H264, 1280, 720, [], frames[0].Item1) with { FrameRate = 60 };
        var game = new Mp4AudioFormat(48000, 2, Mp4AudioFormat.AacLcConfig(48000, 2), 192000, "Game audio");
        var ms = new MemoryStream();
        byte[] fragmented;
        using (var writer = new FragmentedMp4Writer(ms, format, [game]))
        {
            for (int i = 0; i < frames.Count; i++)
            {
                writer.WriteVideo(frames[i].Item1, i * 166_666, i * 166_666, frames[i].Item2);
                // Звук поднялся на 0.4 с позже видео
                if (i >= 24) writer.WriteAudio(0, [0x21, (byte)i, 0x10], i * 166_666L);
            }
            fragmented = ms.ToArray();   // как запись до 2.0.7: без обычного оглавления
            writer.Finish(166_666);
        }
        string fragPath = Path.Combine(_dir, "frag.mp4"), plainPath = Path.Combine(_dir, "plain.mp4");
        File.WriteAllBytes(fragPath, fragmented);
        File.WriteAllBytes(plainPath, fragmented);
        Assert.True(Mp4Defragment.ConvertFile(plainPath));

        string a = Path.Combine(_dir, "a.mp4"), b = Path.Combine(_dir, "b.mp4");
        Mp4Trim.Cut(fragPath, a, 1.2, 2.7, -1);
        Mp4Trim.Cut(plainPath, b, 1.2, 2.7, -1);

        var (fa, _, ta) = Open(a);
        var (fb, _, tb) = Open(b);
        Assert.Equal(tb.Count, ta.Count);
        Assert.Equal(2, ta.Count);
        for (int n = 0; n < ta.Count; n++)
        {
            Assert.Equal(tb[n].Sizes, ta[n].Sizes);
            Assert.Equal(tb[n].Dts, ta[n].Dts);
            Assert.Equal(tb[n].Key, ta[n].Key);
            // Обычная копия хранит задержку звука в мс шкалы фильма, фрагменты — точный DTS
            Assert.InRange(Math.Abs(tb[n].MediaStart - ta[n].MediaStart), 0, tb[n].Timescale / 1000 + 1);
            Assert.Equal(tb[n].EmptySeconds, ta[n].EmptySeconds, 3);
            for (int i = 0; i < ta[n].Sizes.Length; i++) Assert.Equal(Sample(fb, tb[n], i), Sample(fa, ta[n], i));
        }
        Assert.DoesNotContain("iso5", System.Text.Encoding.ASCII.GetString(fa, 0, 32));
    }

    [Fact]
    public void Mixed_audio_replaces_the_separate_tracks_with_one()
    {
        string source = Source();
        string output = Path.Combine(_dir, "mixed.mp4");
        IReadOnlyList<Mp4Trim.AacClip>? given = null;
        var config = Mp4AudioFormat.AacLcConfig(48000, 2);
        // Подставной микшер: 30 кадров с номерами, начало — самое раннее из дорожек
        Mp4Trim.Cut(source, output, 1.5, 2.0, -1, mix: tracks =>
        {
            given = tracks;
            return new Mp4Trim.AacClip(48000, 2, config, 192000,
                Enumerable.Range(0, 30).Select(i => new byte[] { 0x21, (byte)i, 0x77 }).ToList(),
                tracks.Min(t => t.StartSeconds));
        });

        // Микшер получил обе дорожки, начиная с ключевого кадра видео (1.0 с) — микрофон с 0.5 с
        Assert.NotNull(given);
        Assert.Equal(2, given!.Count);
        Assert.Equal([2, 1], given.Select(t => t.Channels));
        Assert.True(given[0].StartSeconds <= 1.0 && given[0].StartSeconds > 0.97);
        Assert.Equal(Mp4AudioFormat.AacLcConfig(48000, 1), given[1].Config);
        Assert.Equal(128000, given[1].Bitrate);

        var (file, _, tracks) = Open(output);
        var audio = Assert.Single(tracks, t => t.Audio);
        Assert.Equal("Mixed audio", audio.Name);
        Assert.Equal(48000, audio.Timescale);
        Assert.Equal(config, Mp4Trim.ParseMp4a(audio.Stsd).Config);
        Assert.Equal(new byte[] { 0x21, 0, 0x77 }, Sample(file, audio, 0));
        // Ноль фрагмента — ровно 1.5 с исходника и на сведённой дорожке
        Assert.Equal(1.5, given[0].StartSeconds + (double)audio.MediaStart / audio.Timescale, 3);
    }

    /// <summary>Время сэмпла звука в исходнике по его байтам (второй байт — номер кадра AAC).</summary>
    private static double SourceTime(Mp4Trim.SourceTrack srcTrack, byte[] sample, byte[] src)
    {
        int index = sample[1];
        return srcTrack.DtsTime(index);
    }

    private static int IndexOf(byte[] file, string type)
    {
        int pos = 0;
        while (pos + 8 <= file.Length)
        {
            if (System.Text.Encoding.ASCII.GetString(file, pos + 4, 4) == type) return pos;
            pos += (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos));
        }
        return -1;
    }
}
