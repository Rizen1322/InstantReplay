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
    private sealed class DownmixToStereoSampleProvider : ISampleProvider
    {
        private const float Attenuated = 0.707f;   // −3 дБ

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
            // Фронт есть всегда — с него и начинаем
            _left[0] = 1f;
            if (_sourceChannels > 1) _right[1] = 1f;

            for (int ch = 2; ch < _sourceChannels; ch++)
                switch (ch)
                {
                    case 2:                                  // FC — поровну в обе стороны
                        _left[ch] = _right[ch] = Attenuated;
                        break;
                    case 3:                                  // LFE — намеренно мимо микса
                        break;
                    default:
                        // Дальше идут парами: тыловые, затем боковые. Чётный — слева.
                        if ((ch & 1) == 0) _left[ch] = Attenuated;
                        else _right[ch] = Attenuated;
                        break;
                }
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
