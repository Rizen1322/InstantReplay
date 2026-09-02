using Aura.Core.Buffering;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Кольцевой буфер Aura: кадры лежат не в отдельных массивах, а вплотную в арене
/// из блоков по 64 МБ. Отсюда три места, где легко испортить данные и которые
/// проверяются здесь: переход кадра через границу блока, обход арены по кругу
/// и снимок, после которого буфер продолжает писать уже в новые блоки.
/// </summary>
public class AuraArenaBufferTests
{
    private const long Second = 10_000_000; // 100-нс тики
    private const long Megabit = 1_000_000;

    /// <summary>Кадр заданной длины, заполненный узнаваемым узором от seed.</summary>
    private static EncodedFrame Frame(long ptsTicks, bool keyframe, int length, byte seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(seed + i);
        return new EncodedFrame(data, 0, length, ptsTicks, Second / 60, keyframe);
    }

    private static void AssertPattern(EncodedFrame frame, byte seed)
    {
        for (int i = 0; i < frame.Length; i++)
            Assert.Equal((byte)(seed + i), frame.Data[frame.Offset + i]);
    }

    private static ReplayVideoBuffer Make(int seconds, long bitrateBps)
    {
        var buffer = new ReplayVideoBuffer { MaxDurationTicks = seconds * Second };
        buffer.Allocate(bitrateBps, seconds);
        return buffer;
    }

    [Fact]
    public void Snapshot_returns_exactly_what_was_written()
    {
        var buffer = Make(seconds: 5, bitrateBps: 40 * Megabit);
        for (int i = 0; i < 60; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i == 0, length: 1000 + i, seed: (byte)i));

        var snapshot = buffer.TakeSnapshot(5 * Second, out _);

