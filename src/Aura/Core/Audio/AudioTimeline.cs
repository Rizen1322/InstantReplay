namespace Aura.Core.Audio;

/// <summary>
/// Ресемплер с плавно меняющимся коэффициентом: оконный sinc (окно Блэкмана),
/// таблица фаз с линейной интерполяцией.
///
/// Нужен для двух вещей сразу:
/// • привести частоту устройства (44.1, 96, 192 кГц) к 48 кГц;
/// • подстраивать темп на сотые доли процента, когда часы звуковой карты идут
///   чуть быстрее или медленнее часов системы (QPC), по которым идёт видео.
/// Раньше вторую задачу решали выбрасыванием кусков по 120 мс раз в секунду —
/// на слух это щелчки, — а при нехватке данных вставляли тишину посреди звука.
/// </summary>
public sealed class SincResampler
{
    private const int Phases = 256;
    private const int BaseHalfTaps = 16;

    private readonly int _channels;
    private readonly int _halfTaps;
    private readonly float[] _table;      // [Phases + 1][2 * _halfTaps]
    private float[] _history;             // вход, чередование каналов
    private int _historyFrames;
    private double _position;             // позиция следующего выхода внутри _history (кадры)

    public SincResampler(int channels, int inputRate, int outputRate)
    {
        _channels = channels;
        InputRate = inputRate;
        OutputRate = outputRate;
        BaseStep = inputRate / (double)outputRate;

        // При понижении частоты срез опускается ниже новой частоты Найквиста,
        // иначе верх спектра завернётся слышимым призвуком. Фильтр при этом
        // пропорционально длиннее, чтобы крутизна среза осталась прежней.
        double cutoff = Math.Min(1.0, outputRate / (double)inputRate) * 0.94;
        _halfTaps = (int)Math.Ceiling(BaseHalfTaps / Math.Min(1.0, outputRate / (double)inputRate));
        int taps = 2 * _halfTaps;
        _table = new float[(Phases + 1) * taps];
        for (int phase = 0; phase <= Phases; phase++)
        {
            double frac = phase / (double)Phases;
            double sum = 0;
            for (int k = 0; k < taps; k++)
            {
                // Отвод k соответствует входному кадру (центр - halfTaps + 1 + k)
                double t = k - (_halfTaps - 1) - frac;
                double x = t * cutoff;
                double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double w = t / _halfTaps;                         // [-1, 1]
                double window = Math.Abs(w) >= 1 ? 0
                    : 0.42 + 0.5 * Math.Cos(Math.PI * w) + 0.08 * Math.Cos(2 * Math.PI * w);
                double value = cutoff * sinc * window;
                _table[phase * taps + k] = (float)value;
                sum += value;
            }
            // Нормировка: постоянный сигнал проходит без изменения громкости
            for (int k = 0; k < taps; k++) _table[phase * taps + k] = (float)(_table[phase * taps + k] / sum);
        }

        _history = new float[4096 * channels];
        Reset();
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Входных кадров на один выходной без поправки.</summary>
    public double BaseStep { get; }

    /// <summary>Поправка темпа: +0.001 — вход съедается на 0.1% быстрее.</summary>
    public double Correction { get; set; }

    /// <summary>Сколько входных кадров ещё не превращено в выход (дробно).</summary>
    public double PendingInput => _historyFrames - _position;

    public void Reset()
    {
        // Перед началом — тишина длиной в половину фильтра: первый выход приходится
        // ровно на первый входной кадр, без сдвига.
        _historyFrames = _halfTaps - 1;
        Array.Clear(_history, 0, _historyFrames * _channels);
        _position = _halfTaps - 1;
        Correction = 0;
    }

    /// <summary>
    /// Добавить вход и выдать всё, что можно посчитать. Возвращает число кадров выхода,
    /// записанных в <paramref name="output"/> (он растёт при необходимости).
    /// </summary>
    public int Process(ReadOnlySpan<float> input, int frames, ref float[] output)
    {
        Append(input, frames);

        double step = BaseStep * (1 + Correction);
        int taps = 2 * _halfTaps;
        int produced = 0;
        int maxOut = (int)((_historyFrames - _position) / step) + 2;
        if (output.Length < maxOut * _channels) output = new float[maxOut * _channels * 2];

        // Выход в позиции p требует вход до p + halfTaps
        while (_position + _halfTaps < _historyFrames)
        {
            int center = (int)_position;
            double frac = _position - center;
            double phasePos = frac * Phases;
            int phase = (int)phasePos;
            float mix = (float)(phasePos - phase);
            int rowA = phase * taps;
            int rowB = rowA + taps;
            int first = center - (_halfTaps - 1);

            for (int c = 0; c < _channels; c++)
            {
                float accA = 0, accB = 0;
                int idx = first * _channels + c;
                for (int k = 0; k < taps; k++, idx += _channels)
                {
                    float x = _history[idx];
                    accA += x * _table[rowA + k];
                    accB += x * _table[rowB + k];
                }
                output[produced * _channels + c] = accA + (accB - accA) * mix;
            }
            produced++;
            _position += step;
        }

        Compact();
        return produced;
    }

    private void Append(ReadOnlySpan<float> input, int frames)
    {
        int needed = (_historyFrames + frames) * _channels;
        if (_history.Length < needed) Array.Resize(ref _history, Math.Max(needed, _history.Length * 2));
        input[..(frames * _channels)].CopyTo(_history.AsSpan(_historyFrames * _channels));
        _historyFrames += frames;
    }

    /// <summary>Выбросить вход, который уже не попадёт ни в один фильтр.</summary>
    private void Compact()
    {
        int keepFrom = (int)_position - (_halfTaps - 1);
        if (keepFrom <= 0) return;
        int keep = _historyFrames - keepFrom;
        Array.Copy(_history, keepFrom * _channels, _history, 0, keep * _channels);
        _historyFrames = keep;
        _position -= keepFrom;
    }
}

/// <summary>
/// Шкала звука одного источника на часах QPC: кольцо сэмплов 48 кГц, где номер
/// сэмпла однозначно переводится во время видео.
///
/// КАК БЫЛО. Микшер тикал каждые 10 мс по QPC и забирал из буфера устройства
/// сколько есть; время блока просто прибавлялось по 10 мс, а задержка устройства
/// «компенсировалась» константой 25 мс, подобранной на глаз. Часы звуковой карты
/// и часы системы расходятся (обычно на десятки миллионных долей), поэтому буфер
/// либо пустел — и в звук вставлялась тишина, — либо рос, и из него раз в секунду
/// вырезали по 120 мс. На длинной записи звук уплывал от видео и щёлкал.
///
/// КАК СТАЛО. WASAPI для каждого пакета сообщает QPC-время его первого кадра
/// (u64QPCPosition): когда этот звук реально прозвучал или был записан. Пакет
/// кладётся на шкалу ровно туда, где он должен быть. Дрейф часов убирается
/// плавной поправкой темпа ресемплера (сотые доли процента, на слух незаметно),
/// а не вырезанием кусков. Разрыв (устройство молчало, сменилось, пропустило
/// пакеты) — это честная тишина в нужном месте, после которой звук снова ложится
/// точно по времени.
///
/// Потоки: пишет поток захвата, читает поток микшера; оба — под одним замком,
/// работа под ним — только копирование.
/// </summary>
public sealed class AudioTimeline
{
    public const int Rate = 48000;

