using Aura.Core.Buffering;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Кольцевой буфер звука и сведение дорожек.
///
/// Раньше здесь не было ни одного теста, а буфер хранил блоки ссылками на массивы,
/// которые микшер выделял заново каждые 10 мс. Теперь микшер массивы ПЕРЕИСПОЛЬЗУЕТ,
/// и весь контракт держится на том, что буфер копирует данные себе — это и проверяем
/// в первую очередь.
/// </summary>
public class AudioBufferTests
{
    private const int Samples = 4;   // блок из четырёх сэмплов: считать глазами проще
    private const long Second = 10_000_000;

    // ---------------- Запас кольца ----------------

    [Fact]
    public void ЗвукХранитсяДольшеЗаказанного_ЧтобыКлипНеНачиналсяСТишины()
    {
        // Снимок видео начинается с ПОСЛЕДНЕГО keyframe перед точкой среза, то есть
        // раньше заказанной границы — на длину группы кадров. Если звук хранит ровно
        // заказанное плюс секунду, этих первых секунд клипа в нём просто нет, и запись
        // начинается с тишины. Запас звука обязан быть не меньше, чем у видео.
        var buffer = new ReplayAudioBuffer();
        buffer.Allocate(blockSamples: Samples, seconds: 10);
        buffer.MaxDurationTicks = TimeSpan.FromSeconds(10).Ticks;

        // Кладём 25 секунд подряд: кольцо обязано удержать заметно больше десяти
        for (int i = 0; i < 25 * 100; i++)
            buffer.Add(new AudioBlock(Filled(1), Filled(2), i * 100_000L));

        var all = buffer.Snapshot(0, long.MaxValue);
        long held = all.Count > 1 ? all.PtsAt(all.Count - 1) - all.PtsAt(0) : 0;

        Assert.True(held >= TimeSpan.FromSeconds(20).Ticks,
            $"кольцо держит всего {TimeSpan.FromTicks(held).TotalSeconds:F1} с — начало клипа останется без звука");
    }

    // ---------------- Владение данными ----------------

    [Fact]
    public void БуферКопируетДанныеАНеДержитМассивМикшера()
    {
        var buffer = NewBuffer(maxSeconds: 10);

        // Микшер отдаёт свои рабочие массивы — те же самые от блока к блоку
        var game = Filled(111);
        var mic = Filled(222);
        buffer.Add(new AudioBlock(game, mic, 0));

        // ...и тут же перезаписывает их под следующий блок
        Array.Fill(game, (short)999);
        Array.Fill(mic, (short)888);

        var snapshot = buffer.Snapshot(0, 0);
        Assert.Equal(1, snapshot.Count);
        Assert.Equal(Repeat(111), Track(snapshot, 0, AudioTrackKind.Game));
        Assert.Equal(Repeat(222), Track(snapshot, 0, AudioTrackKind.Mic));
    }

    [Fact]
    public void СнимокПереживаетДальнейшуюЗапись()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        buffer.Add(new AudioBlock(Filled(111), Filled(222), 0));

        var snapshot = buffer.Snapshot(0, 0);

        // Кольцо продолжает крутиться, пока файл пишется в фоне
        for (int i = 1; i < 500; i++)
            buffer.Add(new AudioBlock(Filled(7), Filled(7), i * 100_000L));