        Assert.Equal(60, snapshot.Count);
        for (int i = 0; i < snapshot.Count; i++)
        {
            Assert.Equal(1000 + i, snapshot[i].Length);
            AssertPattern(snapshot[i], (byte)i);
        }
    }

    [Fact]
    public void Frames_survive_block_boundaries_and_wraparound()
    {
        // Арена на 176 МБ (50 Мбит/с × 16 с ≈ 105 МБ плюс 64 МБ запаса под
        // сохранение, округлённые до блоков по 16 МБ), а пишем 300 МБ: это и
        // переходы через границы блоков, и почти два оборота по кругу. Вытеснение
        // по времени намеренно отключено (час), чтобы работал только механизм
        // освобождения места.
        var buffer = new ReplayVideoBuffer();
        buffer.Allocate(50 * Megabit, seconds: 1);
        buffer.MaxDurationTicks = 3600 * Second;
        Assert.Equal(176L * 1024 * 1024, buffer.CapacityBytes);

        const int frameSize = 200 * 1024;
        for (int i = 0; i < 1500; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, frameSize, (byte)i));

        var snapshot = buffer.TakeSnapshot(long.MaxValue, out _);

        Assert.NotEmpty(snapshot);
        // Кадры в снимке идут подряд и каждый обязан совпасть со своим узором:
        // если бы кадр лёг на стык блоков или затёр соседа, узор бы разъехался.
        int first = 1500 - snapshot.Count;
        for (int i = 0; i < snapshot.Count; i++)
        {
            Assert.Equal(frameSize, snapshot[i].Length);
            AssertPattern(snapshot[i], (byte)(first + i));
        }
    }

    [Fact]
    public void Used_memory_never_exceeds_arena()
    {
        var buffer = Make(seconds: 10, bitrateBps: 40 * Megabit);
        long capacity = buffer.CapacityBytes;

        for (int i = 0; i < 60 * 600; i++) // десять минут кадров в буфер на десять секунд
        {
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, length: 80_000, seed: (byte)i));
            Assert.True(buffer.TotalBytes <= capacity,
                $"занято {buffer.TotalBytes} при ёмкости {capacity}");
        }
    }

    [Fact]
    public void Buffer_keeps_requested_duration_and_starts_with_keyframe()
    {
        var buffer = Make(seconds: 10, bitrateBps: 40 * Megabit);
        for (int i = 0; i < 60 * 60; i++) // минута кадров
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, length: 80_000, seed: (byte)i));

        Assert.True(buffer.BufferedDurationTicks >= 10 * Second,
            $"в буфере {buffer.BufferedDurationTicks / (double)Second:F1} с вместо 10");

        var snapshot = buffer.TakeSnapshot(10 * Second, out _);
        Assert.True(snapshot[0].IsKeyframe, "клип обязан начинаться с ключевого кадра");
    }

    [Fact]
    public void Second_replay_right_after_first_still_has_content()
    {
        // Живой сценарий: «момент был чуть раньше — сохраню ещё раз». Раньше снимок
        // очищал список кадров целиком, и второй повтор подряд содержал только то,
        // что успело накопиться за секунды между нажатиями.
        var buffer = Make(seconds: 10, bitrateBps: 20 * Megabit);
        for (int i = 0; i < 60 * 20; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, length: 40_000, seed: (byte)i));

        var first = buffer.TakeSnapshot(10 * Second, out long token);
        Assert.NotEmpty(first);
        buffer.ReleaseSnapshot(token);

        var second = buffer.TakeSnapshot(10 * Second, out long token2);
        buffer.ReleaseSnapshot(token2);

        Assert.True(second.Count > first.Count / 2,
            $"второй повтор почти пуст: {second.Count} кадров против {first.Count} у первого");
    }

    [Fact]
    public void Writing_after_snapshot_does_not_touch_snapshot_data()
    {
        // Главный инвариант: пока снимок не отпущен, его кадры остаются в арене
        // нетронутыми, а запись идёт в свободную часть. Если бы буфер их затёр,
        // клип портился бы прямо во время записи файла.
        var buffer = Make(seconds: 2, bitrateBps: 10 * Megabit);
        for (int i = 0; i < 120; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, length: 50_000, seed: (byte)i));

        var snapshot = buffer.TakeSnapshot(2 * Second, out long token);
        Assert.NotEmpty(snapshot);

        // Заливаем буфер заново с большим избытком: арена обязана защитить снимок
        // даже когда свободное место кончилось (лишние кадры просто не примутся).
        for (int i = 0; i < 5000; i++)
            buffer.Add(Frame((200 + i) * (Second / 60), keyframe: i % 120 == 0, length: 50_000, seed: 0xFF));

        int first = 120 - snapshot.Count;
        for (int i = 0; i < snapshot.Count; i++)
            AssertPattern(snapshot[i], (byte)(first + i));

        // Снимок отпущен — место возвращается буферу, вытеснение снова работает.
        long usedWithSnapshot = buffer.TotalBytes;
        buffer.ReleaseSnapshot(token);
        Assert.True(buffer.TotalBytes < usedWithSnapshot);

        buffer.ReleaseSnapshot(token);            // повторный возврат ничего не делает
        buffer.ReleaseSnapshot(token + 999);      // чужой токен тоже
        Assert.True(buffer.TotalBytes >= 0);
    }

    [Fact]
    public void Snapshot_does_not_allocate_second_arena()
    {
        // Ради этого всё и затевалось: сохранение не должно удваивать память.
        // Ёмкость арены обязана остаться прежней, а занято — не больше ёмкости.
        var buffer = Make(seconds: 3, bitrateBps: 20 * Megabit);
        long capacity = buffer.CapacityBytes;

        for (int i = 0; i < 60 * 30; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, length: 40_000, seed: (byte)i));

        var snapshot = buffer.TakeSnapshot(3 * Second, out long token);
        Assert.NotEmpty(snapshot);
        Assert.Equal(capacity, buffer.CapacityBytes);

        for (int i = 0; i < 600; i++)
        {
            buffer.Add(Frame((60 * 30 + i) * (Second / 60), keyframe: i % 120 == 0, length: 40_000, seed: 1));
            Assert.True(buffer.TotalBytes <= capacity);
        }

        Assert.Equal(capacity, buffer.CapacityBytes);
        buffer.ReleaseSnapshot(token);
        Assert.True(buffer.TotalBytes <= capacity);
    }

    [Fact]
    public void Clear_releases_arena_and_buffer_still_works()
    {
        var buffer = Make(seconds: 5, bitrateBps: 40 * Megabit);
        for (int i = 0; i < 300; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 120 == 0, length: 60_000, seed: (byte)i));
        Assert.True(buffer.TotalBytes > 0);

        buffer.Clear();
        Assert.Equal(0, buffer.TotalBytes);
        Assert.Equal(0, buffer.BufferedDurationTicks);

        // После очистки блоки выделяются заново — буфер обязан продолжить работать
        buffer.Add(Frame(0, keyframe: true, length: 60_000, seed: 7));
        Assert.Equal(60_000, buffer.TotalBytes);
        var snapshot = buffer.TakeSnapshot(5 * Second, out _);
        AssertPattern(Assert.Single(snapshot), 7);
    }

    [Fact]
    public void First_frame_must_be_keyframe()
    {
        var buffer = Make(seconds: 5, bitrateBps: 40 * Megabit);
        buffer.Add(Frame(0, keyframe: false, length: 1000, seed: 1));
        Assert.Equal(0, buffer.TotalBytes);

        buffer.Add(Frame(Second / 60, keyframe: true, length: 1000, seed: 2));
        Assert.Equal(1000, buffer.TotalBytes);
    }

    /// <summary>
    /// Сломанный GOP: энкодер выдал keyframe в начале и надолго замолчал, вытеснение
    /// по времени срезало голову посреди группы. Снимок обязан начаться с ключевого
    /// кадра, даже если ради этого клип выйдет короче заказанного.
    ///
    /// Раньше он начинался с первого попавшегося P-кадра: файл получался длиннее
    /// настройки, а плеер первые секунды показывал пустоту, пока не перемотаешь.
    /// </summary>
    [Fact]
    public void Snapshot_never_starts_on_non_keyframe()
    {
        var buffer = Make(seconds: 2, bitrateBps: 40 * Megabit);

        // Ключевые только в самом начале и на 19-й секунде — между ними 19 секунд
        // сплошных P-кадров. Ровно так ведёт себя просевший энкодер: GOP задан в
        // КАДРАХ, и на низком fps группа растягивается на десятки секунд.
        //
        // При лимите 2 с буфер выйдет за «лимит + страховка» и включит вытеснение
        // по времени — оно и срежет голову посреди группы, оставив первым P-кадр.
        const int keyframeAt = 60 * 19;
        for (int i = 0; i <= 60 * 20; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i == 0 || i == keyframeAt,
                             length: 1000, seed: (byte)i));

        var snapshot = buffer.TakeSnapshot(4 * Second, out _);

        Assert.NotEmpty(snapshot);
        Assert.True(snapshot[0].IsKeyframe, "снимок начался не с ключевого кадра");
    }

    /// <summary>
    /// «Обзор» обещает ровно столько, сколько сохранится: не больше настройки и
    /// считая от первого ключевого кадра, а не от первого попавшегося.
    /// </summary>
    [Fact]
    public void Buffered_duration_never_exceeds_the_limit()
    {
        var buffer = Make(seconds: 2, bitrateBps: 40 * Megabit);

        // Ключевые раз в 3 секунды — реже, чем длина буфера: именно так ведёт себя
        // просевший энкодер, и именно тут длительность раньше уезжала за настройку.
        for (int i = 0; i < 60 * 8; i++)
            buffer.Add(Frame(i * (Second / 60), keyframe: i % 180 == 0, length: 20_000, seed: (byte)i));

        Assert.True(buffer.BufferedDurationTicks <= 2 * Second,
            $"буфер обещает {buffer.BufferedDurationTicks / (double)Second:F1} с при лимите 2");
    }
}
