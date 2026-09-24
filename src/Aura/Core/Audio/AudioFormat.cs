using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Aura.Core.Audio;

/// <summary>
/// Приведение любого звукового устройства к формату конвейера: 48 кГц, 2 канала, float.
///
/// ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. Здесь нет ни WASAPI, ни устройств — чистая работа над
/// <see cref="ISampleProvider"/>. Благодаря этому её проверяют тесты на синтетическом
/// источнике: завести в тесте настоящую гарнитуру 7.1 нельзя, а поймать ошибку нужно.
///
/// ИСТОРИЯ. Раньше приведение состояло из двух строк: моно превращалось в стерео,
/// частота приводилась к 48 кГц. Случай «каналов больше двух» не обрабатывался вовсе,
/// хотя комментарий обещал стерео. Гарнитура с loopback на 8 каналов (7.1) оставляла
/// конвейер восьмиканальным, а микшер читал его как стерео: на блок в 480 кадров
/// брал 120 настоящих и растягивал вчетверо. На слух — писк; если звук лежал не во
/// фронтальных каналах — тишина. У владельцев стереоустройств всё работало.
/// </summary>
internal static class AudioFormat
{
    public const int TargetSampleRate = 48000;
    public const int TargetChannels = 2;

    /// <summary>
    /// Обернуть источник так, чтобы на выходе было ровно 48 кГц и 2 канала.
    /// Подходящий источник возвращается как есть — лишних преобразований не ставим.
    /// </summary>
    public static ISampleProvider Normalize(ISampleProvider source)
    {
        // Сначала каналы, потом частота: ресемплить два канала вместо восьми дешевле
        ISampleProvider result = source.WaveFormat.Channels switch
        {
            TargetChannels => source,
            1 => new MonoToStereoSampleProvider(source),
            _ => new DownmixToStereoSampleProvider(source)
        };

        if (result.WaveFormat.SampleRate != TargetSampleRate)
            result = new WdlResamplingSampleProvider(result, TargetSampleRate);

        return result;
    }

    /// <summary>
    /// Сведение многоканального звука в стерео по ITU-R BS.775.
    ///
    /// L = FL + 0.707·FC + 0.707·(тыл и бок слева)
    /// R = FR + 0.707·FC + 0.707·(тыл и бок справа)
    ///
    /// Низкочастотный канал (LFE) в сведении не участвует — так делает и вещание,
    /// и звуковые редакторы: иначе бас перегружает микс. Центральный, наоборот,
    /// обязателен: в играх в нём голоса и диалоги, и выбросить его — потерять речь.
    ///
    /// Порядок каналов берётся стандартный для WAVE_FORMAT_EXTENSIBLE:
    /// FL FR FC LFE BL BR SL SR. Именно в таком порядке отдаёт кадры WASAPI.
    /// </summary>
    /// <summary>
    /// Вклад каждого канала устройства в левый и правый канал стерео. Этой же
    /// матрицей пользуется захват WASAPI (<see cref="WasapiSource"/>).
    /// </summary>
    public static (float[] Left, float[] Right) StereoMatrix(int sourceChannels)
    {
        const float Attenuated = 0.707f;   // −3 дБ
        var left = new float[sourceChannels];
        var right = new float[sourceChannels];

        if (sourceChannels == 1)
        {
            left[0] = right[0] = 1f;       // моно — одинаково в обе стороны
            return (left, right);
        }

        // Фронт есть всегда — с него и начинаем
        left[0] = 1f;
        right[1] = 1f;

        // Центр на индексе 2 и LFE на индексе 3 — это раскладка 5.1 и 7.1
        // (KSAUDIO_SPEAKER_5POINT1_SURROUND / 7POINT1_SURROUND). Применять её
        // ко всему подряд нельзя: в QUAD (FL FR BL BR) индекс 2 — это тыл слева,
        // а индекс 3 — тыл справа, и «выбросить LFE» означало потерять целый
        // канал. В 2.1 (FL FR LFE) индекс 2 — наоборот низкочастотный, и
        // разводить его по сторонам как центр значит перегрузить микс басом.
        bool hasCenterAndLfe = sourceChannels is 6 or 8;

        for (int ch = 2; ch < sourceChannels; ch++)
        {
            if (hasCenterAndLfe && ch == 2)              // FC — поровну в обе стороны
            {
                left[ch] = right[ch] = Attenuated;
                continue;
            }
            if (ch == 3 && hasCenterAndLfe) continue;    // LFE — намеренно мимо микса

            // 2.1: третий канал низкочастотный, в стерео ему делать нечего
            if (sourceChannels == 3) continue;

            // Остальное идёт парами (тыл, затем бок): чётный — слева, нечётный — справа
            if ((ch & 1) == 0) left[ch] = Attenuated;
            else right[ch] = Attenuated;
        }
        return (left, right);
    }

    private sealed class DownmixToStereoSampleProvider : ISampleProvider
    {

        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private readonly float[] _left;   // вклад каждого канала источника в левый
        private readonly float[] _right;
        private float[] _scratch = [];

        public DownmixToStereoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _sourceChannels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate, TargetChannels);

            _left = new float[_sourceChannels];
            _right = new float[_sourceChannels];
            BuildMatrix();
        }

        public WaveFormat WaveFormat { get; }

        private void BuildMatrix()
        {
            var (left, right) = StereoMatrix(_sourceChannels);
            left.CopyTo(_left, 0);
            right.CopyTo(_right, 0);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / TargetChannels;
            int needed = frames * _sourceChannels;
            if (_scratch.Length < needed) _scratch = new float[needed];

            int got = _source.Read(_scratch, 0, needed);
            int gotFrames = got / _sourceChannels;

            for (int f = 0; f < gotFrames; f++)
            {
                int src = f * _sourceChannels;
                float l = 0f, r = 0f;
                for (int ch = 0; ch < _sourceChannels; ch++)
                {
                    float sample = _scratch[src + ch];
                    l += sample * _left[ch];
                    r += sample * _right[ch];
                }

                // Сумма каналов способна выйти за пределы; ограничиваем здесь, пока
                // это ещё float — иначе переполнение придёт в целочисленный микшер
                // уже как треск.
                buffer[offset + f * TargetChannels] = Math.Clamp(l, -1f, 1f);
                buffer[offset + f * TargetChannels + 1] = Math.Clamp(r, -1f, 1f);
            }

            return gotFrames * TargetChannels;
        }
    }
}
