using System.Buffers;
using InstantReplay.Core.Buffering;
using InstantReplay.Core.Library;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Кольцевой буфер — самое неочевидное место конвейера: он обязан вытеснять по
/// ВРЕМЕНИ и резать строго по ключевому кадру, иначе UI показывает одну длину
/// буфера, а сохраняется другая, и клип начинается с «каши».
/// </summary>
public class ReplayVideoBufferTests
{
    private const long Second = 10_000_000; // 100-нс тики

    private static EncodedFrame Frame(long ptsTicks, bool keyframe, int length = 16)
    {
        // Массивы берём из пула: буфер возвращает их туда же при вытеснении
        var data = ArrayPool<byte>.Shared.Rent(length);
        return new EncodedFrame(data, length, ptsTicks, Second / 60, keyframe);
    }

    /// <summary>Кадры на 60 fps: каждые 2 секунды — ключевой (GOP как у энкодера).</summary>
    private static void Fill(ReplayVideoBuffer buffer, int seconds)
    {
        for (int i = 0; i < seconds * 60; i++)
        {
            long pts = i * (Second / 60);
            buffer.Add(Frame(pts, keyframe: i % 120 == 0));
        }
    }

    [Fact]
    public void ПервымКадромМожетБытьТолькоКлючевой()
    {
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 10 * Second };

        buffer.Add(Frame(0, keyframe: false));
        Assert.Equal(0, buffer.TotalBytes);

        buffer.Add(Frame(Second, keyframe: true));
        Assert.Equal(16, buffer.TotalBytes);
    }

    [Fact]
    public void ДержитЗаданнуюДлительность()
    {
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 5 * Second };
        Fill(buffer, seconds: 30);

        double buffered = buffer.BufferedDurationTicks / (double)Second;

        // Режем по ключевым кадрам, поэтому допускается запас в один GOP (2 сек)
        Assert.InRange(buffered, 5.0, 7.1);
    }

    [Fact]
    public void СнимокНачинаетсяСКлючевогоКадра()
    {
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 20 * Second };
        Fill(buffer, seconds: 20);

        var snapshot = buffer.SnapshotAndClear(5 * Second);

        Assert.NotEmpty(snapshot);
        Assert.True(snapshot[0].IsKeyframe, "клип обязан начинаться с ключевого кадра");
        double seconds = (snapshot[^1].PtsTicks - snapshot[0].PtsTicks) / (double)Second;
        Assert.InRange(seconds, 5.0, 7.1);

        ReplayVideoBuffer.ReturnToPool(snapshot);
    }

    [Fact]
    public void СнимокОчищаетБуфер()
    {
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 20 * Second };
        Fill(buffer, seconds: 10);

        var snapshot = buffer.SnapshotAndClear(10 * Second);

        Assert.Equal(0, buffer.TotalBytes);
        Assert.Equal(0, buffer.BufferedDurationTicks);
        ReplayVideoBuffer.ReturnToPool(snapshot);
    }

    [Fact]
    public void БезКлючевыхКадровБуферВсёРавноОграничен()
    {
        // Сломанный GOP: энкодер не выдаёт keyframe. Без страховки буфер съел бы всю RAM.
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = 5 * Second };
        buffer.Add(Frame(0, keyframe: true));
        for (int i = 1; i < 60 * 60; i++) // минута кадров подряд
            buffer.Add(Frame(i * (Second / 60), keyframe: false));

        Assert.True(buffer.BufferedDurationTicks <= 16 * Second,
            $"буфер вырос до {buffer.BufferedDurationTicks / (double)Second:F1} с");
    }
}

public class ClipGroupingTests
{
    private static readonly DateTime Today = new(2026, 7, 30);

    [Fact]
    public void СегодняИВчера()
    {
        Assert.Equal("Сегодня", ClipGrouping.DateTitle(Today.AddHours(3), Today));
        Assert.Equal("Вчера", ClipGrouping.DateTitle(Today.AddDays(-1).AddHours(22), Today));
    }

    [Fact]
    public void ДатаВЭтомГодуБезГода()
    {
        string title = ClipGrouping.DateTitle(new DateTime(2026, 7, 14), Today);
        Assert.DoesNotContain("2026", title);
        Assert.Contains("14", title);
    }

    [Fact]
    public void ПрошлогодняяДатаСГодом() =>
        Assert.Contains("2025", ClipGrouping.DateTitle(new DateTime(2025, 12, 31), Today));
}