    /// <summary>Сэмпл N соответствует времени Origin + N * 10^7 / 48000 тиков.</summary>
    public long OriginTicks { get; }

    public int Channels { get; }

    /// <summary>Сколько кадров держит кольцо — с большим запасом над задержкой микшера.</summary>
    public const int CapacityFrames = Rate * 4;

    /// <summary>Расхождение больше этого — не дрейф, а разрыв: кладём звук заново по времени.</summary>
    private const double ResyncFrames = Rate * 0.020;

    /// <summary>
    /// Коэффициент поправки темпа на кадр расхождения. При 1 мс (48 кадров)
    /// поправка ≈ 0.02%: такое расхождение уходит за несколько секунд, а
    /// изменение высоты тона в сотни раз ниже порога слышимости.
    /// </summary>
    private const double Gain = 4e-6;
    private const double MaxCorrection = 0.002;

    private readonly object _sync = new();
    private readonly float[] _ring;
    private long _writePos;
    private SincResampler? _resampler;
    private float[] _scratch = new float[8192];
    private bool _synced;
    private double _smoothedError;

    /// <summary>Сколько раз звук пришлось класть заново (разрыв). Для диагностики.</summary>
    public long Resyncs { get; private set; }

    /// <summary>Текущая поправка темпа — для строки в логе.</summary>
    public double Correction => _resampler?.Correction ?? 0;

