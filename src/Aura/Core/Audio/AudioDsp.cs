namespace Aura.Core.Audio;

/// <summary>
/// Пиковый лимитер с просмотром вперёд.
///
/// ЗАЧЕМ. Раньше звук игры и микрофона складывались с жёстким обрезанием по краям
/// шкалы: громкая сцена плюс голос — и вершины волны срезались, на слух это хрип.
/// Лимитер заранее видит пик (задержка в 2 мс — на записи она незаметна: шкала
/// звука и так идёт с задержкой, а время блоков от этого не меняется) и плавно
/// опускает громкость ровно настолько, чтобы пик прошёл, потом так же плавно
/// возвращает её. Жёсткое ограничение осталось только последней страховкой.
///
/// Задержка лимитера одинакова для всех дорожек и сдвигает их одинаково — на
/// 96 сэмплов. Это 2 мс, меньше одного кадра видео, поправка не нужна.
/// </summary>
public sealed class Limiter
{
    private const float Threshold = 0.966f;     // −0.3 дБFS
    public const int LookaheadFrames = 96;      // 2 мс при 48 кГц

    private readonly int _channels;
    private readonly float[] _delay;            // кольцо задержки, чередование каналов
    private int _delayPos;
    private readonly float[] _required = new float[LookaheadFrames]; // нужное усиление по кадрам окна
    private int _requiredPos;
    private float _gain = 1f;
    private readonly float _attack;
    private readonly float _release;

    /// <summary>Самое глубокое снижение с прошлого чтения (для лога), 1 — не срабатывал.</summary>
    public float MinGain { get; set; } = 1f;

    public Limiter(int channels, int sampleRate = AudioTimeline.Rate)
    {
        _channels = channels;
        _delay = new float[LookaheadFrames * channels];
        Array.Fill(_required, 1f);
        _attack = 1f - MathF.Exp(-4f / LookaheadFrames);          // успевает за окно просмотра
        _release = 1f - MathF.Exp(-1f / (0.150f * sampleRate));   // 150 мс
    }

    /// <summary>Обработать блок на месте.</summary>
    public void Process(Span<float> samples, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            int at = f * _channels;
            float peak = 0;
            for (int c = 0; c < _channels; c++)
            {
                float a = MathF.Abs(samples[at + c]);
                if (a > peak) peak = a;
            }
            _required[_requiredPos] = peak > Threshold ? Threshold / peak : 1f;
            _requiredPos = (_requiredPos + 1) % LookaheadFrames;

            // Цель — самое сильное снижение, которое понадобится в окне впереди
            float target = 1f;
            for (int i = 0; i < LookaheadFrames; i++)
                if (_required[i] < target) target = _required[i];

            _gain += (target - _gain) * (target < _gain ? _attack : _release);
            if (_gain < MinGain) MinGain = _gain;

            int slot = _delayPos * _channels;
            for (int c = 0; c < _channels; c++)
            {
                float delayed = _delay[slot + c];
                _delay[slot + c] = samples[at + c];
                float y = delayed * _gain;
                samples[at + c] = y > 1f ? 1f : y < -1f ? -1f : y;
            }
            _delayPos = (_delayPos + 1) % LookaheadFrames;
        }
    }
}

/// <summary>
/// Шумовой гейт микрофона: два порога с зазором и удержание (перенесён из прежнего
/// микшера без изменения поведения).
/// </summary>
public sealed class NoiseGate
{
    private const float HysteresisDb = 6f;
    private const int HoldBlocks = 25;

    private float _envelope;
    private int _hold;

    public float Process(ReadOnlySpan<float> block, float thresholdDb)
    {
        double sum = 0;
        foreach (float v in block) sum += v * v;
        float rms = (float)Math.Sqrt(sum / Math.Max(1, block.Length));

        float clamped = Math.Clamp(thresholdDb, -70f, -10f);
        float openLevel = MathF.Pow(10, clamped / 20f);
        float closeLevel = MathF.Pow(10, (clamped - HysteresisDb) / 20f);

        if (rms > openLevel) _hold = HoldBlocks;
        else if (rms < closeLevel) _hold = Math.Max(0, _hold - 1);

        float target = _hold > 0 ? 1f : 0f;
        float coef = target > _envelope ? 0.6f : 0.06f;
        _envelope += (target - _envelope) * coef;
        return _envelope;
    }
}