        Assert.Equal(Repeat(111), Track(snapshot, 0, AudioTrackKind.Game));
    }

    // ---------------- Границы кольца ----------------

    [Fact]
    public void КольцоНеРастётСверхЁмкости()
    {
        // Вытеснение по времени отключаем большим MaxDuration — интересует предел
        // арены. Конкретную ёмкость не проверяем: она считается из запаса, общего
        // с видеобуфером, и привязывать тест к её значению значит ломать его на
        // каждой настройке запаса. Контракт же в другом: кольцо ограничено и
        // хранит именно ПОСЛЕДНИЕ блоки, подряд.
        const int added = 5000;
        var buffer = new ReplayAudioBuffer { MaxDurationTicks = 3600 * Second };
        buffer.Allocate(Samples, seconds: 1);

        for (int i = 0; i < added; i++)
            buffer.Add(new AudioBlock(Filled((short)i), Filled(0), i));

        var snapshot = buffer.Snapshot(0, long.MaxValue);

        Assert.True(snapshot.Count < added, "кольцо выросло сверх арены");
        Assert.Equal(added - 1, snapshot.PtsAt(snapshot.Count - 1));          // новейший на месте
        Assert.Equal(added - snapshot.Count, snapshot.PtsAt(0));              // окно непрерывно
    }

    [Fact]
    public void СтароеВытесняетсяПоВремени()
    {
        // Держим заказанное плюс запас. Точную величину запаса тест не знает —
        // проверяем сам факт: далёкое прошлое уходит, а заказанный интервал цел.
        const int seconds = 60;
        var buffer = NewBuffer(maxSeconds: 1);

        for (int i = 0; i <= seconds; i++)
            buffer.Add(new AudioBlock(Filled(1), Filled(1), i * Second));

        var snapshot = buffer.Snapshot(0, long.MaxValue);

        Assert.True(snapshot.Count <= seconds, "ничего не вытеснилось за минуту");
        Assert.True(snapshot.PtsAt(0) > 0, "блок из далёкого прошлого остался в кольце");
        Assert.Equal(seconds * Second, snapshot.PtsAt(snapshot.Count - 1));   // свежий цел
    }

    // ---------------- Снимок ----------------

    [Fact]
    public void СнимокБерётТолькоЗаказанныйИнтервал()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        for (int i = 0; i < 10; i++)
            buffer.Add(new AudioBlock(Filled((short)i), Filled(0), i * 100_000L));

        var snapshot = buffer.Snapshot(300_000L, 600_000L);

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(300_000L, snapshot.PtsAt(0));
        Assert.Equal(600_000L, snapshot.PtsAt(3));
    }

    [Fact]
    public void ПустойБуферОтдаётПустойСнимок()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        Assert.Equal(0, buffer.Snapshot(0, long.MaxValue).Count);

        // И до Allocate тоже — конвейер ещё не стартовал
        Assert.Equal(0, new ReplayAudioBuffer().Snapshot(0, long.MaxValue).Count);
    }

    [Fact]
    public void ИнтервалМимоДанныхДаётПустойСнимок()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        buffer.Add(new AudioBlock(Filled(1), Filled(1), 5 * Second));

        Assert.Equal(0, buffer.Snapshot(0, Second).Count);
    }

    // ---------------- Время жизни арены ----------------

    [Fact]
    public void ClearСохраняетАренуAReleaseОтпускает()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        buffer.Add(new AudioBlock(Filled(1), Filled(1), 0));
        Assert.True(buffer.TotalBytes > 0);

        // После сохранения повтора буфер копит заново, но конвейер работает —
        // арену отпускать нельзя, иначе звук перестал бы писаться до перезапуска
        buffer.Clear();
        Assert.Equal(0, buffer.TotalBytes);
        buffer.Add(new AudioBlock(Filled(5), Filled(5), Second));
        Assert.Equal(1, buffer.Snapshot(0, long.MaxValue).Count);

        // А на остановке конвейера арена возвращается системе
        buffer.Release();
        buffer.Add(new AudioBlock(Filled(5), Filled(5), 2 * Second));
        Assert.Equal(0, buffer.Snapshot(0, long.MaxValue).Count);
    }

    // ---------------- Сведение дорожек ----------------

    [Fact]
    public void ДорожкиВыбираютсяБезАллокаций()
    {
        var block = new AudioBlock(Filled(100), Filled(200), 0);
        var dest = new short[Samples];

        block.CopyTo(AudioTrackKind.Game, dest);
        Assert.Equal(Repeat(100), dest);

        block.CopyTo(AudioTrackKind.Mic, dest);
        Assert.Equal(Repeat(200), dest);

        block.CopyTo(AudioTrackKind.Mixed, dest);
        Assert.Equal(Repeat(300), dest);
    }

    [Fact]
    public void СуммаДорожекЗажимаетсяАНеПереполняется()
    {
        // Переполнение short слышно как треск — самая слышимая ошибка сведения
        var loud = new AudioBlock(Filled(30000), Filled(30000), 0);
        var dest = new short[Samples];
        loud.CopyTo(AudioTrackKind.Mixed, dest);
        Assert.Equal(Repeat(short.MaxValue), dest);

        var quiet = new AudioBlock(Filled(-30000), Filled(-30000), 0);
        quiet.CopyTo(AudioTrackKind.Mixed, dest);
        Assert.Equal(Repeat(short.MinValue), dest);
    }

    [Fact]
    public void СнимокСводитДорожкиТакЖеКакБлок()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        buffer.Add(new AudioBlock(Filled(1000), Filled(-400), 0));

        Assert.Equal(Repeat(600), Track(buffer.Snapshot(0, 0), 0, AudioTrackKind.Mixed));
    }

    // ---------------- Ради чего всё затевалось ----------------

    /// <summary>
    /// Приём блока не выделяет ни байта.
    ///
    /// Это и есть смысл арены: Add зовётся сто раз в секунду всё время работы
    /// буфера. Раньше на каждый вызов приходилась пара свежих массивов от микшера,
    /// и трёхминутный буфер держал их живыми десятками тысяч — во втором поколении
    /// кучи, ровно там, где сборка мусора даёт паузы конвейеру записи.
    /// </summary>
    [Fact]
    public void ПриёмБлоковНеВыделяетПамять()
    {
        var buffer = NewBuffer(maxSeconds: 10);
        var game = Filled(1);
        var mic = Filled(2);

        // Меряем УСТАНОВИВШЕЕСЯ состояние. Разового прогрева мало: на первом прогоне
        // после сборки метод ещё проходит уровни JIT, и само повышение уровня
        // аллоцирует внутри измеряемого цикла — тест падал случайно, без регрессии.
        long allocated = -1;
        long pts = 0;
        for (int attempt = 0; attempt < 5 && allocated != 0; attempt++)
        {
            for (int i = 0; i < 10_000; i++) buffer.Add(new AudioBlock(game, mic, pts++));

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) buffer.Add(new AudioBlock(game, mic, pts++));
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.True(allocated == 0, $"десять тысяч блоков выделили {allocated} байт");
    }

    /// <summary>
    /// Сведение дорожек не выделяет ни байта.
    ///
    /// Прежний селектор возвращал МАССИВ, то есть создавал его на каждый блок —
    /// и в обычной записи это происходило на потоке микшера с приоритетом Highest.
    /// </summary>
    [Fact]
    public void СведениеДорожекНеВыделяетПамять()
    {
        var block = new AudioBlock(Filled(100), Filled(200), 0);
        var dest = new short[Samples];

        // Меряем УСТАНОВИВШЕЕСЯ состояние. На первом прогоне после сборки метод ещё
        // проходит уровни JIT, и само повышение уровня аллоцирует внутри измеряемого
        // цикла — тест падал случайно, примерно раз на запуск, без всякой регрессии.
        // Настоящий контракт: рано или поздно сведение перестаёт выделять память
        // и больше не начинает.
        long allocated = -1;
        for (int attempt = 0; attempt < 5 && allocated != 0; attempt++)
        {
            for (int i = 0; i < 10_000; i++) block.CopyTo(AudioTrackKind.Mixed, dest);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++) block.CopyTo(AudioTrackKind.Mixed, dest);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.True(allocated == 0, $"десять тысяч сведений выделили {allocated} байт");
    }

    // ---------------- Вспомогательное ----------------

    private static ReplayAudioBuffer NewBuffer(int maxSeconds)
    {
        var buffer = new ReplayAudioBuffer { MaxDurationTicks = maxSeconds * Second };
        buffer.Allocate(Samples, maxSeconds);
        return buffer;
    }

    private static short[] Filled(short value)
    {
        var array = new short[Samples];
        Array.Fill(array, value);
        return array;
    }

    private static short[] Repeat(int value) => Filled((short)value);

    private static short[] Track(AudioSnapshot snapshot, int index, AudioTrackKind kind)
    {
        var dest = new short[Samples];
        snapshot.CopyTo(index, kind, dest);
        return dest;
    }
}