    public AudioTimeline(long originTicks, int channels)
    {
        OriginTicks = originTicks;
        Channels = channels;
        _ring = new float[CapacityFrames * channels];
    }

    /// <summary>До какого сэмпла шкала заполнена.</summary>
    public long WritePosition { get { lock (_sync) return _writePos; } }

    /// <summary>
    /// Пакет от устройства: <paramref name="frames"/> кадров в <see cref="Channels"/>
    /// каналах с частотой <paramref name="inputRate"/>; первый кадр прозвучал в
    /// момент <paramref name="qpcTicks"/>.
    /// </summary>
    public void Push(ReadOnlySpan<float> samples, int frames, int inputRate, long qpcTicks, bool discontinuity)
    {
        if (frames <= 0) return;
        lock (_sync)
        {
            if (_resampler is null || _resampler.InputRate != inputRate)
            {
                _resampler = new SincResampler(Channels, inputRate, Rate);
                _synced = false;
            }

            double target = (qpcTicks - OriginTicks) * (double)Rate / 10_000_000;
            if (_synced && !discontinuity)
            {
                // Куда лёг бы первый кадр пакета, если писать подряд
                double step = _resampler.BaseStep * (1 + _resampler.Correction);
                double predicted = _writePos + _resampler.PendingInput / step;
                double error = predicted - target;
                if (Math.Abs(error) > ResyncFrames)
                {
                    Resync(target);
                }
                else
                {
                    // Сглаживаем: метки времени пакетов слегка дрожат, и без этого
                    // темп дёргался бы на каждом пакете.
                    _smoothedError += (error - _smoothedError) * 0.05;
                    _resampler.Correction = Math.Clamp(_smoothedError * Gain, -MaxCorrection, MaxCorrection);
                }
            }
            else Resync(target);

            int produced = _resampler.Process(samples, frames, ref _scratch);
            WriteLocked(_scratch.AsSpan(0, produced * Channels), produced);
        }
    }

    private void Resync(double target)
    {
        long position = (long)Math.Round(target);
        if (_synced) Resyncs++;
        // Вперёд — промежуток становится тишиной; назад — поверх старого.
        if (position > _writePos)
        {
            long from = Math.Max(_writePos, position - CapacityFrames);
            for (long p = from; p < position; p++)
                Array.Clear(_ring, (int)(Mod(p) * Channels), Channels);
        }
        _writePos = position;
        _resampler!.Reset();
        _smoothedError = 0;
        _synced = true;
    }

    private void WriteLocked(ReadOnlySpan<float> data, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            int slot = (int)Mod(_writePos + i) * Channels;
            data.Slice(i * Channels, Channels).CopyTo(_ring.AsSpan(slot, Channels));
        }
        _writePos += frames;
    }

    /// <summary>
    /// Прочитать <paramref name="frames"/> кадров с позиции <paramref name="position"/>.
    /// Чего на шкале нет (ещё не пришло, уже вытеснено или устройство молчало) —
    /// тишина.
    /// </summary>
    public void Read(long position, Span<float> dest, int frames)
    {
        lock (_sync)
        {
            long validFrom = _writePos - CapacityFrames;
            for (int i = 0; i < frames; i++)
            {
                long p = position + i;
                var target = dest.Slice(i * Channels, Channels);
                if (!_synced || p >= _writePos || p < validFrom) target.Clear();
                else _ring.AsSpan((int)Mod(p) * Channels, Channels).CopyTo(target);
            }
        }
    }

    private static long Mod(long p) => ((p % CapacityFrames) + CapacityFrames) % CapacityFrames;
}
