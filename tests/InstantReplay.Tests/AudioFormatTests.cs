using Aura.Core.Audio;
using NAudio.Wave;
using Xunit;

namespace InstantReplay.Tests;

/// <summary>
/// Приведение любого устройства к 48 кГц / 2 канала.
///
/// ЖИВОЙ СЛУЧАЙ. Гарнитура, отдающая loopback восемью каналами (7.1), ломала
/// запись: конвейер оставался восьмиканальным, а микшер читал его как стерео —
/// то есть за «блок на 480 кадров» брал 120 настоящих кадров и растягивал их
/// вчетверо. На слух это писк, а если контент лежал не во фронтальных каналах —
/// тишина. На стереоустройстве того же человека всё работало.
/// </summary>
public class AudioFormatTests
{
    private const int Rate = 48000;

    [Fact]
    public void ВосьмиканальныйИсточникСводитсяВСтерео()
    {
        var source = new FakeSource(channels: 8, sampleRate: Rate);
        var normalized = AudioFormat.Normalize(source);

        Assert.Equal(2, normalized.WaveFormat.Channels);
        Assert.Equal(Rate, normalized.WaveFormat.SampleRate);
    }

    [Fact]
    public void БлокСтереоСэмпловТратитСтолькоЖеКадровИсточника()
    {
        // Суть бага: запрос 960 сэмплов (480 кадров стерео) обязан взять из
        // источника 480 кадров, а не 120. Иначе звук идёт вчетверо быстрее.
        var source = new FakeSource(channels: 8, sampleRate: Rate);
        var normalized = AudioFormat.Normalize(source);

        var buffer = new float[480 * 2];
        int read = normalized.Read(buffer, 0, buffer.Length);

        Assert.Equal(buffer.Length, read);
        Assert.Equal(480, source.FramesRead);
    }

    [Fact]
    public void ФронтальныеКаналыПопадаютВСвоюСторону()
    {
        // 7.1: FL FR C LFE SL SR BL BR. Кладём сигнал только во фронт —
        // слева обязан остаться левый, справа правый, без перемешивания.
        var source = new FakeSource(channels: 8, sampleRate: Rate,
            perChannel: [1.0f, -1.0f, 0f, 0f, 0f, 0f, 0f, 0f]);
        var normalized = AudioFormat.Normalize(source);

        var buffer = new float[2 * 2];
        normalized.Read(buffer, 0, buffer.Length);

        Assert.Equal(1.0f, buffer[0], 3);
        Assert.Equal(-1.0f, buffer[1], 3);
    }

    [Fact]
    public void ЦентрРасходитсяПоровнуМеждуКаналами()
    {
        // Центральный канал по BS.775 идёт в обе стороны с коэффициентом 0.707.
        // Если его выбросить, в записи пропадут голоса и диалоги.
        var source = new FakeSource(channels: 6, sampleRate: Rate,
            perChannel: [0f, 0f, 1.0f, 0f, 0f, 0f]);
        var normalized = AudioFormat.Normalize(source);

        var buffer = new float[2];
        normalized.Read(buffer, 0, buffer.Length);

        Assert.Equal(0.707f, buffer[0], 3);
        Assert.Equal(0.707f, buffer[1], 3);
    }

    [Fact]
    public void НизкочастотныйКаналНеПопадаетВМикс()
    {
        // LFE в стереосведении не участвует — иначе бас перегружает микс.
        var source = new FakeSource(channels: 6, sampleRate: Rate,
            perChannel: [0f, 0f, 0f, 1.0f, 0f, 0f]);
        var normalized = AudioFormat.Normalize(source);

        var buffer = new float[2];
        normalized.Read(buffer, 0, buffer.Length);

        Assert.Equal(0f, buffer[0], 3);
        Assert.Equal(0f, buffer[1], 3);
    }

    [Fact]
    public void КвадроНеТеряетТыловойКанал()
    {
        // QUAD — это FL FR BL BR, без центра и без LFE. Раскладка «индекс 2 = центр,
        // индекс 3 = LFE» тут неверна: по ней тыл слева размазывался в оба канала,
        // а тыл справа выбрасывался целиком.
        var source = new FakeSource(channels: 4, sampleRate: Rate,
            perChannel: [0f, 0f, 0f, 1.0f]);
        var normalized = AudioFormat.Normalize(source);

        var buffer = new float[2];
        normalized.Read(buffer, 0, buffer.Length);

        Assert.True(buffer[0] > 0 || buffer[1] > 0, "тыловой правый канал потерялся при сведении");
    }

    [Fact]
    public void НизкочастотныйКаналВТрёхканальномНеИдётВМикс()
    {
        // 2POINT1 — это FL FR LFE. Принимать третий канал за центр значит гнать
        // бас в обе стороны с коэффициентом 0.707 и перегружать микс.
        var source = new FakeSource(channels: 3, sampleRate: Rate,
            perChannel: [0f, 0f, 1.0f]);
        var normalized = AudioFormat.Normalize(source);

        var buffer = new float[2];
        normalized.Read(buffer, 0, buffer.Length);

        Assert.Equal(0f, buffer[0], 3);
        Assert.Equal(0f, buffer[1], 3);
    }

    [Fact]
    public void МонофоническийИсточникПревращаетсяВСтерео()
    {
        var source = new FakeSource(channels: 1, sampleRate: Rate, perChannel: [0.5f]);
        var normalized = AudioFormat.Normalize(source);

        Assert.Equal(2, normalized.WaveFormat.Channels);

        var buffer = new float[2];
        normalized.Read(buffer, 0, buffer.Length);
        Assert.Equal(0.5f, buffer[0], 3);
        Assert.Equal(0.5f, buffer[1], 3);
    }

    [Fact]
    public void ЧастотаПриводитсяК48кГц()
    {
        var source = new FakeSource(channels: 2, sampleRate: 44100);
        var normalized = AudioFormat.Normalize(source);

        Assert.Equal(Rate, normalized.WaveFormat.SampleRate);
        Assert.Equal(2, normalized.WaveFormat.Channels);
    }

    [Fact]
    public void ВосемьКаналовНа44кГцПриводятсяПоОбоимПризнакам()
    {
        var source = new FakeSource(channels: 8, sampleRate: 44100);
        var normalized = AudioFormat.Normalize(source);

        Assert.Equal(Rate, normalized.WaveFormat.SampleRate);
        Assert.Equal(2, normalized.WaveFormat.Channels);
    }

    [Fact]
    public void ПодходящийИсточникНеОборачиваетсяЛишнимиПреобразованиями()
    {
        var source = new FakeSource(channels: 2, sampleRate: Rate);
        Assert.Same(source, AudioFormat.Normalize(source));
    }

    /// <summary>
    /// Источник с заданным числом каналов. Каждый кадр отдаёт одни и те же
    /// значения по каналам и считает, сколько кадров у него забрали.
    /// </summary>
    private sealed class FakeSource : ISampleProvider
    {
        private readonly float[] _perChannel;

        public FakeSource(int channels, int sampleRate, float[]? perChannel = null)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _perChannel = perChannel ?? new float[channels];
            Assert.Equal(channels, _perChannel.Length);
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>Сколько кадров источника прочитали — по нему видно растяжение времени.</summary>
        public int FramesRead { get; private set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            for (int i = 0; i < count; i++)
                buffer[offset + i] = _perChannel[i % channels];

            FramesRead += count / channels;
            return count;
        }
    }
}
