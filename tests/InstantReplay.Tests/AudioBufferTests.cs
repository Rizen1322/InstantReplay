using Aura.Core.Buffering;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Кольцо звука хранит готовые кадры AAC трёх дорожек. Проверяется то, от чего
/// зависит звук в клипе: запас по времени, копия при снимке, вытеснение, выбор
/// интервала и отрезок после разрыва.
/// </summary>
public class AudioBufferTests
{
    private const long Frame = ReplayAudioBuffer.FrameTicks;
    private const long Second = 10_000_000;

    private static byte[] Payload(byte seed, int length = 400)
    {
        var data = new byte[length];
        Array.Fill(data, seed);
        return data;
    }

    private static ReplayAudioBuffer Make(int seconds, bool game = true, bool mic = true)
    {
        var buffer = new ReplayAudioBuffer { MaxDurationTicks = seconds * Second };
        buffer.Allocate(seconds, game, mic);
        return buffer;
    }

    private static void Feed(ReplayAudioBuffer buffer, int frames, long start = 0)
    {
        for (int i = 0; i < frames; i++)
        {
            long pts = start + i * Frame;
            buffer.Add(AudioTrackKind.Game, Payload((byte)i), pts);
            buffer.Add(AudioTrackKind.Mic, Payload((byte)(i + 1), 200), pts);
            buffer.Add(AudioTrackKind.Mixed, Payload((byte)(i + 2)), pts);
        }
    }

    [Fact]
    public void ЗвукХранитсяДольшеЗаказанного_ЧтобыКлипНеНачиналсяСТишины()
    {
        var buffer = Make(seconds: 5);
        Feed(buffer, frames: 30 * 47); // 30 секунд

        // Снимок видео может начаться на 15 секунд раньше заказанной границы
        long newest = 30 * 47 * Frame;
        var snapshot = buffer.Snapshot(newest - 18 * Second, newest);
        var game = snapshot[AudioTrackKind.Game]!;
        Assert.True(game.Frames.Count * Frame >= 17 * Second,
            $"в кольце {game.Frames.Count * Frame / (double)Second:F1} с вместо ~18");
    }

    [Fact]
    public void СнимокКопируетДанные()
    {
        var buffer = Make(seconds: 5);
        Feed(buffer, frames: 100);
        var snapshot = buffer.Snapshot(0, 100 * Frame);
        byte first = snapshot[AudioTrackKind.Game]!.Frames[0][0];

        // Кольцо продолжает писаться поверх — снимок не меняется
        Feed(buffer, frames: 5000, start: 100 * Frame);
        Assert.Equal(first, snapshot[AudioTrackKind.Game]!.Frames[0][0]);
    }

    [Fact]
    public void СтароеВытесняетсяПоВремени()
    {
        var buffer = Make(seconds: 2);
        Feed(buffer, frames: 47 * 60); // минута

        long newest = (47 * 60 - 1) * Frame;
        var snapshot = buffer.Snapshot(0, newest);
        long covered = snapshot[AudioTrackKind.Game]!.Frames.Count * Frame;
        Assert.InRange(covered, 2 * Second, (2 + 15 + 1) * Second);
    }

    [Fact]
    public void СнимокБерётБлижайшийКНачалуКадр()
    {
        var buffer = Make(seconds: 5);
        Feed(buffer, frames: 200);

        long from = 50 * Frame + Frame / 3;   // ближе к 50-му, чем к 51-му
        var track = buffer.Snapshot(from, 150 * Frame)[AudioTrackKind.Game]!;
        Assert.Equal(50 * Frame, track.FirstPtsTicks);
        Assert.Equal(101, track.Frames.Count);
    }

    [Fact]
    public void ВыключеннаяДорожкаНеЗаводится()
    {
        var buffer = Make(seconds: 5, game: true, mic: false);
        Feed(buffer, frames: 10);
        var snapshot = buffer.Snapshot(0, 10 * Frame);
        Assert.NotNull(snapshot[AudioTrackKind.Game]);
        Assert.Null(snapshot[AudioTrackKind.Mic]);
        Assert.Null(snapshot[AudioTrackKind.Mixed]);   // смесь только при обоих источниках
    }

    [Fact]
    public void ПослеРазрываБерётсяПоследнийСплошнойОтрезок()
    {
        var buffer = Make(seconds: 10);
        Feed(buffer, frames: 100);
        Feed(buffer, frames: 100, start: 100 * Frame + 3 * Second); // разрыв в 3 секунды

        var track = buffer.Snapshot(0, 300 * Frame + 3 * Second)[AudioTrackKind.Game]!;
        Assert.Equal(100, track.Frames.Count);
        Assert.Equal(100 * Frame + 3 * Second, track.FirstPtsTicks);
    }

    [Fact]
    public void ПустойБуферОтдаётПустойСнимок()
    {
        var buffer = Make(seconds: 5);
        Assert.True(buffer.Snapshot(0, Second).IsEmpty);
    }

    [Fact]
    public void ПамятьНеРастётСверхЁмкости()
    {
        var buffer = Make(seconds: 3);
        Feed(buffer, frames: 47 * 600);
        long before = buffer.TotalBytes;
        Feed(buffer, frames: 47 * 600, start: 47 * 600 * Frame);
        Assert.InRange(buffer.TotalBytes, before * 9 / 10, before * 11 / 10);
    }

    [Fact]
    public void СменаДлиныСохраняетКадры()
    {
        var buffer = Make(seconds: 5);
        Feed(buffer, frames: 200);
        buffer.MaxDurationTicks = 20 * Second;
        buffer.Resize(20, game: true, mic: true);

        var track = buffer.Snapshot(0, 200 * Frame)[AudioTrackKind.Game]!;
        Assert.Equal(200, track.Frames.Count);
        Assert.Equal(0, track.FirstPtsTicks);
    }

    [Fact]
    public void ReleaseОтпускаетКольца()
    {
        var buffer = Make(seconds: 5);
        Feed(buffer, frames: 100);
        buffer.Release();
        Assert.Equal(0, buffer.TotalBytes);
        buffer.Add(AudioTrackKind.Game, Payload(1), 0);   // после Release просто игнорируется
        Assert.Equal(0, buffer.TotalBytes);
    }
    [Fact]
    public void РазрывЗаполняетсяТишиной()
    {
        var buffer = Make(seconds: 20);
        Feed(buffer, frames: 100);
        Feed(buffer, frames: 100, start: 100 * Frame + 2 * Second); // разрыв в 2 секунды
        var silence = new byte[] { 0x21, 0x10 };

        var track = buffer.Snapshot(0, 300 * Frame, _ => silence)[AudioTrackKind.Game]!;

        long missing = (long)Math.Round(2.0 * Second / Frame);
        Assert.Equal(0, track.FirstPtsTicks);
        Assert.Equal(200 + missing, track.Frames.Count);
        Assert.Same(silence, track.Frames[100]);
        Assert.Same(silence, track.Frames[(int)(100 + missing - 1)]);
        Assert.Equal(Payload(0), track.Frames[(int)(100 + missing)]);
    }

    [Fact]
    public void НалезающиеКадрыПропускаются()
    {
        var buffer = Make(seconds: 20);
        Feed(buffer, frames: 100);
        Feed(buffer, frames: 50, start: 95 * Frame);  // новый отсчёт кодера налез на 5 кадров

        var track = buffer.Snapshot(0, 300 * Frame, _ => new byte[2])[AudioTrackKind.Game]!;

        Assert.Equal(145, track.Frames.Count);
        Assert.Equal(Payload(99), track.Frames[99]);
        Assert.Equal(Payload(5), track.Frames[100]);
    }
}
